using System.ComponentModel.DataAnnotations;

namespace CVIS.Unity.Core.Entities;

public class PolicyDriftEval
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string PolicyId { get; set; }

    public string? PolicyName { get; set; }

    public Guid BaselinePolicyID { get; set; } // FK to PlatformBaseline.Id

    public Guid PolicyDriftEvalDetailsID { get; set; } // FK to PolicyDriftEvalDetail.Id

    /// <summary>
    /// Only the drifted keys (e.g., "INI:Interval": "720")
    /// Null if Status is NO_DRIFT
    /// </summary>
    public Dictionary<string, string>? Differences { get; set; }

    [Required]
    public string Status { get; set; } // NO_DRIFT, DRIFT, INITIAL_MISSING_BASELINE

    public DateTime RunTimestamp { get; set; } = DateTime.UtcNow;

    public string ExecutionId { get; set; } // Batch grouping ID
}