using CVIS.Unity.Core.Entities;
using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Infrastructure.Data;
using CVIS.Unity.PolicyDrift.Orchestration.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            PolicyDbContext db) : base(fileSystem, publisher)
        {
            // CodeVyrn: You must assign the parameters to the private fields here
            _configuration = configuration;
            _fileProcessor = fileProcessor;
            _db = db;
        }

        public override string WorkflowName => "CyberArk Platform ZIP Monitoring";

        protected override Task<IEnumerable<string>> GetPoliciesAsync()
        {
            // Pulling from appsettings.json to allow runtime control on DB
            var section = _configuration.GetSection("Monitoring:Platforms");
            var platforms = section.GetChildren().Select(s => s.Value).Where(v => !string.IsNullOrEmpty(v)).ToList();

            if (platforms == null || !platforms.Any())
            {
                // This warning is vital for troubleshooting server deployments
                _publisher.LogWarning("No platforms configured for monitoring in appsettings.json. Using internal defaults (WinServerLocal, UnixSSH).");

                return Task.FromResult<IEnumerable<string>>(new List<string>
                {
                    "WinServerLocal",
                    "UnixSSH"
                });
            }

            return Task.FromResult<IEnumerable<string>>(platforms);
        }

        protected override async Task HandleBaselineUpdate(string policyId)
        {
            _publisher.LogInfo($"[PATH-B] Starting Baseline Update for: {policyId}");

            try
            {
                // 1. Get the ZIP (Simulated or via CyberArk API)
                using var zipStream = await GetZipFromCyberArk(policyId);

                // 2. Parse into normalized attributes
                var attributes = await _fileProcessor.ExtractAndParseZipAsync(zipStream, policyId);

                // 3. Persist to SQL 2018 (unity schema)
                await _db.UpsertBaselineAsync(policyId, attributes);

                // 4. Cleanup and Notify
                _fileSystem.DeleteSignalFile(policyId);

                await _publisher.PublishStatusEventAsync(policyId, "BASELINE_UPDATED", new
                {
                    AttributeCount = attributes.Count,
                    Timestamp = DateTime.UtcNow
                });

                _publisher.LogInfo($"[SUCCESS] Baseline for {policyId} updated with {attributes.Count} attributes.");
            }
            catch (Exception ex)
            {
                _publisher.LogError($"Failed to update baseline for {policyId}", ex);
                throw; // Re-throw to be caught by the Orchestrator loop
            }
        }

        protected override async Task HandleDriftCheck(string policyId)
        {//ExtractAndParseZipAsync
            _publisher.LogInfo($"[PATH-A] Starting Drift Check for: {policyId}");

            // 1. Fetch current state from Vault and Generate Scoped Hashes
            using var zipStream = await GetZipFromCyberArk(policyId);
            var discovery = await _fileProcessor.ExtractAndParseZipWithHashesAsync(zipStream, policyId);

            // 2. Fast-Path: Get or Create the Detail record (The "Actual" state exists even without a Baseline)
            var detailId = await _db.GetOrCreatePolicyDetailIdAsync(policyId, discovery.Attributes, discovery.Hashes);

            // 3. Fetch current active baseline
            var baseline = await _db.PlatformBaselines
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.PlatformId == policyId && b.IsActive);

            // MISSING_BASELINE HANDLING ---
            if (baseline == null)
            {
                var missingEval = new PolicyDriftEval
                {
                    Id = Guid.NewGuid(),
                    PolicyId = policyId,
                    BaselinePolicyID = Guid.Empty, // No anchor found
                    PolicyDriftEvalDetailsID = detailId,
                    Status = "MISSING_BASELINE",
                    RunTimestamp = DateTime.UtcNow,
                    ExecutionId = Guid.NewGuid()
                };

                await _db.LogDriftEvalAsync(missingEval);

                // Send to Events Table for Kafka/Splunk alerting
                await _db.SavePolicyEventAsync(policyId, "ORPHAN_POLICY_BASELINE_DETECTED", "CRITICAL", new
                {
                    Message = "Platform discovered in Vault but no Baseline exists in Unity SQL.",
                    DetailId = detailId
                });

                _publisher.LogWarning($"[ORPHAN] Recorded MISSING_BASELINE for {policyId}. Create a signal file to resolve.");
                return;
            }

            // 4. Compare and Log Results
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
                ExecutionId = Guid.NewGuid() // Should ideally come from the main loop
            };

            await _db.LogDriftEvalAsync(eval);

            // 5. Audit Logging: Only send the relevant event to the database
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

        private async Task<Stream> GetZipFromCyberArk(string id)
        {
            _publisher.LogInfo($"[VAULT] Requesting Platform Package for {id} via API...");

            // Create a valid, in-memory ZIP archive so the FileProcessor doesn't crash
            var ms = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                // Add a dummy file so the ZIP isn't technically empty
                var entry = archive.CreateEntry("placeholder.{fileType}");
                using var writer = new StreamWriter(entry.Open());
                await writer.WriteLineAsync($"Dummy content for {id}");
            }

            // Cryptorion: CRITICAL - Reset the stream position to 0 so the reader starts at the beginning
            ms.Position = 0;
            return ms;
        }

        //private async Task<Stream> GetZipFromCyberArk(string id)
        //{
        //    // In the final Unity phase, this calls the CyberArk 'Get Platform' REST API.
        //    // For tonight's build, we return an empty stream or mock data.
        //    _publisher.LogInfo($"[VAULT] Requesting Platform Package for {id} via API...");

        //    return new MemoryStream();
        //}
    }
}
