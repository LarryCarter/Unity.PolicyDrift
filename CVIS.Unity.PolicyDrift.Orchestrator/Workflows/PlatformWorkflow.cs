using CVIS.Unity.Core.Entities;
using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Core.Models;
using CVIS.Unity.Infrastructure.Data;
using CVIS.Unity.PolicyDrift.Orchestration.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.PolicyDrift.Orchestrator.Workflows
{
    public class PlatformWorkflow : PolicyWorkflowBase
    {
        private readonly IConfiguration _configuration;
        private readonly FileProcessor _fileProcessor;
        private readonly PolicyDbContext _db;

        public PlatformWorkflow(
            IFileSystemService fileSystem,
            IUnityEventPublisher publisher,
            IConfiguration configuration,
            FileProcessor fileProcessor,
            PolicyDbContext db) : base(fileSystem, publisher, configuration)
        {
            _configuration = configuration;
            _fileProcessor = fileProcessor;
            _db = db;
        }

        public override string WorkflowName => "CyberArk Platform ZIP Monitoring";

        /// <summary>
        /// File-driven execution loop.
        /// Scans PolicyDrift\PolicyEval\{MM-dd-yyyy}\*.zip and processes each ZIP through
        /// the full pipeline: Unzip → Validate → Signal Check → Optional Promotion → Evaluation → Archive.
        /// After all ZIPs are processed, generates and sends the batch governance report email.
        /// </summary>
        public override async Task ExecuteAsync()
        {
            var workingFolder = _configuration["Monitoring:WorkingFolder"];
            if (string.IsNullOrEmpty(workingFolder))
            {
                _publisher.LogError("CRITICAL: 'Monitoring:WorkingFolder' is missing. Aborting.");
                return;
            }

            var currentDate = DateTime.UtcNow.ToString("MM-dd-yyyy");

            // Derive the three processing zones
            var sourcePath = Path.Combine(workingFolder, currentDate);
            var processingPath = Path.Combine(workingFolder, "processing");
            var processedPath = Path.Combine(workingFolder, "processed");

            var executionId = GenerateExecutionId(sourcePath);
            _publisher.LogInfo($"[WORKFLOW] Starting {WorkflowName} | Execution: {executionId} | Source: {sourcePath}");

            // Guard: Nothing to process
            if (!_fileSystem.DirectoryExists(sourcePath))
            {
                _publisher.LogWarning($"[WORKFLOW] Source folder {sourcePath} does not exist. Nothing to process.");
                return;
            }

            // Ensure processing zones exist
            _fileSystem.CreateDirectory(processingPath);
            _fileSystem.CreateDirectory(processedPath);

            // Scan for all ZIPs in the dated folder
            var zips = _fileSystem.GetFilesInDirectory(sourcePath, "*.zip");
            if (!zips.Any())
            {
                _publisher.LogInfo($"[WORKFLOW] No ZIP files found in {sourcePath}. Standing down.");
                return;
            }

            _publisher.LogInfo($"[WORKFLOW] Found {zips.Count()} ZIP(s) to process.");

            foreach (var zipPath in zips)
            {
                var fileName = Path.GetFileName(zipPath);
                var policyId = Path.GetFileNameWithoutExtension(zipPath);
                var stagingPath = Path.Combine(processingPath, fileName);

                try
                {
                    // Move ZIP from source → processing (atomic claim)
                    _fileSystem.MoveFile(zipPath, stagingPath);

                    // Kafka event: we are now processing this ZIP
                    await _db.SavePolicyEventAsync(policyId, "POLICY_PROCESSING_STARTED", "INFO", new
                    {
                        FileName = fileName,
                        StagingPath = stagingPath,
                        ExecutionId = executionId,
                        Timestamp = DateTime.UtcNow
                    });
                    _publisher.LogInfo($"[STAGING] {policyId}.zip claimed for processing.");

                    // Run the full pipeline for this policy
                    await ProcessSinglePolicy(policyId, stagingPath, processedPath, executionId);
                }
                catch (Exception ex)
                {
                    _publisher.LogError($"[SKIP] Failed to process {policyId}.", ex);
                    continue;
                }
            }

            // ── BATCH REPORT ────────────────────────────────────────────────
            await GenerateBatchReport(executionId);

            _publisher.LogInfo($"[WORKFLOW] Execution {executionId} complete. All ZIPs processed.");
        }

        /// <summary>
        /// Full pipeline for a single policy ZIP:
        /// 1. Unzip & validate required files ({policyId}.xml and {policyId}.ini must exist; exe/dll are allowed)
        /// 2. Parse all file types into normalized attributes (INI:key, XML:key, EXE:hash, DLL:hash, etc.)
        /// 3. Signal check → optional baseline promotion (deactivate old, insert new, version+1, Kafka event)
        /// 4. Detail deduplication: reuse existing detail FK if hashes match, or insert new versioned detail
        /// 5. Fetch active baseline → compare → log PolicyDriftEval with FKs to both baseline and detail
        /// 6. Move ZIP from processing → processed
        /// </summary>
        private async Task ProcessSinglePolicy(string policyId, string stagingPath, string processedPath, string executionId)
        {
            _publisher.LogInfo($"[PROCESS] Beginning analysis for {policyId}");

            // ── STEP 1: Unzip & Validate Required Files ─────────────────────
            using var zipStream = _fileSystem.OpenRead(stagingPath);

            // The ZIP must contain {policyId}.xml and {policyId}.ini at minimum.
            // It may also contain .exe, .dll, and other files — those are valid and will be processed.
            var requiredFiles = new[] { $"{policyId}.xml", $"{policyId}.ini" };
            var missingFiles = new List<string>();

            using (var inspectionArchive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true))
            {
                var entryNames = inspectionArchive.Entries
                    .Select(e => e.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var required in requiredFiles)
                {
                    if (!entryNames.Contains(required))
                    {
                        missingFiles.Add(required);
                    }
                }
            }

            if (missingFiles.Any())
            {
                var missingList = string.Join(", ", missingFiles);
                _publisher.LogError($"[VALIDATION] {policyId}.zip is missing required files: {missingList}. Skipping.", null);

                await _db.SavePolicyEventAsync(policyId, "POLICY_ZIP_INVALID", "CRITICAL", new
                {
                    Message = $"Required files missing from ZIP: {missingList}",
                    ExpectedFiles = string.Join(", ", requiredFiles),
                    MissingFiles = missingList,
                    ZipPath = stagingPath,
                    Severity = "CRITICAL"
                });

                // Archive the bad ZIP so it doesn't block the pipeline
                var failedArchivePath = Path.Combine(processedPath, Path.GetFileName(stagingPath));
                _fileSystem.MoveFile(stagingPath, failedArchivePath);
                return;
            }

            // Reset stream position after inspection
            zipStream.Position = 0;

            // ── STEP 2: Parse the validated ZIP ─────────────────────────────
            // FileProcessor reads all file types: .ini, .xml, .exe, .dll, etc.
            // Attributes are stored as type-prefixed keys: INI:SettingName, XML:NodePath, EXE:FileHash, DLL:FileHash
            var discovery = await _fileProcessor.ExtractAndParseZipWithHashesAsync(zipStream, policyId);

            // ── STEP 3: Signal Check — Baseline Promotion ───────────────────
            var updatePolicyFolder = _configuration["Monitoring:UpdatePolicyFolder"] ?? "";
            var signalFilePath = Path.Combine(updatePolicyFolder, $"{policyId}.txt");

            if (_fileSystem.SignalFileExists(signalFilePath))
            {
                _publisher.LogInfo($"[PATH-B] Signal file detected for {policyId}. Promoting baseline...");

                if (discovery.Attributes == null || !discovery.Attributes.Any())
                {
                    _publisher.LogError($"[BASELINE] Refusing to promote empty attributes for {policyId}. Possible ZIP parse failure.", null);
                    await _db.SavePolicyEventAsync(policyId, "BASELINE_PROMOTION_FAILED", "CRITICAL", new
                    {
                        Reason = "Empty attributes returned from FileProcessor",
                        Severity = "CRITICAL"
                    });
                }
                else
                {
                    // Deactivate old baseline → Insert new with version+1 → Get versions back
                    var (oldVersion, newVersion) = await _db.UpsertBaselineAsync(
                        policyId, discovery.Attributes, discovery.Hashes);

                    // Kafka proxy event
                    await _db.SavePolicyEventAsync(
                        policyId: policyId,
                        eventName: "BaselinePromoted",
                        eventType: "GOVERNANCE_ACTION",
                        meta: new { OldVersion = oldVersion, NewVersion = newVersion, Authorizer = "SignalFile" });

                    _publisher.LogInfo($"[BASELINE] {policyId} promoted: v{oldVersion} → v{newVersion} with {discovery.Attributes.Count} attributes.");

                    // Consume the signal file — promotion succeeded
                    _fileSystem.DeleteSignalFile(signalFilePath);
                }
            }

            // ── STEP 4: Detail Deduplication (FK Resolution) ────────────────
            // If a detail record with the same policyId exists and hashes match → reuse its Id as FK (no drift in detail).
            // If hashes differ → insert a new detail record with DriftVersion+1 and use the new Id as FK.
            // If none exists → insert the first detail record at DriftVersion 1.
            var detailId = await _db.GetOrCreatePolicyDetailIdAsync(
                policyId, discovery.Attributes, discovery.Hashes);

            // ── STEP 5: Fetch active baseline and evaluate ──────────────────
            // After a promotion, this fetches the record we just inserted — comparison will yield NO_DRIFT.
            var baseline = await _db.PlatformBaselines
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.PlatformId == policyId && b.IsActive);

            if (baseline == null)
            {
                // No baseline — record MISSING_BASELINE
                var missingEval = new PolicyDriftEval
                {
                    Id = Guid.NewGuid(),
                    PolicyId = policyId,
                    BaselinePolicyID = Guid.Empty,
                    PolicyDriftEvalDetailsID = detailId,
                    Status = "MISSING_BASELINE",
                    RunTimestamp = DateTime.UtcNow,
                    ExecutionId = executionId
                };

                await _db.LogDriftEvalAsync(missingEval);

                await _db.SavePolicyEventAsync(policyId, "ORPHAN_POLICY_BASELINE_DETECTED", "CRITICAL", new
                {
                    Message = "Platform discovered in Vault but no Baseline exists in Unity SQL.",
                    DetailId = detailId
                });

                _publisher.LogWarning($"[ORPHAN] Recorded MISSING_BASELINE for {policyId}. Create a signal file to resolve.");
            }
            else
            {
                // Compare current snapshot against the active baseline
                var driftReport = CompareAttributes(baseline.Attributes, discovery.Attributes);
                bool hasDrift = driftReport.Count > 0;

                // PolicyDriftEval points to both the baseline (what it should be) and the detail (what it is)
                var eval = new PolicyDriftEval
                {
                    Id = Guid.NewGuid(),
                    PolicyId = policyId,
                    BaselinePolicyID = baseline.Id,
                    PolicyDriftEvalDetailsID = detailId,
                    Differences = hasDrift ? driftReport : null,
                    Status = hasDrift ? "DRIFT" : "NO_DRIFT",
                    RunTimestamp = DateTime.UtcNow,
                    ExecutionId = executionId
                };

                await _db.LogDriftEvalAsync(eval);

                if (hasDrift)
                {
                    await _db.SavePolicyEventAsync(policyId, "POLICY_DRIFT_DETECTED", "CRITICAL", new
                    {
                        Message = "A Policy Drift has been discovered showing differences against the baseline.",
                        DetailId = detailId,
                        DriftCount = driftReport.Count
                    });
                    _publisher.LogWarning($"[DRIFT] Differences found for {policyId}. Check the audit dashboard.");
                }
                else
                {
                    await _db.SavePolicyEventAsync(policyId, "POLICY_NO_DRIFT", "INFO", new
                    {
                        Message = "No Policy Drift has been detected when compared against the baseline.",
                        DetailId = detailId
                    });
                    _publisher.LogInfo($"[CLEAN] {policyId} is in sync with the Gold Standard.");
                }
            }

            // ── STEP 6: Archive — move from processing → processed ──────────
            var finalArchivePath = Path.Combine(processedPath, Path.GetFileName(stagingPath));
            _fileSystem.MoveFile(stagingPath, finalArchivePath);
            _publisher.LogInfo($"[ARCHIVE] {policyId}.zip moved to processed.");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  BATCH REPORT — HTML email sent after all ZIPs are processed
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Generates an HTML governance report email with three sections:
        /// 1. Summary counts (NO_DRIFT, DRIFT, MISSING_BASELINE)
        /// 2. Drift detail: for each drifted policy, show what changed and what the baseline says it should be
        /// 3. Missing baseline: list each orphaned policyId
        /// </summary>
        private async Task GenerateBatchReport(string executionId)
        {
            _publisher.LogInfo($"[REPORT] Aggregating results for Batch: {executionId}");

            // 1. Pull all eval records for this batch
            var batchResults = await _db.PolicyDriftEvals
                .Where(e => e.ExecutionId == executionId)
                .ToListAsync();

            if (!batchResults.Any())
            {
                _publisher.LogWarning($"[REPORT] No records found for Batch {executionId}.");
                return;
            }

            // 2. Categorize
            int total = batchResults.Count;
            var noDriftResults = batchResults.Where(e => e.Status == "NO_DRIFT").ToList();
            var driftResults = batchResults.Where(e => e.Status == "DRIFT").ToList();
            var missingResults = batchResults.Where(e => e.Status == "MISSING_BASELINE").ToList();

            // 3. For each DRIFT eval, look up the baseline to show "what it should be"
            var driftSections = new List<DriftReportEntry>();
            foreach (var drift in driftResults)
            {
                PlatformBaseline baseline = null;
                if (drift.BaselinePolicyID != Guid.Empty)
                {
                    baseline = await _db.PlatformBaselines
                        .AsNoTracking()
                        .FirstOrDefaultAsync(b => b.Id == drift.BaselinePolicyID);
                }

                var entry = new DriftReportEntry
                {
                    PolicyId = drift.PolicyId,
                    Differences = drift.Differences ?? new Dictionary<string, string>(),
                    BaselineAttributes = baseline?.Attributes ?? new Dictionary<string, string>()
                };

                driftSections.Add(entry);
            }

            // 4. Build the HTML email
            var html = BuildReportHtml(executionId, total, noDriftResults, driftSections, missingResults);

            // 5. Send the email
            var recipients = _configuration["Reporting:EmailRecipients"] ?? "unity-governance@company.com";
            await _publisher.SendEmailAsync(
                to: recipients,
                subject: $"Unity Policy Drift Report — Batch {executionId[..8]} | {DateTime.UtcNow:yyyy-MM-dd}",
                htmlBody: html);

            _publisher.LogInfo($"[REPORT] Governance report emailed to {recipients}.");

            // 6. Kafka summary event
            await _db.SavePolicyEventAsync("BATCH", "BATCH_REPORT_GENERATED", "INFO", new
            {
                ExecutionId = executionId,
                TotalProcessed = total,
                NoDrift = noDriftResults.Count,
                DriftCount = driftResults.Count,
                MissingBaseline = missingResults.Count,
                Timestamp = DateTime.UtcNow
            });
        }

        private string BuildReportHtml(
            string executionId,
            int total,
            List<PolicyDriftEval> noDrift,
            List<DriftReportEntry> driftSections,
            List<PolicyDriftEval> missingBaseline)
        {
            var sb = new StringBuilder();

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><style>");
            sb.AppendLine("body { font-family: Segoe UI, Arial, sans-serif; color: #333; margin: 0; padding: 20px; }");
            sb.AppendLine(".header { background: #1a1a2e; color: #fff; padding: 20px 30px; border-radius: 6px 6px 0 0; }");
            sb.AppendLine(".header h1 { margin: 0; font-size: 22px; }");
            sb.AppendLine(".header p { margin: 4px 0 0; opacity: 0.8; font-size: 13px; }");
            sb.AppendLine(".body-wrap { border: 1px solid #e0e0e0; border-top: none; border-radius: 0 0 6px 6px; padding: 24px 30px; }");
            sb.AppendLine(".summary-grid { display: flex; gap: 16px; margin: 16px 0 24px; }");
            sb.AppendLine(".stat-card { flex: 1; padding: 16px; border-radius: 6px; text-align: center; }");
            sb.AppendLine(".stat-card .number { font-size: 32px; font-weight: 700; }");
            sb.AppendLine(".stat-card .label { font-size: 12px; text-transform: uppercase; letter-spacing: 0.5px; margin-top: 4px; }");
            sb.AppendLine(".card-green { background: #e8f5e9; color: #2e7d32; }");
            sb.AppendLine(".card-red { background: #fce4ec; color: #c62828; }");
            sb.AppendLine(".card-amber { background: #fff3e0; color: #e65100; }");
            sb.AppendLine(".card-blue { background: #e3f2fd; color: #1565c0; }");
            sb.AppendLine("h2 { font-size: 16px; border-bottom: 2px solid #e0e0e0; padding-bottom: 8px; margin-top: 28px; }");
            sb.AppendLine("table { width: 100%; border-collapse: collapse; margin: 12px 0; font-size: 13px; }");
            sb.AppendLine("th { background: #f5f5f5; text-align: left; padding: 10px 12px; border: 1px solid #e0e0e0; }");
            sb.AppendLine("td { padding: 8px 12px; border: 1px solid #e0e0e0; vertical-align: top; }");
            sb.AppendLine("tr:nth-child(even) { background: #fafafa; }");
            sb.AppendLine(".tag-drift { background: #c62828; color: #fff; padding: 2px 8px; border-radius: 3px; font-size: 11px; }");
            sb.AppendLine(".tag-clean { background: #2e7d32; color: #fff; padding: 2px 8px; border-radius: 3px; font-size: 11px; }");
            sb.AppendLine(".tag-missing { background: #e65100; color: #fff; padding: 2px 8px; border-radius: 3px; font-size: 11px; }");
            sb.AppendLine(".footer { margin-top: 24px; padding-top: 16px; border-top: 1px solid #e0e0e0; font-size: 11px; color: #999; }");
            sb.AppendLine("</style></head><body>");

            // ── HEADER ──────────────────────────────────────────────────────
            sb.AppendLine("<div class='header'>");
            sb.AppendLine("<h1>Unity Policy Drift — Governance Report</h1>");
            sb.AppendLine($"<p>Batch: {executionId[..12]}... | Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC</p>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div class='body-wrap'>");

            // ── SUMMARY CARDS ───────────────────────────────────────────────
            sb.AppendLine("<div class='summary-grid'>");
            sb.AppendLine($"<div class='stat-card card-blue'><div class='number'>{total}</div><div class='label'>Total Processed</div></div>");
            sb.AppendLine($"<div class='stat-card card-green'><div class='number'>{noDrift.Count}</div><div class='label'>No Drift</div></div>");
            sb.AppendLine($"<div class='stat-card card-red'><div class='number'>{driftSections.Count}</div><div class='label'>Drift Detected</div></div>");
            sb.AppendLine($"<div class='stat-card card-amber'><div class='number'>{missingBaseline.Count}</div><div class='label'>Missing Baseline</div></div>");
            sb.AppendLine("</div>");

            // ── SECTION 1: NO DRIFT (clean) ─────────────────────────────────
            if (noDrift.Any())
            {
                sb.AppendLine("<h2>Clean Policies — No Drift Detected</h2>");
                sb.AppendLine("<table><tr><th>Policy ID</th><th>Status</th><th>Evaluated At</th></tr>");
                foreach (var eval in noDrift)
                {
                    sb.AppendLine($"<tr><td>{eval.PolicyId}</td><td><span class='tag-clean'>NO_DRIFT</span></td><td>{eval.RunTimestamp:yyyy-MM-dd HH:mm}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // ── SECTION 2: DRIFT DETECTED ───────────────────────────────────
            if (driftSections.Any())
            {
                sb.AppendLine("<h2>Drift Detected — Requires Attention</h2>");

                foreach (var drift in driftSections)
                {
                    sb.AppendLine($"<h3 style='margin-top:20px;'><span class='tag-drift'>DRIFT</span> {drift.PolicyId}</h3>");
                    sb.AppendLine("<table><tr><th>Attribute</th><th>Current State (What Changed)</th><th>Expected State (Baseline)</th></tr>");

                    foreach (var diff in drift.Differences)
                    {
                        // diff.Key = attribute name, diff.Value = "MODIFIED (Base: X | Current: Y)" or "REMOVED" or "ADDED"
                        var expectedValue = drift.BaselineAttributes.ContainsKey(diff.Key)
                            ? drift.BaselineAttributes[diff.Key]
                            : "—";

                        sb.AppendLine($"<tr><td><code>{diff.Key}</code></td><td>{diff.Value}</td><td>{expectedValue}</td></tr>");
                    }

                    sb.AppendLine("</table>");
                }
            }

            // ── SECTION 3: MISSING BASELINE ─────────────────────────────────
            if (missingBaseline.Any())
            {
                sb.AppendLine("<h2>Missing Baseline — Orphaned Policies</h2>");
                sb.AppendLine("<p>The following policies were discovered in the Vault but have no active baseline in Unity SQL. Create a <code>{PolicyId}.txt</code> signal file to establish the initial baseline.</p>");
                sb.AppendLine("<table><tr><th>Policy ID</th><th>Detail ID</th><th>Discovered At</th></tr>");

                foreach (var orphan in missingBaseline)
                {
                    sb.AppendLine($"<tr><td><span class='tag-missing'>{orphan.PolicyId}</span></td><td><code>{orphan.PolicyDriftEvalDetailsID}</code></td><td>{orphan.RunTimestamp:yyyy-MM-dd HH:mm}</td></tr>");
                }

                sb.AppendLine("</table>");
            }

            // ── FOOTER ──────────────────────────────────────────────────────
            sb.AppendLine("<div class='footer'>");
            sb.AppendLine($"CVIS Unity PolicyDrift Engine — Execution ID: {executionId}");
            sb.AppendLine("</div>");

            sb.AppendLine("</div></body></html>");

            return sb.ToString();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  COMPARISON LOGIC
        // ═══════════════════════════════════════════════════════════════════

        public Dictionary<string, string> CompareAttributes(Dictionary<string, string> baseline, Dictionary<string, string> current)
        {
            var changes = new Dictionary<string, string>();

            // TODO: Feature Link - Replace this HashSet with a DB call to 'unity.IgnoreAttributes'
            var ignoreList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "INI:ApiVersion",
                "FILE_SIZE_placeholder.txt",
                "XML:LastModified"
            };

            // 1. Check for Modified or Removed
            foreach (var baseKvp in baseline)
            {
                if (ignoreList.Contains(baseKvp.Key)) continue;

                if (!current.ContainsKey(baseKvp.Key))
                {
                    changes[baseKvp.Key] = $"REMOVED (Was: {baseKvp.Value})";
                }
                else if (current[baseKvp.Key] != baseKvp.Value)
                {
                    changes[baseKvp.Key] = $"MODIFIED (Base: {baseKvp.Value} | Current: {current[baseKvp.Key]})";
                }
            }

            // 2. Check for Added
            foreach (var curKvp in current)
            {
                if (ignoreList.Contains(curKvp.Key)) continue;

                if (!baseline.ContainsKey(curKvp.Key))
                {
                    changes[curKvp.Key] = $"ADDED (New Value: {curKvp.Value})";
                }
            }

            return changes;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  INTERNAL TYPES
        // ═══════════════════════════════════════════════════════════════════

        private class DriftReportEntry
        {
            public string PolicyId { get; set; }
            public Dictionary<string, string> Differences { get; set; }
            public Dictionary<string, string> BaselineAttributes { get; set; }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  BASE CLASS CONTRACT (stubs — ExecuteAsync drives everything)
        // ═══════════════════════════════════════════════════════════════════

        protected override Task<IEnumerable<string>> GetPoliciesAsync() => Task.FromResult(Enumerable.Empty<string>());
        protected override Task HandleBaselineUpdate(string policyId) => Task.CompletedTask;
        protected override Task HandleDriftCheck(string policyId) => Task.CompletedTask;
    }
}
