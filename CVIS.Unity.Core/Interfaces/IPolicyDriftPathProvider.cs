using CVIS.Unity.Core.Monitoring;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Interfaces
{
    public interface IPolicyDriftPathProvider
    {
        /// <summary>
        /// Validates configuration, derives all paths for today's drift eval run,
        /// ensures root-level folders exist, and generates the execution ID.
        /// Returns a hard failure (with Kafka alert) if config keys are missing.
        /// </summary>
        Task<PolicyDriftContextResult> BuildDriftContextAsync();

        /// <summary>
        /// Same as above but for a specific date — backfill / reprocessing.
        /// </summary>
        Task<PolicyDriftContextResult> BuildDriftContextAsync(DateTime asOfUtc);

        /// <summary>
        /// Creates Processing/ and Processed/ subdirs under the source path.
        /// Call only after you've confirmed there's actual work to stage.
        /// </summary>
        void EnsureStagingDirectories(PolicyDriftContext context);

        /// <summary>
        /// Deterministic SHA-256 hash of the normalized source path.
        /// Same evalRoot + same date = same group identity, every time.
        /// </summary>
        string GenerateExecutionId(string path);
    }
}
