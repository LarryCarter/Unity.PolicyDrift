using CVIS.Unity.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Infrastructure.Services
{
    public class DriftComparisonService : IDriftComparisonService
    {
        private readonly IConfiguration _configuration;
        private readonly IUnityEventPublisher _publisher;

        // Config keys
        private const string RequireSnowTicketKey = "Governance:RequireSnowTicket";
        private const string DriftScopeSectionKey = "Governance:DriftScope";

        // File type prefixes that can be toggled
        private static readonly string[] ConfigurableScopes = { "XML", "DLL", "EXE" };

        public DriftComparisonService(IConfiguration configuration, IUnityEventPublisher publisher)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        }

        public bool RequireSnowTicket
        {
            get
            {
                var value = _configuration[RequireSnowTicketKey];
                return bool.TryParse(value, out var result) && result;
            }
        }

        public bool IsPromotionAllowed(string? snowTicketId)
        {
            if (!RequireSnowTicket)
                return true;

            return !string.IsNullOrWhiteSpace(snowTicketId);
        }

        public HashSet<string> GetActiveDriftScopes()
        {
            // INI is always active — it's the core configuration scope
            var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "INI" };

            foreach (var scope in ConfigurableScopes)
            {
                // Default to true if not configured — all scopes active unless explicitly disabled
                var configValue = _configuration[$"{DriftScopeSectionKey}:{scope}"];
                var isEnabled = string.IsNullOrEmpty(configValue) || (bool.TryParse(configValue, out var val) && val);

                if (isEnabled)
                    scopes.Add(scope);
            }

            return scopes;
        }

        public Dictionary<string, string> CompareAttributes(
            Dictionary<string, string> baseline,
            Dictionary<string, string> current)
        {
            var changes = new Dictionary<string, string>();

            // TODO: Feature Link - Replace this HashSet with a DB call to 'unity.IgnoreAttributes'
            var ignoreList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "INI:ApiVersion",
                "FILE_SIZE_placeholder.txt",
                "XML:LastModified"
            };

            var activeScopes = GetActiveDriftScopes();

            // Check for Modified or Removed
            foreach (var baseKvp in baseline)
            {
                if (ignoreList.Contains(baseKvp.Key)) continue;
                if (!IsInActiveScope(baseKvp.Key, activeScopes)) continue;

                if (!current.ContainsKey(baseKvp.Key))
                    changes[baseKvp.Key] = $"REMOVED (Was: {baseKvp.Value})";
                else if (current[baseKvp.Key] != baseKvp.Value)
                    changes[baseKvp.Key] = $"MODIFIED (Base: {baseKvp.Value} | Current: {current[baseKvp.Key]})";
            }

            // Check for Added
            foreach (var curKvp in current)
            {
                if (ignoreList.Contains(curKvp.Key)) continue;
                if (!IsInActiveScope(curKvp.Key, activeScopes)) continue;

                if (!baseline.ContainsKey(curKvp.Key))
                    changes[curKvp.Key] = $"ADDED (New Value: {curKvp.Value})";
            }

            return changes;
        }

        /// <summary>
        /// Checks if an attribute key belongs to an active drift scope.
        /// Keys are prefixed with scope: "INI:Timeout", "DLL:FileHash", "XML:PlatformBaseID"
        /// FILE_SIZE_ keys are treated as metadata and always included if their scope is active.
        /// </summary>
        private static bool IsInActiveScope(string key, HashSet<string> activeScopes)
        {
            var colonIndex = key.IndexOf(':');
            if (colonIndex <= 0)
            {
                // Keys like FILE_SIZE_xxx don't have a scope prefix — always include
                return true;
            }

            var prefix = key[..colonIndex];
            return activeScopes.Contains(prefix);
        }
    }
}
