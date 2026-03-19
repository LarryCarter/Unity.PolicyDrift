using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Interfaces
{
    public interface IPolicyWorkflow
    {
        string WorkflowName { get; }

        /// <summary>
        /// Executes the full lifecycle: List targets, check for signal files, 
        /// and perform either a Baseline Update or a Drift Check.
        /// </summary>
        Task ExecuteAsync();
    }
}
