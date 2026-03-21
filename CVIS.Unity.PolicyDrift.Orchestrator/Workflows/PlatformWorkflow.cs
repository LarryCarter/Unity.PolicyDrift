using CVIS.Unity.Core.Entities;
using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Core.Models;
using CVIS.Unity.Core.Monitoring;
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
        private readonly ISignalFileService _signalFiles;
        private readonly PolicyDbContext _db;

        public PlatformWorkflow(
            IFileSystemService fileSystem,
            IUnityEventPublisher publisher,
            IPolicyDriftPathProvider driftPath,
            IConfiguration configuration,
            FileProcessor fileProcessor,
            ISignalFileService signalFiles,
            PolicyDbContext db)
            : base(fileSystem, publisher, driftPath)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _fileProcessor = fileProcessor ?? throw new ArgumentNullException(nameof(fileProcessor));
            _signalFiles = signalFiles ?? throw new ArgumentNullException(nameof(signalFiles));
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public override string WorkflowName => "CyberArk Platform ZIP Monitoring";

        /// <summary>
        /// File-driven execution loop.
        /// Scans the daily eval folder for *.zip and processes each through
        /// the full pipeline: Unzip → Validate → Signal Check → Optional Promotion → Evaluation → Archive.
        /// After all ZIPs are processed, generates and sends the batch governance report email.
        /// </summary>
        public override async Task ExecuteAsync()
        {
            // ── 1. Build Context ─────────────────────────────────────
            var result = await _driftPath.BuildDriftContextAsync();
            if (!result.IsValid)
            {
                // Already logged + Kafka-alerted inside the provider.
                return;
            }

            var ctx = result.Context!;
            _publisher.LogInfo(
                $"[WORKFLOW] Starting {WorkflowName} | Execution: {ctx.ExecutionId} | Source: {ctx.SourcePath}");

            // ── 2. Source Gate ────────────────────────────────────────
            if (!_fileSystem.DirectoryExists(ctx.SourcePath))
            {
                _publisher.LogWarning(
                    $"[WORKFLOW] Source folder {ctx.SourcePath} does not exist. Nothing to process.");
                return;
            }

            // ── 3. Staging ───────────────────────────────────────────
            _driftPath.EnsureStagingDirectories(ctx);

            // ── 4. Scan for ZIPs ─────────────────────────────────────
            var zips = _fileSystem.GetFilesInDirectory(ctx.SourcePath, "*.zip");
            if (!zips.Any())
            {
                _publisher.LogInfo($"[WORKFLOW] No ZIP files found in {ctx.SourcePath}. Standing down.");
                return;
            }

            _publisher.LogInfo($"[WORKFLOW] Found {zips.Length} ZIP(s) to process.");

            // ── 5. Process Each ZIP ──────────────────────────────────
            foreach (var zipPath in zips)
            {
                var fileName = Path.GetFileName(zipPath);
                var policyId = Path.GetFileNameWithoutExtension(zipPath);
                var stagingPath = Path.Combine(ctx.ProcessingPath, fileName);

                try
                {
                    _fileSystem.MoveFile(zipPath, stagingPath);

                    await _db.SavePolicyEventAsync(policyId, "POLICY_PROCESSING_STARTED", "INFO", new
                    {
                        FileName = fileName,
                        StagingPath = stagingPath,
                        ExecutionId = ctx.ExecutionId,
                        Timestamp = DateTime.UtcNow
                    });
                    _publisher.LogInfo($"[STAGING] {policyId}.zip claimed for processing.");

                    await ProcessSinglePolicy(policyId, stagingPath, ctx);
                }
                catch (Exception ex)
                {
                    _publisher.LogError($"[SKIP] Failed to process {policyId}.", ex);

                    // Kafka: BATCH_PROCESSING_ERROR — operations subscribes
                    await _publisher.PublishStatusEventAsync(policyId, "BATCH_PROCESSING_ERROR", new
                    {
                        ExecutionId = ctx.ExecutionId,
                        Error = ex.Message,
                        Stage = "ZIP_PROCESSING",
                        Timestamp = DateTime.UtcNow
                    });

                    continue;
                }
            }

            // ── 6. Batch Report ──────────────────────────────────────
            await GenerateBatchReport(ctx.ExecutionId);

            _publisher.LogInfo($"[WORKFLOW] Execution {ctx.ExecutionId} complete. All ZIPs processed.");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  SINGLE POLICY PIPELINE
        // ═══════════════════════════════════════════════════════════════════

        private async Task ProcessSinglePolicy(
            string policyId,
            string stagingPath,
            PolicyDriftContext ctx)
        {
            _publisher.LogInfo($"[PROCESS] Beginning analysis for {policyId}");

            // ── STEP 1: Unzip & Validate Required Files ─────────────────────
            using (var zipStream = _fileSystem.OpenRead(stagingPath))
            {

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
                            missingFiles.Add(required);
                    }
                }

                if (missingFiles.Any())
                {
                    var missingList = string.Join(", ", missingFiles);
                    _publisher.LogError(
                        $"[VALIDATION] {policyId}.zip is missing required files: {missingList}. Skipping.", null);

                    await _db.SavePolicyEventAsync(policyId, "POLICY_ZIP_INVALID", "CRITICAL", new
                    {
                        Message = $"Required files missing from ZIP: {missingList}",
                        ExpectedFiles = string.Join(", ", requiredFiles),
                        MissingFiles = missingList,
                        ZipPath = stagingPath,
                        Severity = "CRITICAL"
                    });

                    // Kafka: BATCH_PROCESSING_ERROR — invalid ZIP structure
                    await _publisher.PublishStatusEventAsync(policyId, "BATCH_PROCESSING_ERROR", new
                    {
                        Error = $"Required files missing: {missingList}",
                        Stage = "ZIP_VALIDATION",
                        ExecutionId = ctx.ExecutionId
                    });

                    var failedArchivePath = Path.Combine(ctx.ProcessedPath, Path.GetFileName(stagingPath));
                    _fileSystem.MoveFile(stagingPath, failedArchivePath);
                    return;
                }

                zipStream.Position = 0;

                // ── STEP 2: Parse the validated ZIP ─────────────────────────────
                var discovery = await _fileProcessor.ExtractAndParseZipWithHashesAsync(zipStream, policyId);

                // ── STEP 3: Signal Check — Baseline Promotion ───────────────────
                if (_signalFiles.Exists(ctx.BaselineFolder, policyId))
                {
                    _publisher.LogInfo($"[PATH-B] Signal file detected for {policyId}. Promoting baseline...");

                    if (discovery.Attributes == null || !discovery.Attributes.Any())
                    {
                        _publisher.LogError(
                            $"[BASELINE] Refusing to promote empty attributes for {policyId}. Possible ZIP parse failure.",
                            null);

                        await _db.SavePolicyEventAsync(policyId, "BASELINE_PROMOTION_FAILED", "CRITICAL", new
                        {
                            Reason = "Empty attributes returned from FileProcessor",
                            Severity = "CRITICAL"
                        });
                    }
                    else
                    {
                        // Read the SNOW ticket ID from the signal file before consuming it
                        var snowTicketId = _signalFiles.ReadTicketId(ctx.BaselineFolder, policyId);

                        var (oldVersion, newVersion) = await _db.UpsertBaselineAsync(
                            policyId, discovery.Attributes, discovery.Hashes, snowTicketId);

                        await _db.SavePolicyEventAsync(
                            policyId: policyId,
                            eventName: "BaselinePromoted",
                            eventType: "GOVERNANCE_ACTION",
                            meta: new
                            {
                                OldVersion = oldVersion,
                                NewVersion = newVersion,
                                AttributeCount = discovery.Attributes.Count,
                                Authorizer = "SignalFile",
                                SNOWTicket = snowTicketId ?? "NOT_PROVIDED"
                            });

                        // Kafka: BASELINE_PROMOTED — governance dashboard subscribes
                        await _publisher.PublishStatusEventAsync(policyId, "BASELINE_PROMOTED", new
                        {
                            OldVersion = oldVersion,
                            NewVersion = newVersion,
                            AttributeCount = discovery.Attributes.Count,
                            SNOWTicket = snowTicketId ?? "NOT_PROVIDED",
                            ExecutionId = ctx.ExecutionId
                        });

                        // Kafka: BASELINE_MISSING_SNOW_TICKET — compliance subscribes
                        if (string.IsNullOrEmpty(snowTicketId))
                        {
                            await _publisher.PublishStatusEventAsync(policyId, "BASELINE_MISSING_SNOW_TICKET", new
                            {
                                Version = newVersion,
                                Message = "Baseline promoted without ServiceNow change authorization.",
                                ExecutionId = ctx.ExecutionId,
                                Severity = "WARNING"
                            });
                        }

                        _publisher.LogInfo(
                            $"[BASELINE] {policyId} promoted: v{oldVersion} → v{newVersion} " +
                            $"with {discovery.Attributes.Count} attributes. SNOW: {snowTicketId ?? "N/A"}");

                        // Consume the signal file — log and continue if delete fails
                        _signalFiles.TryDelete(ctx.BaselineFolder, policyId);
                    }
                }

                // ── STEP 4: Detail Deduplication (FK Resolution) ────────────────
                var detailId = await _db.GetOrCreatePolicyDetailIdAsync(
                    policyId, discovery.Attributes, discovery.Hashes);

                // ── STEP 5: Fetch active baseline and evaluate ──────────────────
                var baseline = await _db.PlatformBaselines
                    .AsNoTracking()
                    .FirstOrDefaultAsync(b => b.PlatformId == policyId && b.IsActive);

                if (baseline == null)
                {
                    var missingEval = new PolicyDriftEval
                    {
                        Id = Guid.NewGuid(),
                        PolicyId = policyId,
                        BaselinePolicyID = Guid.Empty,
                        PolicyDriftEvalDetailsID = detailId,
                        Status = "MISSING_BASELINE",
                        RunTimestamp = DateTime.UtcNow,
                        ExecutionId = ctx.ExecutionId
                    };

                    await _db.LogDriftEvalAsync(missingEval);

                    await _db.SavePolicyEventAsync(policyId, "ORPHAN_POLICY_BASELINE_DETECTED", "CRITICAL", new
                    {
                        Message = "Platform discovered in Vault but no Baseline exists in Unity SQL.",
                        DetailId = detailId
                    });

                    // Kafka: POLICY_MISSING_BASELINE — provisioning/ticketing subscribes
                    await _publisher.PublishStatusEventAsync(policyId, "POLICY_MISSING_BASELINE", new
                    {
                        DetailId = detailId,
                        Message = "No active baseline exists. Signal file required.",
                        ExecutionId = ctx.ExecutionId
                    });

                    _publisher.LogWarning(
                        $"[ORPHAN] Recorded MISSING_BASELINE for {policyId}. Create a signal file to resolve.");
                }
                else
                {
                    var driftReport = CompareAttributes(baseline.Attributes, discovery.Attributes);
                    bool hasDrift = driftReport.Count > 0;

                    var eval = new PolicyDriftEval
                    {
                        Id = Guid.NewGuid(),
                        PolicyId = policyId,
                        BaselinePolicyID = baseline.Id,
                        PolicyDriftEvalDetailsID = detailId,
                        Differences = hasDrift ? driftReport : null,
                        Status = hasDrift ? "DRIFT" : "NO_DRIFT",
                        RunTimestamp = DateTime.UtcNow,
                        ExecutionId = ctx.ExecutionId
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

                        // Kafka: POLICY_DRIFT_DETECTED — security operations subscribes
                        await _publisher.PublishKafkaDriftAsync(policyId, driftReport, baseline.Attributes);

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
            }
            // ── STEP 6: Archive — move from processing → processed ──────────
            var finalArchivePath = Path.Combine(ctx.ProcessedPath, Path.GetFileName(stagingPath));
            _fileSystem.MoveFile(stagingPath, finalArchivePath);
            _publisher.LogInfo($"[ARCHIVE] {policyId}.zip moved to processed.");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  BATCH REPORT — Corporate branded HTML email
        // ═══════════════════════════════════════════════════════════════════

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

            // ── Categorize eval results ──────────────────────────────
            int total = batchResults.Count;
            var noDriftResults = batchResults.Where(e => e.Status == "NO_DRIFT").ToList();
            var driftResults = batchResults.Where(e => e.Status == "DRIFT").ToList();
            var missingResults = batchResults.Where(e => e.Status == "MISSING_BASELINE").ToList();

            // ── Drift detail: look up baselines for "expected" column ─
            var driftSections = new List<DriftReportEntry>();
            foreach (var drift in driftResults)
            {
                PlatformBaseline? baseline = null;
                if (drift.BaselinePolicyID != Guid.Empty)
                {
                    baseline = await _db.PlatformBaselines
                        .AsNoTracking()
                        .FirstOrDefaultAsync(b => b.Id == drift.BaselinePolicyID);
                }

                driftSections.Add(new DriftReportEntry
                {
                    PolicyId = drift.PolicyId,
                    Differences = drift.Differences ?? new Dictionary<string, string>(),
                    BaselineAttributes = baseline?.Attributes ?? new Dictionary<string, string>()
                });
            }

            // ── Baseline promotions from PolicyEvents ────────────────
            // Pull promotion events for this batch's policies.
            // Metadata is a ValueConverter column so we filter in-memory after materialization.
            var batchPolicyIds = batchResults.Select(e => e.PolicyId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var promotionEvents = await _db.PolicyEvents
                .Where(e => e.EventName == "BaselinePromoted")
                .OrderByDescending(e => e.Timestamp)
                .ToListAsync();

            var promotions = promotionEvents
                .Where(e => batchPolicyIds.Contains(e.PolicyId)
                    && e.Metadata.ContainsKey("OldVersion"))
                .Select(e => new BaselinePromotionEntry
                {
                    PolicyId = e.PolicyId,
                    OldVersion = e.Metadata.GetValueOrDefault("OldVersion", "0"),
                    NewVersion = e.Metadata.GetValueOrDefault("NewVersion", "1"),
                    AttributeCount = e.Metadata.GetValueOrDefault("AttributeCount", "—"),
                    SnowTicketId = e.Metadata.GetValueOrDefault("SNOWTicket")
                })
                .ToList();

            // ── Build & send ─────────────────────────────────────────
            var html = BuildReportHtml(
                executionId, total, promotions, missingResults,
                driftSections, noDriftResults);

            var recipients = _configuration["Reporting:EmailRecipients"] ?? "unity-governance@company.com";
            await _publisher.SendEmailAsync(
                to: recipients,
                subject: $"Unity Policy Drift Report — Batch {executionId[..8]} | {DateTime.UtcNow:yyyy-MM-dd}",
                htmlBody: html);

            _publisher.LogInfo($"[REPORT] Governance report emailed to {recipients}.");

            // ── SQL Event: summary record ────────────────────────────
            await _db.SavePolicyEventAsync("BATCH", "BATCH_REPORT_GENERATED", "INFO", new
            {
                ExecutionId = executionId,
                TotalProcessed = total,
                NoDrift = noDriftResults.Count,
                DriftCount = driftResults.Count,
                MissingBaseline = missingResults.Count,
                BaselinePromotions = promotions.Count,
                Timestamp = DateTime.UtcNow
            });

            // ── Kafka: BATCH_GOVERNANCE_REPORT ───────────────────────
            // Full structured report — dashboard app subscribes and renders.
            // Contains everything a downstream consumer needs without parsing HTML.
            await _publisher.PublishStatusEventAsync("BATCH", "BATCH_GOVERNANCE_REPORT", new
            {
                ExecutionId = executionId,
                Timestamp = DateTime.UtcNow,
                Summary = new
                {
                    TotalProcessed = total,
                    NoDrift = noDriftResults.Count,
                    DriftDetected = driftResults.Count,
                    MissingBaseline = missingResults.Count,
                    BaselinePromotions = promotions.Count
                },
                Promotions = promotions.Select(p => new
                {
                    p.PolicyId,
                    p.OldVersion,
                    p.NewVersion,
                    p.AttributeCount,
                    SNOWTicket = p.SnowTicketId ?? "NOT_PROVIDED"
                }),
                MissingBaselines = missingResults.Select(m => new
                {
                    m.PolicyId,
                    DetailId = m.PolicyDriftEvalDetailsID,
                    m.RunTimestamp
                }),
                DriftDetails = driftSections.Select(d => new
                {
                    d.PolicyId,
                    DriftCount = d.Differences.Count,
                    Attributes = d.Differences
                }),
                CleanPolicies = noDriftResults.Select(c => new
                {
                    c.PolicyId,
                    c.RunTimestamp
                })
            });
        }

        /// <summary>
        /// Corporate branded HTML report.
        /// Section order: Baseline Updates → Missing Baselines → Drift Detected → Clean Policies
        /// </summary>
        private string BuildReportHtml(
            string executionId,
            int total,
            List<BaselinePromotionEntry> promotions,
            List<PolicyDriftEval> missingBaseline,
            List<DriftReportEntry> driftSections,
            List<PolicyDriftEval> noDrift)
        {
            var sb = new StringBuilder();

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><style>");
            sb.AppendLine("body { font-family: Segoe UI, Arial, sans-serif; color: #333; margin: 0; padding: 0; background: #f4f4f4; }");
            sb.AppendLine(".brand-bar { background: #cf0a2c; padding: 16px 28px; }");
            sb.AppendLine(".brand-bar h1 { margin: 0; font-size: 22px; font-weight: 700; color: #fff; letter-spacing: 0.5px; }");
            sb.AppendLine(".gold-accent { height: 4px; background: #ffbf00; }");
            sb.AppendLine(".body-wrap { background: #fff; padding: 24px 28px; }");
            sb.AppendLine(".itsm-badge { background: #ffbf00; padding: 6px 16px; border-radius: 2px; display: inline-block; font-size: 14px; font-weight: 700; color: #333; }");
            sb.AppendLine(".summary-grid { display: flex; gap: 10px; margin: 0 0 24px; }");
            sb.AppendLine(".stat-card { flex: 1; padding: 14px 10px; border-radius: 4px; text-align: center; }");
            sb.AppendLine(".stat-card .number { font-size: 26px; font-weight: 700; }");
            sb.AppendLine(".stat-card .label { font-size: 10px; text-transform: uppercase; letter-spacing: 0.5px; margin-top: 4px; }");
            sb.AppendLine(".card-blue { background: #e3f2fd; color: #1565c0; }");
            sb.AppendLine(".card-green { background: #e8f5e9; color: #2e7d32; }");
            sb.AppendLine(".card-red { background: #fce4ec; color: #c62828; }");
            sb.AppendLine(".card-amber { background: #fff3e0; color: #e65100; }");
            sb.AppendLine("h2 { font-size: 15px; font-weight: 700; color: #333; border-bottom: 2px solid #e0e0e0; padding-bottom: 8px; margin: 28px 0 12px; }");
            sb.AppendLine("table { width: 100%; border-collapse: collapse; margin: 0 0 8px; font-size: 13px; }");
            sb.AppendLine("th { background: #f5f5f5; text-align: left; padding: 10px 12px; border: 1px solid #e0e0e0; font-weight: 600; color: #333; }");
            sb.AppendLine("td { padding: 8px 12px; border: 1px solid #e0e0e0; vertical-align: top; }");
            sb.AppendLine("tr:nth-child(even) { background: #fafafa; }");
            sb.AppendLine(".tag-drift { background: #c62828; color: #fff; padding: 2px 8px; border-radius: 3px; font-size: 11px; }");
            sb.AppendLine(".tag-clean { background: #2e7d32; color: #fff; padding: 2px 8px; border-radius: 3px; font-size: 11px; }");
            sb.AppendLine(".tag-missing { background: #e65100; color: #fff; padding: 2px 8px; border-radius: 3px; font-size: 11px; }");
            sb.AppendLine(".tag-snow-missing { background: #fff3e0; color: #e65100; padding: 2px 8px; border-radius: 3px; font-size: 11px; font-weight: 600; }");
            sb.AppendLine("code { font-family: Consolas, monospace; font-size: 12px; background: #f5f5f5; padding: 2px 6px; border-radius: 3px; }");
            sb.AppendLine(".footer { margin: 24px 0 0; padding: 16px 0 0; border-top: 1px solid #e0e0e0; font-size: 11px; color: #999; }");
            sb.AppendLine("</style></head><body>");

            // ── CORPORATE HEADER ─────────────────────────────────────
            sb.AppendLine("<div class='brand-bar'><h1>WELLS FARGO</h1></div>");
            sb.AppendLine("<div class='gold-accent'></div>");
            sb.AppendLine("<div class='body-wrap'>");

            // ── ITSM Badge + Date ────────────────────────────────────
            sb.AppendLine("<div style='display:flex;justify-content:space-between;align-items:flex-start;margin:0 0 20px;'>");
            sb.AppendLine("<span class='itsm-badge'>CVIS Unity — Policy Drift Governance</span>");
            sb.AppendLine($"<span style='font-size:13px;color:#666;'>{DateTime.UtcNow:MMMM d, yyyy}</span>");
            sb.AppendLine("</div>");

            // ── Greeting ─────────────────────────────────────────────
            sb.AppendLine("<p style='margin:0 0 6px;font-size:14px;'>Hi CyberArk Platform Team,</p>");
            sb.AppendLine($"<p style='margin:0 0 20px;font-size:14px;color:#555;'>The following is the automated daily governance report for CyberArk platform policy drift monitoring. This batch evaluated <strong>{total} platforms</strong> against their authorized baselines.</p>");

            // ── SUMMARY CARDS ────────────────────────────────────────
            sb.AppendLine("<div class='summary-grid'>");
            sb.AppendLine($"<div class='stat-card card-blue'><div class='number'>{total}</div><div class='label'>Total Processed</div></div>");
            sb.AppendLine($"<div class='stat-card card-green'><div class='number'>{noDrift.Count}</div><div class='label'>No Drift</div></div>");
            sb.AppendLine($"<div class='stat-card card-red'><div class='number'>{driftSections.Count}</div><div class='label'>Drift Detected</div></div>");
            sb.AppendLine($"<div class='stat-card card-amber'><div class='number'>{missingBaseline.Count}</div><div class='label'>Missing Baseline</div></div>");
            sb.AppendLine("</div>");

            // ── SECTION 1: BASELINE UPDATES ──────────────────────────
            if (promotions.Any())
            {
                sb.AppendLine("<h2>Baseline updates — promoted this batch</h2>");
                sb.AppendLine("<table><tr><th>Policy ID</th><th>Version</th><th>Attributes</th><th>SNOW Ticket</th></tr>");

                foreach (var promo in promotions)
                {
                    var ticketCell = string.IsNullOrEmpty(promo.SnowTicketId) || promo.SnowTicketId == "NOT_PROVIDED"
                        ? "<span class='tag-snow-missing'>MISSING</span>"
                        : $"<code>{promo.SnowTicketId}</code>";

                    sb.AppendLine($"<tr><td>{promo.PolicyId}</td><td>v{promo.OldVersion} → v{promo.NewVersion}</td><td>{promo.AttributeCount}</td><td>{ticketCell}</td></tr>");
                }

                sb.AppendLine("</table>");
            }

            // ── SECTION 2: MISSING BASELINE ──────────────────────────
            if (missingBaseline.Any())
            {
                sb.AppendLine("<h2>Missing baseline — orphaned policies</h2>");
                sb.AppendLine("<p style='font-size:13px;color:#666;margin:0 0 12px;'>The following policies were discovered in the Vault but have no active baseline in Unity SQL. Submit a change request and create a <code>{PolicyId}.txt</code> signal file to establish the initial baseline.</p>");
                sb.AppendLine("<table><tr><th>Policy ID</th><th>Detail ID</th><th>Discovered At</th></tr>");

                foreach (var orphan in missingBaseline)
                {
                    sb.AppendLine($"<tr><td><span class='tag-missing'>{orphan.PolicyId}</span></td><td><code>{orphan.PolicyDriftEvalDetailsID}</code></td><td>{orphan.RunTimestamp:yyyy-MM-dd HH:mm}</td></tr>");
                }

                sb.AppendLine("</table>");
            }

            // ── SECTION 3: DRIFT DETECTED ────────────────────────────
            if (driftSections.Any())
            {
                sb.AppendLine("<h2>Drift detected — requires attention</h2>");

                foreach (var drift in driftSections)
                {
                    sb.AppendLine($"<h3 style='margin-top:20px;font-size:14px;font-weight:600;'><span class='tag-drift'>DRIFT</span> {drift.PolicyId}</h3>");
                    sb.AppendLine("<table><tr><th>Attribute</th><th>Current State</th><th>Expected (Baseline)</th></tr>");

                    foreach (var diff in drift.Differences)
                    {
                        var expectedValue = drift.BaselineAttributes.ContainsKey(diff.Key)
                            ? drift.BaselineAttributes[diff.Key]
                            : "—";

                        sb.AppendLine($"<tr><td><code>{diff.Key}</code></td><td style='color:#c62828;'>{diff.Value}</td><td>{expectedValue}</td></tr>");
                    }

                    sb.AppendLine("</table>");
                }
            }

            // ── SECTION 4: CLEAN POLICIES (last) ─────────────────────
            if (noDrift.Any())
            {
                sb.AppendLine("<h2>Clean policies — no drift detected</h2>");
                sb.AppendLine("<table><tr><th>Policy ID</th><th>Status</th><th>Evaluated At</th></tr>");

                foreach (var eval in noDrift)
                {
                    sb.AppendLine($"<tr><td>{eval.PolicyId}</td><td><span class='tag-clean'>NO_DRIFT</span></td><td>{eval.RunTimestamp:yyyy-MM-dd HH:mm}</td></tr>");
                }

                sb.AppendLine("</table>");
            }

            // ── FOOTER ───────────────────────────────────────────────
            sb.AppendLine("<div class='footer'>");
            sb.AppendLine($"<p style='margin:0 0 4px;'>CVIS Unity PolicyDrift Engine — Execution ID: {executionId}</p>");
            sb.AppendLine("<p style='margin:0;'>This is an automated report generated by IT Service Management. For questions, contact the CyberArk Platform Engineering team.</p>");
            sb.AppendLine("</div>");

            sb.AppendLine("</div></body></html>");

            return sb.ToString();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  COMPARISON LOGIC
        // ═══════════════════════════════════════════════════════════════════

        public Dictionary<string, string> CompareAttributes(
            Dictionary<string, string> baseline,
            Dictionary<string, string> current)
        {
            var changes = new Dictionary<string, string>();

            // TODO: Feature Link - Replace this HashSet with a DB call to 'unity.IgnoreAttributes'
            var ignoreList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "INI:ApiVersion",
                "FILE_SIZE_placeholder.txt",
                "XML:LastModified"
            };

            // Check for Modified or Removed
            foreach (var baseKvp in baseline)
            {
                if (ignoreList.Contains(baseKvp.Key)) continue;

                if (!current.ContainsKey(baseKvp.Key))
                    changes[baseKvp.Key] = $"REMOVED (Was: {baseKvp.Value})";
                else if (current[baseKvp.Key] != baseKvp.Value)
                    changes[baseKvp.Key] = $"MODIFIED (Base: {baseKvp.Value} | Current: {current[baseKvp.Key]})";
            }

            // Check for Added
            foreach (var curKvp in current)
            {
                if (ignoreList.Contains(curKvp.Key)) continue;

                if (!baseline.ContainsKey(curKvp.Key))
                    changes[curKvp.Key] = $"ADDED (New Value: {curKvp.Value})";
            }

            return changes;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  INTERNAL TYPES
        // ═══════════════════════════════════════════════════════════════════

        private class DriftReportEntry
        {
            public string PolicyId { get; set; } = string.Empty;
            public Dictionary<string, string> Differences { get; set; } = new();
            public Dictionary<string, string> BaselineAttributes { get; set; } = new();
        }

        private class BaselinePromotionEntry
        {
            public string PolicyId { get; set; } = string.Empty;
            public string OldVersion { get; set; } = "0";
            public string NewVersion { get; set; } = "1";
            public string AttributeCount { get; set; } = "—";
            public string? SnowTicketId { get; set; }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  BASE CLASS CONTRACT (stubs — ExecuteAsync drives everything)
        // ═══════════════════════════════════════════════════════════════════

        protected override Task<IEnumerable<string>> GetPoliciesAsync()
            => Task.FromResult(Enumerable.Empty<string>());

        protected override Task HandleBaselineUpdate(string policyId)
            => Task.CompletedTask;

        protected override Task HandleDriftCheck(string policyId)
            => Task.CompletedTask;
    }
}
