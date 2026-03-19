using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using CVIS.Unity.Core.Models;

namespace CVIS.Unity.PolicyDrift.ConsoleHost.Services
{
    /// <summary>
    /// Core extraction engine for CyberArk Platform ZIP packages.
    /// Normalizes INI, XML, and Binary artifacts into a flat attribute dictionary.
    /// </summary>
    public class FileProcessor
    {
        public async Task<PolicyPackage> ExtractAndParseZipAsync(Stream zipStream, string policyId)
        {
            var package = new PolicyPackage { PolicyId = policyId };

            // CodeVyrn: Open ZIP in-memory to maintain speed and cross-platform compatibility
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    // Skip folders, process only files
                    if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith("/")) continue;

                    var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                    using var entryStream = entry.Open();

                    switch (extension)
                    {
                        case ".ini":
                            await ParseIniEntry(entryStream, package.Attributes);
                            break;

                        case ".xml":
                            ParseXmlEntry(entryStream, package.Attributes, entry.Name);
                            break;

                        case ".exe":
                        case ".dll":
                            // Binary validation via SHA-256 Hash
                            var hash = CalculateFileHash(entryStream);
                            package.Attributes[$"FILE_HASH_{entry.Name}"] = hash;
                            break;

                        default:
                            // Log existence of other files (like .sample or .txt) to detect structural drift
                            package.Attributes[$"FILE_EXISTS_{entry.Name}"] = "True";
                            break;
                    }
                }
            }

            return package;
        }

        private async Task ParseIniEntry(Stream stream, Dictionary<string, string> attributes)
        {
            // CyberArk INIs can vary; UTF8 is the safest default for .NET 8
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                var trimmed = line.Trim();
                // Ignore comments (;), section headers ([]), and whitespace
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith(";") || trimmed.StartsWith("["))
                    continue;

                if (trimmed.Contains('='))
                {
                    var parts = trimmed.Split('=', 2);
                    var key = $"INI_{parts[0].Trim()}";
                    attributes[key] = parts[1].Trim();
                }
            }
        }

        private void ParseXmlEntry(Stream stream, Dictionary<string, string> attributes, string fileName)
        {
            try
            {
                var doc = XDocument.Load(stream);
                // Datyrix: Crawl all nodes to capture every attribute setting
                foreach (var element in doc.Descendants())
                {
                    foreach (var attr in element.Attributes())
                    {
                        // Structure: XML_[File]_[Element]_[Attribute]
                        var key = $"XML_{fileName}_{element.Name.LocalName}_{attr.Name.LocalName}";
                        attributes[key] = attr.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                attributes[$"ERROR_XML_{fileName}"] = $"Parsing failed: {ex.Message}";
            }
        }

        private string CalculateFileHash(Stream stream)
        {
            using var sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(stream);
            // Convert to a clean, lowercase hex string for DB storage
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }
}