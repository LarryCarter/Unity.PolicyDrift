using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using CVIS.Unity.Core.Entities;

namespace CVIS.Unity.Infrastructure.Data
{
    public class PolicyDbContext : DbContext
    {
        public PolicyDbContext(DbContextOptions<PolicyDbContext> options) : base(options) { }

        public DbSet<PlatformBaseline> PlatformBaselines { get; set; }
        public DbSet<PolicyDriftEval> PolicyDriftEvals { get; set; }
        public DbSet<PolicyDriftEvalDetail> PolicyDriftEvalDetails { get; set; }
        public DbSet<PolicyEvent> PolicyEvents { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // CodeVyrn: Explicitly set the unity schema
            modelBuilder.HasDefaultSchema("unity");

            modelBuilder.Entity<PlatformBaseline>(entity =>
            {
                entity.ToTable("PlatformBaselines");
                entity.HasKey(e => e.Id);
                // EF8 native JSON mapping
                entity.OwnsOne(e => e.Attributes, b => b.ToJson());
                entity.OwnsOne(e => e.AttributesHash, b => b.ToJson());
            });

            modelBuilder.Entity<PolicyDriftEvalDetail>(entity =>
            {
                entity.ToTable("PolicyDriftEvalDetails");
                entity.HasKey(e => e.Id);
                entity.OwnsOne(e => e.Attributes, b => b.ToJson());
                entity.OwnsOne(e => e.AttributesHash, b => b.ToJson());
            });

            modelBuilder.Entity<PolicyDriftEval>(entity =>
            {
                entity.ToTable("PolicyDriftEval");
                entity.HasKey(e => e.Id);
                entity.OwnsOne(e => e.Differences, b => b.ToJson());
            });

            modelBuilder.Entity<PolicyEvent>(entity =>
            {
                entity.ToTable("PolicyEvents");
                entity.HasKey(e => e.Id);
                entity.OwnsOne(e => e.Metadata, b => b.ToJson());
            });
        }

        /// <summary>
        /// Records a high-value System of Record event to the unity.PolicyEvents table.
        /// This acts as the local proxy for future Kafka Event Streaming.
        /// </summary>
        public async Task SavePolicyEventAsync(string policyId, string eventName, string eventType, object? meta = null)
        {
            Dictionary<string, string> normalizedMetadata;

            if (meta == null)
            {
                normalizedMetadata = new Dictionary<string, string>();
            }
            else if (meta is Dictionary<string, string> directDict)
            {
                normalizedMetadata = directDict;
            }
            else
            {
                try
                {
                    var json = JsonConvert.SerializeObject(meta);
                    normalizedMetadata = JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                                        ?? new Dictionary<string, string>();
                }
                catch
                {
                    normalizedMetadata = new Dictionary<string, string> { { "RawData", meta.ToString() ?? "Unknown" } };
                }
            }

            var policyEvent = new PolicyEvent
            {
                PolicyId = policyId,
                EventName = eventName,
                EventType = eventType,
                Metadata = normalizedMetadata,
                Timestamp = DateTime.UtcNow,
                Actor = "System"
            };

            await this.PolicyEvents.AddAsync(policyEvent);
            await this.SaveChangesAsync();
        }

        /// <summary>
        /// Updates an existing baseline or creates a new one.
        /// </summary>
        public async Task UpsertBaselineAsync(string policyId, Dictionary<string, string> attributes, Dictionary<string, string>? hashes = null)
        {
            var existing = await PlatformBaselines
                .FirstOrDefaultAsync(b => b.PlatformId == policyId && b.IsActive);

            if (existing != null)
            {
                existing.Attributes = attributes;
                existing.AttributesHash = hashes ?? new Dictionary<string, string>();
                existing.LastUpdate = DateTime.UtcNow;
            }
            else
            {
                var newBaseline = new PlatformBaseline
                {
                    Id = Guid.NewGuid(),
                    PlatformId = policyId,
                    Attributes = attributes,
                    AttributesHash = hashes ?? new Dictionary<string, string>(),
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    Version = 1
                };
                await PlatformBaselines.AddAsync(newBaseline);
            }

            await SaveChangesAsync();
        }

        /// <summary>
        /// The Fast-Path Logic: Only creates a new Detail row if hashes have changed.
        /// </summary>
        public async Task<Guid> GetOrCreatePolicyDetailIdAsync(string policyId, Dictionary<string, string> attributes, Dictionary<string, string> currentHashes)
        {
            var latestDetail = await PolicyDriftEvalDetails
                .Where(d => d.PolicyId == policyId)
                .OrderByDescending(d => d.DriftVersion)
                .FirstOrDefaultAsync();

            if (latestDetail != null && AreHashesEqual(latestDetail.AttributesHash, currentHashes))
            {
                return latestDetail.Id;
            }

            var newDetail = new PolicyDriftEvalDetail
            {
                Id = Guid.NewGuid(),
                PolicyId = policyId,
                DriftVersion = (latestDetail?.DriftVersion ?? 0) + 1,
                Attributes = attributes,
                AttributesHash = currentHashes,
                CreatedAt = DateTime.UtcNow
            };

            await PolicyDriftEvalDetails.AddAsync(newDetail);
            await SaveChangesAsync();

            return newDetail.Id;
        }

        public async Task LogDriftEvalAsync(PolicyDriftEval eval)
        {
            await PolicyDriftEvals.AddAsync(eval);
            await SaveChangesAsync();
        }

        private bool AreHashesEqual(Dictionary<string, string> h1, Dictionary<string, string> h2)
        {
            if (h1.Count != h2.Count) return false;
            return h1.OrderBy(kvp => kvp.Key).SequenceEqual(h2.OrderBy(kvp => kvp.Key));
        }
    }
}