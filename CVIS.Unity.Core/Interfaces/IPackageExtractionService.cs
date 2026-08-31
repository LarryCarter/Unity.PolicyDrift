using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Interfaces
{
    /// <summary>
    /// Handles ZIP extraction to a working directory.
    /// Owns the temp folder convention and run-isolated extraction paths.
    /// Uses IFileSystemService for directory creation and cleanup.
    /// </summary>
    public interface IPackageExtractionService
    {
        /// <summary>
        /// Extracts a ZIP stream into a run-isolated temp folder.
        /// Returns the extraction path — caller is responsible for cleanup.
        /// </summary>
        Task<string> ExtractAsync(Stream zipStream, string platformId);

        /// <summary>
        /// Cleans up a previously extracted working directory.
        /// </summary>
        void Cleanup(string extractionPath);
    }
}
