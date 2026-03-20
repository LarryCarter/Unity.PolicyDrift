using System;
using System.Linq;

namespace CVIS.Unity.Core.Entities
{
    public class MasterPolicyBaseline : BaselineEntity
    {
        public bool IsActive { get; set; }
        public string RuleName { get; set; }
        // Using EF8 .ToJson() mapping
        public Dictionary<string, string> Parameters { get; set; } = new(); 
    }
}
