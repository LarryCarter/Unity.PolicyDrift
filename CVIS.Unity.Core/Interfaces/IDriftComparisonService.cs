using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Interfaces
{
    /// <summary>
    /// Configuration-driven drift comparison and baseline governance rules.
    /// Shared by all workflows to ensure consistent drift detection behavior.
    /// </summary>
    public interface IDriftComparisonService
    {
        /// <summary>
        /// Compares current attributes against baseline, respecting:
        /// - The ignore list (keys that never count as drift)
        /// - The drift scope (which file type prefixes count as drift vs. audit-only)
        /// Returns only the differences that constitute actionable drift.
        /// </summary>
        Dictionary<string, string> CompareAttributes(
            Dictionary<string, string> baseline,
            Dictionary<string, string> current);

        /// <summary>
        /// Checks whether a baseline promotion should proceed given the SNOW ticket state.
        /// Returns true if promotion is allowed, false if it should be rejected.
        /// </summary>
        bool IsPromotionAllowed(string? snowTicketId);

        /// <summary>
        /// Whether SNOW tickets are required for baseline promotions.
        /// Read from config: Governance:RequireSnowTicket (default: false)
        /// </summary>
        bool RequireSnowTicket { get; }

        /// <summary>
        /// Returns the set of file type prefixes currently enabled for drift detection.
        /// Always includes INI. Others (XML, DLL, EXE) are configurable.
        /// </summary>
        HashSet<string> GetActiveDriftScopes();
    }
}
