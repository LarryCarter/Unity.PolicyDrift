using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CVIS.Unity.PolicyDrift.Orchestration.Services
{
    public partial class FileProcessor
    {
        /// <summary>
        /// Refactored: Parses INI content into the attribute bag using the "INI:" scope prefix.
        /// Logic: Skips comments, section headers, and empty lines to focus on operational settings.
        /// </summary>
        /// <summary>
        /// Entry Point A: Processes raw bytes from the ZIP extraction.
        /// </summary>
        public void ParseIni(byte[] content, Dictionary<string, string> attributes)
        {
            var text = Encoding.UTF8.GetString(content);
            ParseIniCore(text, attributes);
        }

        /// <summary>
        /// Entry Point B: Processes a stream (Legacy/Mock support).
        /// </summary>
        public async Task ParseIni(Stream stream, Dictionary<string, string> attributes)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            ParseIniCore(text, attributes);
        }

        /// <summary>
        /// The Single System of Record for INI parsing logic.
        /// </summary>
        public void ParseIniCore(string content, Dictionary<string, string> attributes)
        {
            var lines = content.Split(new[] { "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // Datyrix: Skip comments, headers, and whitespace
                if (string.IsNullOrWhiteSpace(trimmed) ||
                    trimmed.StartsWith(";") ||
                    trimmed.StartsWith("["))
                    continue;

                if (trimmed.Contains('='))
                {
                    var parts = trimmed.Split('=', 2);

                    // CodeVyrn: Standardize on the "INI:" prefix
                    var key = $"INI:{parts[0].Trim()}";
                    var value = parts[1].Trim();

                    // Cryptorion: Last value wins to prevent duplicate key crashes
                    attributes[key] = value;
                }
            }
        }

        /// <summary>
        /// Refactored: Parses XML content from a byte array into the attribute bag.
        /// Logic: Uses XDocument to flatten the tree into "XML:Element_Attribute" pairs.
        /// </summary>
        public void ParseXml(byte[] fileBytes, Dictionary<string, string> attributes)
        {
            try
            {
                // Datyrix: Convert bytes to memory stream for XDocument loading
                using var ms = new MemoryStream(fileBytes);
                var doc = XDocument.Load(ms);

                foreach (var element in doc.Descendants())
                {
                    var elementName = element.Name.LocalName;

                    // 1. Map Attributes to Synthetic Keys
                    foreach (var attr in element.Attributes())
                    {
                        // Format: XML:[ElementName]_[AttributeName]
                        var key = $"XML:{elementName}_{attr.Name.LocalName}";
                        attributes[key] = attr.Value;
                    }

                    // 2. Map Leaf Node Values
                    if (!element.HasElements && !string.IsNullOrWhiteSpace(element.Value))
                    {
                        var key = $"XML:{elementName}_Value";
                        // Cryptorion: Only add if not already defined by an attribute
                        if (!attributes.ContainsKey(key))
                        {
                            attributes[key] = element.Value.Trim();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Record the failure as an attribute for the Drift Engine
                attributes["XML:ParseError"] = $"Exception: {ex.Message}";
            }
        }
    }
}