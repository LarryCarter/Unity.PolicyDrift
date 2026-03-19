using System.ComponentModel.DataAnnotations;

namespace CVIS.Unity.Core.Entities;

public class PlatformBaseline
{
    [Key]
    public Guid Id { get; set; } // Internal Version ID

    [Required]
    public string PlatformId { get; set; } // e.g., "ANS-SA-N-R"

    public string? PlatformName { get; set; }

    public int Version { get; set; } // Incremental: 1, 2, 3...

    public bool IsActive { get; set; } // Current authorized version

    /// <summary>
    /// Prefixed keys: "INI:Timeout", "XML:PlatformBaseID", etc.
    /// Maps to JSON in SQL via EF8 .ToJson()
    /// </summary>
    public Dictionary<string, string> Attributes { get; set; } = new();

    /// <summary>
    /// Scoped Hashes: { "INI": "hash", "XML": "hash", "DLL": "hash" }
    /// </summary>
    public Dictionary<string, string> AttributesHash { get; set; } = new();

    public string? LastSNOWTicket { get; set; } // Change authorization

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUpdate { get; set; } // Populated when version is retired
}