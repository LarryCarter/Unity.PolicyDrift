using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Models
{
    public class DiscoveryResult
    {
        public string PolicyId { get; set; }

        // Key-Value pairs of all .ini, .xml, and .dll settings
        public Dictionary<string, string> Attributes { get; set; } = new();

        // Scoped Hashes (e.g., "INI" -> "HASH_VALUE")
        public Dictionary<string, string> Hashes { get; set; } = new();

        public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    }
}
