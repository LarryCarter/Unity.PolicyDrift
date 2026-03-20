using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Core.Monitoring;
using Microsoft.DotNet.Scaffolding.Shared;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Infrastructure.Monitoring
{
    public class PolicyDriftPathProvider : IPolicyDriftPathProvider
    {
        private readonly IConfiguration _configuration;
        private readonly IFileSystem _fileSystem;
        private readonly IUnityEventPublisher _publisher;

        // Config keys — single source of truth
        private const string BaselineFolderKey = "Monitoring:PolicyBaselineFolder";
        private const string EvalRootKey = "Monitoring:PolicyEvalFolder";

        public PolicyDriftPathProvider(
            IConfiguration configuration,
            IFileSystem fileSystem,
            IUnityEventPublisher publisher)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        }

        public Task<PolicyDriftContextResult> BuildDriftContextAsync()
            => BuildDriftContextAsync(DateTime.UtcNow);

        public async Task<PolicyDriftContextResult> BuildDriftContextAsync(DateTime asOfUtc)
        {
            // ── 1. Config Validation ─────────────────────────────────
            //    Missing config = human intervention required → Kafka alert
            var baselineFolder = _configuration[BaselineFolderKey];
            if (string.IsNullOrEmpty(baselineFolder))
            {
                var error = $"CRITICAL: '{BaselineFolderKey}' is missing in configuration. " +
                            "Drift eval cannot proceed without a baseline reference path.";
                _publisher.LogError(error);
                await AlertConfigurationFailureAsync(BaselineFolderKey, error);
                return PolicyDriftContextResult.Failure(error);
            }

            var evalRoot = _configuration[EvalRootKey];
            if (string.IsNullOrEmpty(evalRoot))
            {
                var error = $"CRITICAL: '{EvalRootKey}' is missing in configuration. " +
                            "Drift eval cannot proceed without an evaluation root path.";
                _publisher.LogError(error);
                await AlertConfigurationFailureAsync(EvalRootKey, error);
                return PolicyDriftContextResult.Failure(error);
            }

            // ── 2. Root Folder Assurance ─────────────────────────────
            //    These are infrastructure folders — safe to create if absent.
            EnsureRootFolder(baselineFolder, BaselineFolderKey);
            EnsureRootFolder(evalRoot, EvalRootKey);

            // ── 3. Path Derivation ───────────────────────────────────
            var dateStamp = asOfUtc.ToString("yyyy-MM-dd");
            var sourcePath = Path.Combine(evalRoot, dateStamp);
            var processingPath = Path.Combine(sourcePath, "Processing");
            var processedPath = Path.Combine(sourcePath, "Processed");

            // ── 4. Execution Identity ────────────────────────────────
            var executionId = GenerateExecutionId(sourcePath);

            _publisher.LogInfo(
                $"[PolicyDriftPath] Context built | " +
                $"ExecutionId: {executionId} | " +
                $"Date: {dateStamp} | " +
                $"Source: {sourcePath}");

            return PolicyDriftContextResult.Success(new PolicyDriftContext(
                baselineFolder,
                evalRoot,
                sourcePath,
                processingPath,
                processedPath,
                executionId,
                dateStamp));
        }

        public void EnsureStagingDirectories(PolicyDriftContext context)
        {
            _fileSystem.CreateDirectory(context.ProcessingPath);
            _publisher.LogInfo($"[PolicyDriftPath] Staging ready: {context.ProcessingPath}");

            _fileSystem.CreateDirectory(context.ProcessedPath);
            _publisher.LogInfo($"[PolicyDriftPath] Staging ready: {context.ProcessedPath}");
        }

        public string GenerateExecutionId(string path)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(path.ToLowerInvariant());
            byte[] hashBytes = SHA256.HashData(pathBytes);
            return Convert.ToHexString(hashBytes);
        }

        // ─────────────────────────────────────────────────────────
        //  Private helpers
        // ─────────────────────────────────────────────────────────

        private void EnsureRootFolder(string path, string configKey)
        {
            if (!_fileSystem.DirectoryExists(path))
            {
                _fileSystem.CreateDirectory(path);
                _publisher.LogWarning(
                    $"[PolicyDriftPath] Root folder did not exist and was created: " +
                    $"{path} (from {configKey})");
            }
        }

        private async Task AlertConfigurationFailureAsync(string missingKey, string errorDetail)
        {
            var differences = new Dictionary<string, string>
            {
                ["MissingConfigKey"] = missingKey,
                ["ErrorDetail"] = errorDetail,
                ["DetectedAtUtc"] = DateTime.UtcNow.ToString("O")
            };

            var baseline = new Dictionary<string, string>
            {
                ["ExpectedState"] = "Configuration key present and non-empty",
                ["ActualState"] = "Key missing or empty"
            };

            await _publisher.PublishKafkaDriftAsync(
                policyId: $"CONFIG:{missingKey}",
                differences: differences,
                baseline: baseline);
        }
    }
}
