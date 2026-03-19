using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace CVIS.Unity.Core.Entities
{
    public class PolicyEvent
    {
        [Key]
        public long Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string PolicyId { get; set; } = string.Empty;

        [Required]
        [MaxLength(50)]
        public string EventType { get; set; } = string.Empty; // e.g., "AUDIT", "STATUS", "DRIFT"

        [Required]
        [MaxLength(100)]
        public string EventName { get; set; } = string.Empty; // e.g., "BASELINE_UPDATED", "DRIFT_DETECTED"

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Stores the event payload (e.g., specific attributes that drifted) as JSON.
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Actor { get; set; } = "System";
    }
}