using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Entities
{
    public class EventBus
    {
        [Key]
        public long Id { get; set; }

        [Required]
        [MaxLength(50)]
        public string EntityType { get; set; } = string.Empty;
        //This will be where the Primary Key Belongs to. This way you can tied it back.
        //'POLICY'(POLICYID), 'MMC', 'EVP', 'AD'

        [Required]
        [MaxLength(100)]
        public string EntityId { get; set; } = string.Empty;

        [Required]
        [MaxLength(50)]
        public string Domain { get; set; } = string.Empty;
        // 'PolicyDrift', 'MMC'

        [Required]
        [MaxLength(50)]
        public string SubDomain { get; set; } = string.Empty;
        // 'Baseline', 'Evaluation', 'S', 'Integration'

        [Required]
        [MaxLength(50)]
        public string EventType { get; set; } = string.Empty;
        // 'AUDIT', 'STATUS', 'DRIFT'

        [Required]
        [MaxLength(100)]
        public string EventName { get; set; } = string.Empty;
        // 'BASELINE_PROMOTED', 'DRIFT_DETECTED'

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public string? Actor { get; set; } = "System";

        public Dictionary<string, string> Metadata { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);

        [MaxLength(100)]
        public string? CorrelationId { get; set; }
    }
}
