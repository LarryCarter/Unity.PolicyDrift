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
            _baselinePath = config["Storage:BaselinePath"] ?? "C:\\Baselines";

            // Datyrix: New dedicated temp folder for ZIP extractions
            _tempRoot = Path.Combine(Path.GetTempPath(), "CVIS_Unity_Working");

            if (!Directory.Exists(_tempRoot)) Directory.CreateDirectory(_tempRoot);
            if (!Directory.Exists(_baselinePath)) Directory.CreateDirectory(_baselinePath);
        }

        #region Existing Signal File Logic (Larry's Original)

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

        #region New Extraction & Cleanup Logic (Unity Workflow)

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
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex)
            {
                // Cryptorion: Log warning but don't break the main audit flow if a file handle is stuck
                _logger.LogWarning(ex, "Failed to clean up path: {Path}. A process may still have a handle.", path);
            }
        }

        #endregion
    }
}