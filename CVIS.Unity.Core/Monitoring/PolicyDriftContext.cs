using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Monitoring
{
    // ─────────────────────────────────────────────────────────────
    //  Immutable snapshot: everything a consumer needs for one drift eval run
    // ─────────────────────────────────────────────────────────────
    public sealed class PolicyDriftContext
    {
        public string BaselineFolder { get; }
        public string EvalRoot { get; }
        public string SourcePath { get; }
        public string ProcessingPath { get; }
        public string ProcessedPath { get; }
        public string ExecutionId { get; }
        public string DateStamp { get; }

        public PolicyDriftContext(
            string baselineFolder,
            string evalRoot,
            string sourcePath,
            string processingPath,
            string processedPath,
            string executionId,
            string dateStamp)
        {
            BaselineFolder = baselineFolder;
            EvalRoot = evalRoot;
            SourcePath = sourcePath;
            ProcessingPath = processingPath;
            ProcessedPath = processedPath;
            ExecutionId = executionId;
            DateStamp = dateStamp;
        }
    }
}
