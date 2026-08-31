using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Core.Models;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace CVIS.Unity.PolicyDrift.Orchestration.Services
{
    /// <summary>
    /// Extracts and parses ZIP packages into attribute bags and scoped hashes.
    /// Pure in-memory processing — no filesystem paths, no config, no side effects.
    /// Split across partial classes: this file = extraction + orchestration,
    /// FileProcessorParse.cs = INI/XML parsing logic.
    /// </summary>
    public partial class FileProcessor : IFileProcessor
    {
        public FileProcessor() { }

        // ─────────────────────────────────────────────────────────
        //  IFileProcessor — Primary Entry Points
        // ─────────────────────────────────────────────────────────

        public virtual async Task<Dictionary<string, string>> ExtractAndParseZipAsync(
            Stream zipStream, string policyId)
        {
            return await ProcessZipEntriesAsync(zipStream);
        }

        public virtual async Task<DiscoveryResult> ExtractAndParseZipWithHashesAsync(
            Stream zipStream, string policyId)
        {
            var result = new DiscoveryResult
            {
                PolicyId = policyId,
                Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var ext = Path.GetExtension(entry.Name).ToLowerInvariant().TrimStart('.');
                var scopeKey = ext.ToUpperInvariant();

                var fileBytes = await ReadEntryBytesAsync(entry);

                // Scoped hash for the fast-path comparison
                result.Hashes[scopeKey] = ComputeSha256Hex(fileBytes);

                // Parse based on extension
                switch (ext)
                {
                    case "ini":
                        ParseIni(fileBytes, result.Attributes);
                        break;
                    case "xml":
                        ParseXml(fileBytes, result.Attributes);
                        break;
                    case "exe":
                    case "dll":
                        result.Attributes[$"{scopeKey}:FileHash"] = result.Hashes[scopeKey];
                        result.Attributes[$"{scopeKey}:FileName"] = entry.Name;
                        break;
                }
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────
        //  Internal Processing
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Flat attribute extraction without scoped hashes.
        /// Used by the simpler ExtractAndParseZipAsync path.
        /// </summary>
        private async Task<Dictionary<string, string>> ProcessZipEntriesAsync(Stream zipStream)
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var fileBytes = await ReadEntryBytesAsync(entry);
                var ext = Path.GetExtension(entry.Name).ToLowerInvariant();

                // Metadata — applies to all files
                attributes[$"FILE_SIZE_{entry.Name}"] = entry.Length.ToString();

                // Content-specific parsing
                switch (ext)
                {
                    case ".ini":
                        ParseIni(fileBytes, attributes);
                        break;
                    case ".xml":
                        ParseXml(fileBytes, attributes);
                        break;
                    case ".exe":
                    case ".dll":
                        var scopeKey = ext.TrimStart('.').ToUpperInvariant();
                        attributes[$"{scopeKey}:FileHash"] = ComputeSha256Hex(fileBytes);
                        attributes[$"{scopeKey}:FileName"] = entry.Name;
                        break;
                }
            }

            return attributes;
        }

        // ─────────────────────────────────────────────────────────
        //  Shared Helpers
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Reads a ZIP entry fully into a byte array.
        /// Scopes the streams tightly so handles release immediately.
        /// </summary>
        private static async Task<byte[]> ReadEntryBytesAsync(ZipArchiveEntry entry)
        {
            using var entryStream = entry.Open();
            using var ms = new MemoryStream();
            await entryStream.CopyToAsync(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// Single source of truth for hashing.
        /// Returns lowercase hex string — consistent across all callers.
        /// </summary>
        private static string ComputeSha256Hex(byte[] bytes)
        {
            var hashBytes = SHA256.HashData(bytes);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
    }
}