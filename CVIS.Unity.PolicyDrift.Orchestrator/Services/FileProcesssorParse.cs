using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CVIS.Unity.PolicyDrift.Orchestration.Services
{
    /// <summary>
    /// Partial: INI and XML parsing logic for FileProcessor.
    /// Pure parsing — byte[] in, attributes out, no side effects.
    /// </summary>
    public partial class FileProcessor
    {
        // ─────────────────────────────────────────────────────────
        //  INI Parsing
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Entry point from ZIP extraction — converts bytes to string,
        /// delegates to ParseIniCore.
        /// </summary>
        public void ParseIni(byte[] content, Dictionary<string, string> attributes)
        {
            var text = Encoding.UTF8.GetString(content);
            ParseIniCore(text, attributes);
        }

        /// <summary>
        /// Single system of record for INI parsing.
        /// Skips comments (;), section headers ([...]), and empty lines.
        /// Keys are scoped with "INI:" prefix. Last value wins on duplicates.
        /// </summary>
        public void ParseIniCore(string content, Dictionary<string, string> attributes)
        {
            var lines = content.Split(
                new[] { "\n", "\r\n" },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmed) ||
                    trimmed.StartsWith(';') ||
                    trimmed.StartsWith('['))
                    continue;

                if (trimmed.Contains('='))
                {
                    var parts = trimmed.Split('=', 2);
                    var key = $"INI:{parts[0].Trim()}";
                    var value = parts[1].Trim();

                    // Last value wins — prevents duplicate key crashes
                    attributes[key] = value;
                }
            }
        }

        // ─────────────────────────────────────────────────────────
        //  XML Parsing
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Flattens XML into "XML:Element_Attribute" pairs.
        /// Leaf node values captured as "XML:Element_Value".
        /// Parse errors recorded as attributes for the drift engine.
        /// </summary>
        public void ParseXml(byte[] fileBytes, Dictionary<string, string> attributes)
        {
            try
            {
                using var ms = new MemoryStream(fileBytes);
                var doc = XDocument.Load(ms);

                foreach (var element in doc.Descendants())
                {
                    var elementName = element.Name.LocalName;

                    // Map attributes to synthetic keys
                    foreach (var attr in element.Attributes())
                    {
                        var key = $"XML:{elementName}_{attr.Name.LocalName}";
                        attributes[key] = attr.Value;
                    }

                    // Map leaf node values (only if not already defined by an attribute)
                    if (!element.HasElements && !string.IsNullOrWhiteSpace(element.Value))
                    {
                        var key = $"XML:{elementName}_Value";
                        if (!attributes.ContainsKey(key))
                        {
                            attributes[key] = element.Value.Trim();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                attributes["XML:ParseError"] = $"Exception: {ex.Message}";
            }
        }
    }
}