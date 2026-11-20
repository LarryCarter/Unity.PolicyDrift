using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Interfaces
{
    /// <summary>
    /// Signal file operations for baseline triggers.
    /// Owns the convention: policyId → {policyId}.txt in the baseline folder.
    /// The file content contains the ServiceNow (SNOW) ticket ID authorizing the change.
    /// </summary>
    public interface ISignalFileService
    {
        bool Exists(string baselineFolder, string policyId);

        /// <summary>
        /// Reads the signal file content and extracts the SNOW ticket ID.
        /// Returns null if the file is empty or unreadable.
        /// </summary>
        string? ReadTicketId(string baselineFolder, string policyId);

        /// <summary>
        /// Deletes the signal file after successful baseline promotion.
        /// Logs and continues if deletion fails — does not throw.
        /// </summary>
        bool TryDelete(string baselineFolder, string policyId);

        string GetFullPath(string baselineFolder, string fileName);
    }
}
