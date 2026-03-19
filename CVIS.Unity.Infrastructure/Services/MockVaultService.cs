using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using CVIS.Unity.Core.Interfaces;

namespace CVIS.Unity.Infrastructure.Services
{
    public class MockVaultService : ICyberArkVaultService
    {
        public async Task<Stream> GetPlatformPackageAsync(string platformId)
        {
            var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                var entry = archive.CreateEntry("Policy.ini");
                using var writer = new StreamWriter(entry.Open());
                await writer.WriteLineAsync("[Policy]");
                await writer.WriteLineAsync($"PlatformName={platformId}");
                await writer.WriteLineAsync("Timeout=30");
            }
            ms.Position = 0;
            return ms;
        }
    }
}