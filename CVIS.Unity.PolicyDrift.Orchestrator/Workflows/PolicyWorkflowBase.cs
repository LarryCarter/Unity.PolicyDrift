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
        protected readonly IPolicyDriftPathProvider _driftPath;

        protected PolicyWorkflowBase(
            IFileSystemService fileSystem,
            IUnityEventPublisher publisher,
            IPolicyDriftPathProvider driftPath)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
            _driftPath = driftPath ?? throw new ArgumentNullException(nameof(driftPath));
        }

        public abstract string WorkflowName { get; }

        public virtual async Task ExecuteAsync()
        {
            var policies = await GetPoliciesAsync();
            foreach (var policyId in policies)
            {
                await HandleDriftCheck(policyId);
            }
        }

        protected abstract Task<IEnumerable<string>> GetPoliciesAsync();
        protected abstract Task HandleBaselineUpdate(string policyId);
        protected abstract Task HandleDriftCheck(string policyId);
    }
}
