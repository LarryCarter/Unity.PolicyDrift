using CVIS.Unity.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Extensions
{
    public static class AttributeExtensions
    {
        // Async-compatible extension to allow direct piping from the FileProcessor
        public static async Task<DiscoveryResult> ToDiscoveryResultAsync(
            this Task<(Dictionary<string, string> Attributes, Dictionary<string, string> Hashes)> task,
            string policyId)
        {
            var result = await task;
            return new DiscoveryResult
            {
                PolicyId = policyId,
                Attributes = result.Attributes,
                Hashes = result.Hashes
            };
        }
    }
}
