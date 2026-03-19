using CVIS.Unity.Core.Interfaces;
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

        protected PolicyWorkflowBase(IFileSystemService fileSystem, IUnityEventPublisher publisher)
        {
            _fileSystem = fileSystem;
            _publisher = publisher;
        }

        public abstract string WorkflowName { get; }

        public async Task ExecuteAsync()
        {
            // 1. Fetch the list of PolicyIDs from CyberArk (Placeholder for tonight)
            var policies = await GetPoliciesAsync();

            foreach (var policyId in policies)
            {
                // 2. Logic from your diagram: Check for {PolicyID}.txt
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
