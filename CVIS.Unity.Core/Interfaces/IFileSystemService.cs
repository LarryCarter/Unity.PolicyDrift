using System;
using System.IO;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Interfaces
{
    /// <summary>
    /// Refactored: Provides atomic filesystem operations for Platform Batch Monitoring.
    /// Logic: Supports directory management, file staging, and signal detection.
    /// </summary>
    public interface IFileSystemService
    {
        // 1. Directory Management (Section 1 Readiness)
        bool DirectoryExists(string path);
        void CreateDirectory(string path);
        string[] GetFilesInDirectory(string path, string searchPattern);

        // 2. Atomic Orchestration (Drop -> Processing -> Processed)
        void MoveFile(string source, string destination);
        Stream OpenRead(string path);

        // 3. Signal Gate Logic (Baseline Trigger)
        bool SignalFileExists(string path);
        void DeleteSignalFile(string path);

        // 4. Cleanup and Extraction
        Task<string> ExtractPlatformPackage(Stream zipStream, string platformId);
        void Cleanup(string path);
        string GetFullPath(string fileName);
    }
}