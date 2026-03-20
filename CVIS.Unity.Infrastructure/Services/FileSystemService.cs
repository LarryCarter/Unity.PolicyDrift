using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CVIS.Unity.Core.Interfaces;

namespace CVIS.Unity.Infrastructure.Services
{
    public class FileSystemService : IFileSystemService
    {
        private readonly string _baselinePath;
        private readonly string _tempRoot;
        private readonly ILogger<FileSystemService> _logger;

        public FileSystemService(IConfiguration config, ILogger<FileSystemService> logger)
        {
            _logger = logger;
            // Your existing baseline logic
            _baselinePath = config["Storage:BaselinePath"] ?? "C:\\CVIS\\PolicyDrift\\BaselineUpdate";
            _baselinePath = config["Monitoring:UpdatePolicyFolder"] ?? "";

            // New dedicated temp folder for ZIP extractions
            _tempRoot = Path.Combine(Path.GetTempPath(), "CVIS_Unity_Working");

            if (!Directory.Exists(_tempRoot)) Directory.CreateDirectory(_tempRoot);
            if (!Directory.Exists(_baselinePath)) Directory.CreateDirectory(_baselinePath);
        }

        // --- Section 1: Directory & Batch Management ---
        public bool DirectoryExists(string path) => Directory.Exists(path);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public string[] GetFilesInDirectory(string path, string searchPattern)
            => Directory.GetFiles(path, searchPattern);
        public void MoveFile(string source, string destination)
        {
            // Ensure the destination directory exists before moving
            var destDir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            // Retry logic to handle "File In Use" scenarios
            int maxRetries = 3;
            int delayMs = 2000; // 2-second breather between attempts

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    // Overwrite existing files in processing if a batch is restarted
                    File.Move(source, destination, overwrite: true);
                    return; // Success, exit the loop
                }
                catch (IOException ex) when (IsFileLocked(ex))
                {
                    if (i == maxRetries - 1)
                    {
                        // Cryptorion: Throw a specific exception for the Orchestrator to catch
                        throw new InvalidOperationException($"CRITICAL: File {source} remains locked after {maxRetries} attempts.", ex);
                    }

                    _logger.LogWarning("File {File} is locked. Retrying in {Delay}ms...", source, delayMs);
                    Thread.Sleep(delayMs);
                }
            }
        }

        private bool IsFileLocked(IOException exception)
        {
            // Datyrix: Standard HResult for file sharing violations
            int errorCode = System.Runtime.InteropServices.Marshal.GetHRForException(exception) & 0xFFFF;
            return errorCode == 32 || errorCode == 33; // 32: Sharing violation, 33: Lock violation
        }

        public Stream OpenRead(string path) => File.OpenRead(path);

        #region Extraction & Cleanup Logic (Unity Workflow)

        // --- Section 2: Extraction & Cleanup ---
        public async Task<string> ExtractPlatformPackage(Stream zipStream, string platformId)
        {
            // CodeVyrn: Create a unique sub-folder for this specific run
            var runId = Guid.NewGuid().ToString().Substring(0, 8);
            var extractPath = Path.Combine(_tempRoot, platformId, runId);

            _logger.LogDebug("Extracting package for {PlatformId} to {Path}", platformId, extractPath);

            if (!Directory.Exists(extractPath)) Directory.CreateDirectory(extractPath);

            using var archive = new ZipArchive(zipStream);
            await Task.Run(() => archive.ExtractToDirectory(extractPath, overwriteFiles: true));

            return extractPath;
        }

        public void Cleanup(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    _logger.LogDebug("Cleaning up temporary directory: {Path}", path);
                    if (Directory.Exists(path)) 
                        Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex)
            {
                // Log warning but don't break the main audit flow if a file handle is stuck
                _logger.LogWarning(ex, "Failed to clean up path: {Path}. A process may still have a handle.", path);
            }
        }
        #endregion

        #region Existing Signal File Logic

        public bool SignalFileExists(string policyId)
        {
            var path = Path.Combine(_baselinePath, $"{policyId}.txt");
            return File.Exists(path);
        }

        public void DeleteSignalFile(string policyId)
        {
            var path = Path.Combine(_baselinePath, $"{policyId}.txt");
            if (File.Exists(path)) File.Delete(path);
        }

        public string GetFullPath(string fileName)
        {
            return Path.Combine(_baselinePath, fileName);
        }

        #endregion

      
    }
}