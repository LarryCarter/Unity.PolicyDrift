using CVIS.Unity.Core.Entities;
using CVIS.Unity.Core.Extensions;
using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Core.Models;
using CVIS.Unity.Core.Monitoring;
using CVIS.Unity.Infrastructure.Data;
using CVIS.Unity.PolicyDrift.Orchestration.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Text;

namespace CVIS.Unity.PolicyDrift.Orchestrator.Workflows
{
    public class PlatformBatchOrchestrator : PolicyWorkflowBase
    {
        private readonly PolicyDbContext _db;
        private readonly IFileProcessor _fileProcessor;
        private readonly ISignalFileService _signalFiles;

        public PlatformBatchOrchestrator(
            IFileSystemService fileSystem,
            IUnityEventPublisher publisher,
            IPolicyDriftPathProvider driftPath,
            IFileProcessor fileProcessor,
            ISignalFileService signalFiles,
            PolicyDbContext db)
            : base(fileSystem, publisher, driftPath)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _fileProcessor = fileProcessor ?? throw new ArgumentNullException(nameof(fileProcessor));
            _signalFiles = signalFiles ?? throw new ArgumentNullException(nameof(signalFiles));
        }

        public override string WorkflowName => "CyberArk Platform Batch";

        /// <summary>
        /// Entry point — overrides base to run the batch pipeline.
        /// </summary>
        public override async Task ExecuteAsync()
        {
            await RunBatchRefineryAsync();
        }

        public async Task RunBatchRefineryAsync()
        {
            // ── 1. Build Context ─────────────────────────────────────
            //    Config validation, root folder assurance, path derivation,
            //    execution ID — all handled by the provider.
            //    If config is missing, Kafka already fired before this returns.
            var result = await _driftPath.BuildDriftContextAsync();

            if (!result.IsValid)
            {
                // Already logged + Kafka-alerted inside the provider.
                return;
            }

            var ctx = result.Context!;
            _publisher.LogInfo($"[BATCH] Starting {WorkflowName} | Execution: {ctx.ExecutionId}");

            // ── 2. Source Gate ────────────────────────────────────────
            if (!_fileSystem.DirectoryExists(ctx.SourcePath))
            {
                _publisher.LogWarning(
                    $"[BATCH] {ctx.ExecutionId} | Source zone {ctx.SourcePath} is empty. Standing down.");
                return;
            }

            // ── 3. Staging ───────────────────────────────────────────
            //    Only create Processing/Processed now that we know there's work.
            _driftPath.EnsureStagingDirectories(ctx);

            // ── 4. Atomic Processing Loop ────────────────────────────
            var zips = _fileSystem.GetFilesInDirectory(ctx.SourcePath, "*.zip");
            foreach (var zipPath in zips)
            {
                var fileName = Path.GetFileName(zipPath);
                var stagingPath = Path.Combine(ctx.ProcessingPath, fileName);
                var policyId = Path.GetFileNameWithoutExtension(zipPath);

                try
                {
                    _fileSystem.MoveFile(zipPath, stagingPath);
                    await ProcessSinglePlatform(policyId, stagingPath, ctx);
                }
                catch (Exception ex)
                {
                    _publisher.LogError($"[SKIP] Failed to stage {policyId}.", ex);
                    continue;
                }
            }

            // ── 5. Report & Cleanup ──────────────────────────────────
            await GenerateBatchReport(ctx.ExecutionId);

            if (_fileSystem.DirectoryExists(ctx.ProcessingPath))
            {
                _fileSystem.DeleteDirectory(ctx.ProcessingPath, recursive: true);
                _publisher.LogInfo($"[CLEANUP] Workspace cleared for {ctx.DateStamp}.");
            }
        }

        // ─────────────────────────────────────────────────────────
        //  Single Platform Pipeline
        // ─────────────────────────────────────────────────────────

        private async Task ProcessSinglePlatform(
            string policyId,
            string stagePath,
            PolicyDriftContext ctx)
        {
            _publisher.LogInfo($"[PROCESS] Beginning analysis for {policyId}");

            // 1. Memory Extraction & Parsing
            using var zipStream = _fileSystem.OpenRead(stagePath);
            var discovery = await _fileProcessor.ExtractAndParseZipWithHashesAsync(zipStream, policyId);

            // 2. Baseline Gate — signal file check uses the context's baseline folder
            if (_signalFiles.Exists(ctx.BaselineFolder, policyId))
            {
                await HandleBaselinePromotion(policyId, discovery);
                _signalFiles.TryDelete(ctx.BaselineFolder, policyId);
                _publisher.LogInfo($"[BASELINE] Promoted new Baseline for {policyId}. Signal cleared.");
            }

            // 3. Audit Engine
            await ExecuteAuditAsync(policyId, discovery, ctx.ExecutionId);

            // 4. Archive: Processing → Processed
            var finalArchivePath = Path.Combine(ctx.ProcessedPath, Path.GetFileName(stagePath));
            _fileSystem.MoveFile(stagePath, finalArchivePath);
        }

        // ─────────────────────────────────────────────────────────
        //  Baseline Promotion
        // ─────────────────────────────────────────────────────────

        private async Task HandleBaselinePromotion(string policyId, DiscoveryResult discovery)
        {
            if (discovery.Attributes == null || !discovery.Attributes.Any())
            {
                _publisher.LogError(
                    $"[BASELINE] Refusing to promote empty attributes for {policyId}. Possible ZIP parse failure.",
                    null);

                await _publisher.PublishStatusEventAsync(policyId, "BASELINE_PROMOTION_FAILED", new
                {
                    Reason = "Empty attributes returned from FileProcessor",
                    Severity = "CRITICAL"
                });
                return;
            }

            var existingBaselines = await _db.PlatformBaselines
                .Where(b => b.PlatformId == policyId && b.IsActive)
                .ToListAsync();

            foreach (var b in existingBaselines) b.IsActive = false;

            await _db.PlatformBaselines.AddAsync(new PlatformBaseline
            {
                Id = Guid.NewGuid(),
                PlatformId = policyId,
                Attributes = discovery.Attributes,
                AttributesHash = discovery.Hashes,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();

            _publisher.LogInfo(
                $"[BASELINE] {policyId} promoted successfully with {discovery.Attributes.Count} attributes.");
        }

        // ─────────────────────────────────────────────────────────
        //  Audit Engine
        // ─────────────────────────────────────────────────────────

        private async Task ExecuteAuditAsync(
            string policyId,
            DiscoveryResult discovery,
            string executionId)
        {
            var baseline = await _db.PlatformBaselines
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.PlatformId == policyId && b.IsActive);

            if (baseline == null)
            {
                await SaveEvaluation(policyId, discovery, executionId, "MISSING_BASELINE", Guid.Empty);
                return;
            }

            bool isMatch = CompareHashes(baseline.AttributesHash, discovery.Hashes);
            string status = "NO_DRIFT";
            Dictionary<string, string>? differences = null;

            if (!isMatch)
            {
                differences = CompareAttributes(baseline.Attributes, discovery.Attributes);
                status = differences.Count > 0 ? "DRIFT" : "NO_DRIFT";
            }

            var detailId = await _db.GetOrCreatePolicyDetailIdAsync(
                policyId, discovery.Attributes, discovery.Hashes);

            await SaveEvaluation(policyId, discovery, executionId, status, baseline.Id, detailId, differences);

            if (status == "DRIFT")
            {
                await _publisher.PublishKafkaDriftAsync(policyId, differences!, baseline.Attributes);
            }
        }

        private async Task SaveEvaluation(
            string policyId,
            DiscoveryResult discovery,
            string executionId,
            string status,
            Guid baselineId,
            Guid? detailId = null,
            Dictionary<string, string>? diffs = null)
        {
            var eval = new PolicyDriftEval
            {
                Id = Guid.NewGuid(),
                PolicyId = policyId,
                BaselinePolicyID = baselineId,
                PolicyDriftEvalDetailsID = detailId ?? Guid.Empty,
                Status = status,
                Differences = diffs,
                ExecutionId = executionId,
                RunTimestamp = DateTime.UtcNow
            };

            await _db.LogDriftEvalAsync(eval);
            _publisher.LogInfo($"[AUDIT] {policyId} recorded as {status} in batch {executionId}");
        }

        // ─────────────────────────────────────────────────────────
        //  Batch Report
        // ─────────────────────────────────────────────────────────

        private async Task GenerateBatchReport(string executionId)
        {
            _publisher.LogInfo($"[REPORT] Aggregating results for Batch: {executionId}");

            var batchResults = await _db.PolicyDriftEvals
                .Where(e => e.ExecutionId == executionId)
                .ToListAsync();

            if (!batchResults.Any())
            {
                _publisher.LogWarning($"[REPORT] No records found for Batch {executionId}.");
                return;
            }

            int total = batchResults.Count;
            int noDrift = batchResults.Count(e => e.Status == "NO_DRIFT");
            int driftCount = batchResults.Count(e => e.Status == "DRIFT");
            int missingBaseline = batchResults.Count(e => e.Status == "MISSING_BASELINE");

            var allPolicyIds = batchResults.Select(e => e.PolicyId).ToList();

            var driftSummaries = batchResults
                .Where(e => e.Status == "DRIFT")
                .Select(e => $"Policy: {e.PolicyId} | Changes: {e.Differences?.Count ?? 0} attributes")
                .ToList();

            var missingDetails = batchResults
                .Where(e => e.Status == "MISSING_BASELINE")
                .Select(e => e.PolicyId)
                .ToList();

            _publisher.LogInfo("--- BATCH GOVERNANCE SUMMARY ---");
            _publisher.LogInfo($"Total Platforms Processed: {total}");
            _publisher.LogInfo($"Clean (No Drift): {noDrift}");
            _publisher.LogInfo($"Drifting Platforms: {driftCount}");
            _publisher.LogInfo($"Orphaned (Missing Baseline): {missingBaseline}");
            _publisher.LogInfo($"Policy Roster: {string.Join(", ", allPolicyIds)}");

            await _publisher.PublishStatusEventAsync("BATCH_REPORT", "COMPLETED", new
            {
                BatchId = executionId,
                TotalCount = total,
                DriftList = driftSummaries,
                MissingBaselines = missingDetails,
                Timestamp = DateTime.UtcNow
            });
        }

        // ─────────────────────────────────────────────────────────
        //  Comparison Helpers
        // ─────────────────────────────────────────────────────────

        private bool CompareHashes(
            Dictionary<string, string> baseHashes,
            Dictionary<string, string> discoveryHashes)
        {
            if (baseHashes == null || discoveryHashes == null) return false;
            if (baseHashes.Count != discoveryHashes.Count) return false;

            return baseHashes.All(k =>
                discoveryHashes.ContainsKey(k.Key) && discoveryHashes[k.Key] == k.Value);
        }

        private Dictionary<string, string> CompareAttributes(
            Dictionary<string, string> baseline,
            Dictionary<string, string> current)
        {
            var changes = new Dictionary<string, string>();
            var ignoreList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "INI:ApiVersion",
                "XML:LastModified"
            };

            foreach (var baseKvp in baseline)
            {
                if (ignoreList.Contains(baseKvp.Key)) continue;

                if (!current.ContainsKey(baseKvp.Key))
                    changes[baseKvp.Key] = $"REMOVED (Was: {baseKvp.Value})";
                else if (current[baseKvp.Key] != baseKvp.Value)
                    changes[baseKvp.Key] = $"MODIFIED (Base: {baseKvp.Value} | Current: {current[baseKvp.Key]})";
            }

            return changes;
        }

        // ─────────────────────────────────────────────────────────
        //  Base class abstract stubs — batch doesn't use these
        // ─────────────────────────────────────────────────────────

        protected override Task<IEnumerable<string>> GetPoliciesAsync()
            => Task.FromResult(Enumerable.Empty<string>());

        protected override Task HandleBaselineUpdate(string policyId)
            => Task.CompletedTask;

        protected override Task HandleDriftCheck(string policyId)
            => Task.CompletedTask;
    }
}