using CVIS.Unity.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace CVIS.Unity.Infrastructure.Services
{
    /// <summary>
    /// Pure filesystem implementation. No config, no domain paths, no state.
    /// Retry logic on MoveFile preserved — file lock contention is an I/O concern.
    /// Works identically on Windows VMs and Linux containers.
    /// </summary>
    public class FileSystemService : IFileSystemService
    {
        private readonly ILogger<FileSystemService> _logger;

        public FileSystemService(ILogger<FileSystemService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // ── Directory Operations ──────────────────────────────────

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public string[] GetFilesInDirectory(string path, string searchPattern)
            => Directory.GetFiles(path, searchPattern);

        // ── File Operations ───────────────────────────────────────

        public bool FileExists(string path) => File.Exists(path);

        public void MoveFile(string source, string destination)
        {
            var destDir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            const int maxRetries = 3;
            const int delayMs = 2000;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    File.Move(source, destination, overwrite: true);
                    return;
                }
                catch (IOException ex) when (IsFileLocked(ex))
                {
                    if (attempt == maxRetries - 1)
                    {
                        throw new InvalidOperationException(
                            $"CRITICAL: File {source} remains locked after {maxRetries} attempts.", ex);
                    }

                    _logger.LogWarning(
                        "File {File} is locked. Attempt {Attempt}/{Max}. Retrying in {Delay}ms...",
                        source, attempt + 1, maxRetries, delayMs);

                    Thread.Sleep(delayMs);
                }
            }
        }

        public void DeleteFile(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        public Stream OpenRead(string path) => File.OpenRead(path);

        public string ReadAllText(string path) => File.ReadAllText(path);

        // ── Cleanup ───────────────────────────────────────────────

        public void DeleteDirectory(string path, bool recursive)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    _logger.LogDebug("Deleting directory: {Path} (recursive: {Recursive})", path, recursive);
                    Directory.Delete(path, recursive);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to delete directory: {Path}. A process may still have a handle.", path);
            }
        }

        // ── Private ───────────────────────────────────────────────

        private static bool IsFileLocked(IOException exception)
        {
            int errorCode = Marshal.GetHRForException(exception) & 0xFFFF;
            return errorCode == 32 || errorCode == 33;
        }
    }
}