using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace CVIS.Unity.Core.Entities
{
    public abstract class BaselineEntity
    {
        [Key]
        public string Id { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }
}
