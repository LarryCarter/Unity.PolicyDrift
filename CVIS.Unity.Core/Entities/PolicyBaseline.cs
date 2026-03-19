using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Entities
{
    public class PolicyBaseline
    {
        [Key]
        public string PlatformID { get; set; } // PK
        public bool IsActive { get; set; }
        public required string PlatformType { get; set; } // Windows, Unix, etc.

        // Navigation for all the granular INI/XML bits
        public required List<PlatformAttribute> Attributes { get; set; }
        public DateTime LastBaselineSync { get; set; }
    }
}
