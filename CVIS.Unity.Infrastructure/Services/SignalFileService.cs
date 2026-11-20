using CVIS.Unity.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Infrastructure.Services
{
    public class SignalFileService : ISignalFileService
    {
        private readonly IFileSystemService _fileSystem;
        private readonly ILogger<SignalFileService> _logger;

        public SignalFileService(IFileSystemService fileSystem, ILogger<SignalFileService> logger)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool Exists(string baselineFolder, string policyId)
        {
            var path = BuildSignalPath(baselineFolder, policyId);
            return _fileSystem.FileExists(path);
        }

        public string? ReadTicketId(string baselineFolder, string policyId)
        {
            var path = BuildSignalPath(baselineFolder, policyId);
            try
            {
                var content = _fileSystem.ReadAllText(path).Trim();
                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger.LogWarning(
                        "Signal file for {PolicyId} exists but is empty — no SNOW ticket ID found.", policyId);
                    return null;
                }

                // File may contain just the ticket ID on the first line,
                // or additional metadata below. We take the first line.
                var ticketId = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.Trim();

                _logger.LogDebug(
                    "Read SNOW ticket {TicketId} from signal file for {PolicyId}.", ticketId, policyId);
                return ticketId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to read signal file content for {PolicyId}. " +
                    "Baseline promotion will proceed without SNOW ticket.", policyId);
                return null;
            }
        }

        public bool TryDelete(string baselineFolder, string policyId)
        {
            var path = BuildSignalPath(baselineFolder, policyId);
            try
            {
                _fileSystem.DeleteFile(path);
                return true;
            }
            catch (Exception ex)
            {
                // Per diagram: "Log delete failing → Continue"
                _logger.LogWarning(ex,
                    "Failed to delete signal file for {PolicyId} at {Path}. " +
                    "Baseline promotion succeeded but signal file remains — manual cleanup may be needed.",
                    policyId, path);
                return false;
            }
        }

        public string GetFullPath(string baselineFolder, string fileName)
        {
            return Path.Combine(baselineFolder, fileName);
        }

        private static string BuildSignalPath(string baselineFolder, string policyId)
        {
            return Path.Combine(baselineFolder, $"{policyId}.txt");
        }
    }
}
