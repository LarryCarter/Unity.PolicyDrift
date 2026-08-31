using CVIS.Unity.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Infrastructure.Services
{
    public class PackageExtractionService : IPackageExtractionService
    {
        private readonly IFileSystemService _fileSystem;
        private readonly ILogger<PackageExtractionService> _logger;
        private readonly string _tempRoot;

        public PackageExtractionService(
            IFileSystemService fileSystem,
            ILogger<PackageExtractionService> logger)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Cross-platform: Path.GetTempPath() returns the right thing on both OS
            _tempRoot = Path.Combine(Path.GetTempPath(), "CVIS_Unity_Working");
            _fileSystem.CreateDirectory(_tempRoot);
        }

        public async Task<string> ExtractAsync(Stream zipStream, string platformId)
        {
            var runId = Guid.NewGuid().ToString()[..8];
            var extractPath = Path.Combine(_tempRoot, platformId, runId);

            _logger.LogDebug(
                "Extracting package for {PlatformId} to {Path}", platformId, extractPath);

            _fileSystem.CreateDirectory(extractPath);

            using var archive = new ZipArchive(zipStream);
            await Task.Run(() => archive.ExtractToDirectory(extractPath, overwriteFiles: true));

            return extractPath;
        }

        public void Cleanup(string extractionPath)
        {
            _fileSystem.DeleteDirectory(extractionPath, recursive: true);
        }
    }
}
