using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace CVIS.Unity.Core.Entities
{
    public class PlatformAttribute
    {
        public int Id { get; set; }
        public string PlatformID { get; set; }
        public string SourceFile { get; set; } // "INI" or "XML"
        public string Section { get; set; }    // e.g. [Main]
        public string Key { get; set; }        // e.g. PromptTimeout
        public string Value { get; set; }      // e.g. 30
    }
}
