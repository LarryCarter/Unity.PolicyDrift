using System.ComponentModel.DataAnnotations;

namespace CVIS.Unity.Core.Entities;

public class PolicyDriftEvalDetail
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string PolicyId { get; set; } // Lookup link

    public int DriftVersion { get; set; } // Generation number

    /// <summary>
    /// Full extracted configuration with source prefixes
    /// </summary>
    public Dictionary<string, string> Attributes { get; set; } = new();

    /// <summary>
    /// Scoped hashes for the Fast-Path check
    /// </summary>
    public Dictionary<string, string> AttributesHash { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}