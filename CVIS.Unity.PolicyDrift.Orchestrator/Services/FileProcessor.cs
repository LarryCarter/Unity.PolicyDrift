using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace CVIS.Unity.PolicyDrift.Orchestration.Services
{
    public partial class FileProcessor
    {
        public async Task<Dictionary<string, string>> ExtractAndParseZipAsync(Stream zipStream, string policyId)
        {
            // We reuse the ProcessZipAsync logic to return the normalized attribute bag
            return await ProcessZipAsync(zipStream);
        }

        public async Task<(Dictionary<string, string> Attributes, Dictionary<string, string> Hashes)> ExtractAndParseZipWithHashesAsync(Stream zipStream, string policyId)
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var ext = Path.GetExtension(entry.Name).ToLowerInvariant().TrimStart('.');
                var scopeKey = ext.ToUpperInvariant(); // e.g., "INI", "XML", "DLL"

                using var stream = entry.Open();
                // We must copy to memory because we need to read the stream twice (Hash + Parse)
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var fileBytes = ms.ToArray();

                // 1. Generate Scoped Hash for the Fast-Path
                hashes[scopeKey] = CalculateHash(fileBytes);

                // 2. Parse based on Extension
                if (ext == "ini")
                {
                    ParseIni(fileBytes, attributes);
                }
                else if (ext == "xml")
                {
                    ParseXml(fileBytes, attributes);
                }
                else if (ext == "exe" || ext == "dll")
                {
                    // Datyrix: For binaries, the hash is the primary attribute
                    attributes[$"{scopeKey}:FileHash"] = hashes[scopeKey];
                    attributes[$"{scopeKey}:FileName"] = entry.Name;
                }
            }

            return (attributes, hashes);
        }

        private string CalculateHash(byte[] bytes)
        {
            var hashBytes = SHA256.HashData(bytes);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

      

        private string GenerateSha256(byte[] bytes)
        {
            var hashBytes = SHA256.HashData(bytes);
            return Convert.ToHexString(hashBytes);
        }

        public async Task<Dictionary<string, string>> ProcessZipAsync(Stream zipStream)
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                byte[] fileBytes;

                // Scope the streams so they close the moment we have our byte array
                using (var entryStream1 = entry.Open())
                {
                    using (var ms = new MemoryStream())
                    {
                        await entryStream1.CopyToAsync(ms);
                        fileBytes = ms.ToArray();
                    }
                    // ms is disposed here
                }

                var ext = Path.GetExtension(entry.Name).ToLowerInvariant();

                // 1. Metadata attributes (Applies to ALL files)
                attributes[$"FILE_SIZE_{entry.Name}"] = entry.Length.ToString();

                // 2. Content-specific attributes using the standardized Scoped Prefix
                switch (ext)
                {
                    case ".ini":
                        ParseIni(fileBytes, attributes); // Corrected to use byte[]
                        break;
                    case ".xml":
                        ParseXml(fileBytes, attributes); // Corrected to match byte[] signature
                        break;
                    case ".exe":
                    case ".dll":
                        {
                            // Binary Guard
                            var scopeKey = ext.TrimStart('.').ToUpperInvariant();
                            var hash = CalculateHash(fileBytes); // Corrected to use byte[]
                            attributes[$"{scopeKey}:FileHash"] = hash;
                            attributes[$"{scopeKey}:FileName"] = entry.Name;
                            break;
                        }
                }
            }

            return attributes;
        }

        private string CalculateHash(Stream stream)
        {
            using var sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }
}