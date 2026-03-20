using CVIS.Unity.Core.Entities;
using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Core.Models;
using CVIS.Unity.Infrastructure.Data;
using CVIS.Unity.PolicyDrift.Orchestration.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Text;
using CVIS.Unity.Core.Extensions;

namespace CVIS.Unity.PolicyDrift.Orchestrator.Workflows
{
    public class PlatformBatchOrchestrator : PolicyWorkflowBase
    {
        private readonly IConfiguration _configuration;
        private readonly IFileProcessor _fileProcessor;
        private readonly PolicyDbContext _db;

        public PlatformBatchOrchestrator(
            IFileSystemService fileSystem,
            IUnityEventPublisher publisher,
            IConfiguration configuration,
            IFileProcessor fileProcessor,
            PolicyDbContext db) : base(fileSystem, publisher, configuration)
        {
            _configuration = configuration;
            _fileProcessor = fileProcessor;
            _db = db;
        }

        public override string WorkflowName => "CyberArk Platform Batch Refinery";


        public async Task RunBatchRefineryAsync()
        {
            // 1. Foundation Guard: Prevent ArgumentNullException
            var workingFolder = _configuration["Monitoring:WorkingFolder"];
            if (string.IsNullOrEmpty(workingFolder))
            {
                _publisher.LogError("CRITICAL: 'Monitoring:WorkingFolder' is missing. Batch aborted.");
                return;
            }

            var currentDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

            // 2. Derive Atomic Paths
            var sourcePath = Path.Combine(workingFolder, "PlatformPolicies", currentDate);
            var processingPath = Path.Combine(workingFolder, "PlatformPolicies", "processing", currentDate);
            var processedPath = Path.Combine(workingFolder, "PlatformPolicies", "processed", currentDate);

            // 3. Batch Identity
            var executionId = GenerateExecutionId(sourcePath);
            _publisher.LogInfo($"[BATCH] Starting {WorkflowName} | Execution: {executionId}");

            // 4. Staging Readiness
            if (!_fileSystem.DirectoryExists(sourcePath))
            {
                _publisher.LogWarning($"Source zone {sourcePath} is empty. Standing down.");
                return;
            }

            // Datyrix: Physically create the zones so MoveFile doesn't fail
            _fileSystem.CreateDirectory(processingPath);
            _fileSystem.CreateDirectory(processedPath);

            // 5. Atomic Processing Loop
            var zips = _fileSystem.GetFilesInDirectory(sourcePath, "*.zip");
            foreach (var zipPath in zips)
            {
                var fileName = Path.GetFileName(zipPath);
                var stagingPath = Path.Combine(processingPath, fileName);
                var policyId = Path.GetFileNameWithoutExtension(zipPath);

                try
                {
                    _fileSystem.MoveFile(zipPath, stagingPath);
                    await ProcessSinglePlatform(policyId, stagingPath, processedPath, executionId);
                }
                catch (Exception ex)
                {
                    _publisher.LogError($"[SKIP] Failed to stage {policyId}.", ex);
                    continue;
                }
            }

            await ProcessReport(executionId);
        }

        private async Task ProcessReport( string executionId)
        {
            // Section 3: Batch Conclusion
            await GenerateBatchReport(executionId);

            // Cryptorion: Final Technical Cleanup
            var currentDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var processingPath = Path.Combine(_configuration["Monitoring:WorkingFolder"], "PlatformPolicies", "processing", currentDate);

            if (_fileSystem.DirectoryExists(processingPath))
            {
                _fileSystem.Cleanup(processingPath);
                _publisher.LogInfo($"[CLEANUP] Workspace cleared for {currentDate}.");
            }
        }

        /// <summary>
        /// Overrides the base logic to process the Daily Drop folder
        /// </summary>
        public override async Task ExecuteAsync()
        {
            await RunBatchRefineryAsync();
        }


        private async Task GenerateBatchReport(string executionId)
        {
            _publisher.LogInfo($"[REPORT] Aggregating results for Batch: {executionId}");

            // 1. Extract Batch Results from SQL
            var batchResults = await _db.PolicyDriftEvals
                .Where(e => e.ExecutionId == executionId)
                .ToListAsync();

            if (!batchResults.Any())
            {
                _publisher.LogWarning($"[REPORT] No records found for Batch {executionId}.");
                return;
            }

            // 2. Calculate Quantitative Counts
            int total = batchResults.Count;
            int noDrift = batchResults.Count(e => e.Status == "NO_DRIFT");
            int driftCount = batchResults.Count(e => e.Status == "DRIFT");
            int missingBaseline = batchResults.Count(e => e.Status == "MISSING_BASELINE");

            // 3. Collect Identity Roster
            var allPolicyIds = batchResults.Select(e => e.PolicyId).ToList();

            // 4. Detailed Drift & Missing Summaries
            var driftSummaries = batchResults
                .Where(e => e.Status == "DRIFT")
                .Select(e => $"Policy: {e.PolicyId} | Changes: {e.Differences?.Count ?? 0} attributes")
                .ToList();

            var missingDetails = batchResults
                .Where(e => e.Status == "MISSING_BASELINE")
                .Select(e => e.PolicyId)
                .ToList();

            // 5. Publish the Governance Report
            _publisher.LogInfo("--- BATCH GOVERNANCE SUMMARY ---");
            _publisher.LogInfo($"Total Platforms Processed: {total}");
            _publisher.LogInfo($"Clean (No Drift): {noDrift}");
            _publisher.LogInfo($"Drifting Platforms: {driftCount}");
            _publisher.LogInfo($"Orphaned (Missing Baseline): {missingBaseline}");
            _publisher.LogInfo($"Policy Roster: {string.Join(", ", allPolicyIds)}");

            // Datyrix: Send the final summary to Kafka for the dashboard
            await _publisher.PublishStatusEventAsync("BATCH_REPORT", "COMPLETED", new
            {
                BatchId = executionId,
                TotalCount = total,
                DriftList = driftSummaries,
                MissingBaselines = missingDetails,
                Timestamp = DateTime.UtcNow
            });
        }

        


        // Required by Base, but redirected to the Batch logic
        protected override Task<IEnumerable<string>> GetPoliciesAsync() => Task.FromResult(Enumerable.Empty<string>());
        protected override Task HandleBaselineUpdate(string policyId) => Task.CompletedTask;
        protected override Task HandleDriftCheck(string policyId) => Task.CompletedTask;

        private async Task ProcessSinglePlatform(string policyId, string stagePath, string processedPath, string executionId)
        {
            _publisher.LogInfo($"[PROCESS] Beginning analysis for {policyId}");

            // 1. Memory Extraction & Parsing
            using var zipStream = _fileSystem.OpenRead(stagePath);
            var discovery = await _fileProcessor.ExtractAndParseZipWithHashesAsync(zipStream, policyId);

            // 2. The Baseline Gate: Check for manual override signal
            var updateSignalPath = Path.Combine(_configuration["Monitoring:UpdatePolicyFolder"], $"{policyId}.txt");

            if (_fileSystem.SignalFileExists(updateSignalPath))
            {
                // PATH B: Baseline Promotion
                await HandleBaselinePromotion(policyId, discovery);
                _fileSystem.DeleteSignalFile(updateSignalPath);
                _publisher.LogInfo($"[BASELINE] Promoted new Gold Standard for {policyId}. Signal cleared.");
            }

            // 3. Audit Engine: Determine Drift Status
            await ExecuteAuditAsync(policyId, discovery, executionId);

            // 4. Archive: Move from Processing to Processed
            var finalArchivePath = Path.Combine(processedPath, Path.GetFileName(stagePath));
            _fileSystem.MoveFile(stagePath, finalArchivePath);
        }

        private async Task HandleBaselinePromotion(string policyId, DiscoveryResult discovery)
        {
            // Guard: Never silently promote an empty baseline
            if (discovery.Attributes == null || !discovery.Attributes.Any())
            {
                _publisher.LogError($"[BASELINE] Refusing to promote empty attributes for {policyId}. Possible ZIP parse failure.", null);

                await _publisher.PublishStatusEventAsync(policyId, "BASELINE_PROMOTION_FAILED", new
                {
                    Reason = "Empty attributes returned from FileProcessor",
                    Severity = "CRITICAL"
                });

                return;
            }

            // Deactivate any existing active baseline for this platform
            var existingBaselines = await _db.PlatformBaselines
                .Where(b => b.PlatformId == policyId && b.IsActive)
                .ToListAsync();

            foreach (var b in existingBaselines) b.IsActive = false;

            // Insert the new Gold Standard
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

            _publisher.LogInfo($"[BASELINE] {policyId} promoted successfully with {discovery.Attributes.Count} attributes.");
        }

        private async Task ExecuteAuditAsync(string policyId, DiscoveryResult discovery, string executionId)
        {
            // 1. Fetch Active Baseline Anchor
            var baseline = await _db.PlatformBaselines
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.PlatformId == policyId && b.IsActive);

            // 2. Handle MISSING_BASELINE
            if (baseline == null)
            {
                await SaveEvaluation(policyId, discovery, executionId, "MISSING_BASELINE", Guid.Empty);
                return;
            }

            // 3. Fast-Path Hash Check
            bool isMatch = CompareHashes(baseline.AttributesHash, discovery.Hashes);
            string status = "NO_DRIFT";
            Dictionary<string, string> differences = null;

            // 4. Deep Attribute Comparison (If hashes differ)
            if (!isMatch)
            {
                differences = CompareAttributes(baseline.Attributes, discovery.Attributes);
                status = differences.Count > 0 ? "DRIFT" : "NO_DRIFT";
            }

            // 5. Detail Deduplication (FK Resolution)
            // Datyrix: This looks for an existing detail record or creates a new versioned one
            var detailId = await _db.GetOrCreatePolicyDetailIdAsync(policyId, discovery.Attributes, discovery.Hashes);

            // 6. Final Persistence
            await SaveEvaluation(policyId, discovery, executionId, status, baseline.Id, detailId, differences);

            // 7. Kafka Alerting
            if (status == "DRIFT")
            {
                await _publisher.PublishKafkaDriftAsync(policyId, differences, baseline.Attributes);
            }
        }

        private async Task SaveEvaluation(string policyId,
            DiscoveryResult discovery,
            string executionId,
            string status,
            Guid baselineId,
            Guid? detailId = null,
            Dictionary<string, string> diffs = null)
        {
            var eval = new PolicyDriftEval
            {
                Id = Guid.NewGuid(),
                PolicyId = policyId,
                BaselinePolicyID = baselineId,
                PolicyDriftEvalDetailsID = detailId ?? Guid.Empty,
                Status = status,
                Differences = diffs,
                ExecutionId = executionId, // The batch anchor
                RunTimestamp = DateTime.UtcNow
            };

            await _db.LogDriftEvalAsync(eval);

            _publisher.LogInfo($"[AUDIT] {policyId} recorded as {status} in batch {executionId}");
        }

        // Add these helper methods to PlatformBatchOrchestrator.cs

        private bool CompareHashes(Dictionary<string, string> baseHashes, Dictionary<string, string> discoveryHashes)
        {
            if (baseHashes == null || discoveryHashes == null) return false;
            if (baseHashes.Count != discoveryHashes.Count) return false;

            return baseHashes.All(k => discoveryHashes.ContainsKey(k.Key) && discoveryHashes[k.Key] == k.Value);
        }

        private Dictionary<string, string> CompareAttributes(Dictionary<string, string> baseline, Dictionary<string, string> current)
        {
            var changes = new Dictionary<string, string>();
            var ignoreList = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "INI:ApiVersion", "XML:LastModified" };

            foreach (var baseKvp in baseline)
            {
                if (ignoreList.Contains(baseKvp.Value)) continue;
                if (!current.ContainsKey(baseKvp.Key))
                    changes[baseKvp.Key] = $"REMOVED (Was: {baseKvp.Value})";
                else if (current[baseKvp.Key] != baseKvp.Value)
                    changes[baseKvp.Key] = $"MODIFIED (Base: {baseKvp.Value} | Current: {current[baseKvp.Key]})";
            }
            return changes;
        }

    }
}