using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Interfaces
{
    public interface IFileSystemService
    {
        /// <summary>
        /// Checks if a {PolicyID}.txt file exists in the configured signal folder.
        /// </summary>
        bool SignalFileExists(string policyId);

        /// <summary>
        /// Deletes the signal file once the baseline update is complete.
        /// </summary>
        void DeleteSignalFile(string policyId);

        /// <summary>
        /// Resolves the full path to a file in a cross-platform manner.
        /// </summary>
        string GetFullPath(string fileName);

        // New workflow methods for CyberArk ZIP handling
        Task<string> ExtractPlatformPackage(Stream zipStream, string platformId);
        void Cleanup(string path);
    }
}
