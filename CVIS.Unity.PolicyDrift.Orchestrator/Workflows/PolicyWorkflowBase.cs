using CVIS.Unity.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.PolicyDrift.Orchestrator.Workflows
{
    public abstract class PolicyWorkflowBase : IPolicyWorkflow
    {
        protected readonly IFileSystemService _fileSystem;
        protected readonly IUnityEventPublisher _publisher;
        protected readonly IConfiguration _configuration;

        protected PolicyWorkflowBase(IFileSystemService fileSystem, IUnityEventPublisher publisher, IConfiguration configuration)
        {
            _fileSystem = fileSystem;
            _publisher = publisher;
            _configuration = configuration;
        }

        public abstract string WorkflowName { get; }

        // Logic Anchor: Deterministic Identity
        public string GenerateExecutionId(string path)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(path.ToLowerInvariant());
            byte[] hashBytes = System.Security.Cryptography.SHA256.HashData(pathBytes);
            return Convert.ToHexString(hashBytes);
        }

        // Configuration-Driven Path Resolver
        protected virtual string GetCurrentBatchPath()
        {
            var root = _configuration["Monitoring:WorkingFolder"] ?? @"C:\Baselines";
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            return Path.Combine(root, "PlatformPolicies", today);
        }

        // Mark this virtual so the Batch Orchestrator can override it
        public virtual async Task ExecuteAsync()
        {
            // Default logic for single-policy polling
            var policies = await GetPoliciesAsync();
            foreach (var policyId in policies)
            {
                if (_fileSystem.SignalFileExists(policyId))
                {
                    await HandleBaselineUpdate(policyId);
                }
                else
                {
                    await HandleDriftCheck(policyId);
                }
            }
        }

        protected abstract Task<IEnumerable<string>> GetPoliciesAsync();
        protected abstract Task HandleBaselineUpdate(string policyId);
        protected abstract Task HandleDriftCheck(string policyId);
    }
}
