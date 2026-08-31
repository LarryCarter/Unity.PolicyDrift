using CVIS.Unity.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Interfaces
{
    public interface IFileProcessor
    {
        Task<Dictionary<string, string>> ExtractAndParseZipAsync(Stream zipStream, string policyId);
        Task<DiscoveryResult> ExtractAndParseZipWithHashesAsync(Stream zipStream, string policyId);
    }
}
