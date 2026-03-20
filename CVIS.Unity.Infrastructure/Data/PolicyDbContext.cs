using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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

            // FIX: EF Core 8's OwnsOne(..., b => b.ToJson()) cannot materialize Dictionary<string, string>
            // because Dictionary has no CLR properties for the JSON shaper to map. This causes an
            // ArgumentOutOfRangeException in JsonEntityMaterializerRewriter on any query that reads these columns.
            //
            // Solution: Use ValueConversion to store as nvarchar(max) JSON strings.
            // The DB column content is identical — no migration needed — but EF treats it as a
            // plain string column with automatic serialization/deserialization.

            var dictConverter = new ValueConverter<Dictionary<string, string>, string>(
                v => JsonConvert.SerializeObject(v),
                v => JsonConvert.DeserializeObject<Dictionary<string, string>>(v)
                    ?? new Dictionary<string, string>());

            // ValueComparer is required so EF can detect changes to dictionary contents
            var dictComparer = new ValueComparer<Dictionary<string, string>>(
                (d1, d2) => JsonConvert.SerializeObject(d1) == JsonConvert.SerializeObject(d2),
                d => d == null ? 0 : JsonConvert.SerializeObject(d).GetHashCode(),
                d => JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    JsonConvert.SerializeObject(d)) ?? new Dictionary<string, string>());

            modelBuilder.Entity<PlatformBaseline>(entity =>
            {
                entity.ToTable("PlatformBaselines");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Attributes)
                    .HasConversion(dictConverter)
                    .Metadata.SetValueComparer(dictComparer);

                entity.Property(e => e.AttributesHash)
                    .HasConversion(dictConverter)
                    .Metadata.SetValueComparer(dictComparer);
            });

            modelBuilder.Entity<PolicyDriftEvalDetail>(entity =>
            {
                entity.ToTable("PolicyDriftEvalDetails");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Attributes)
                    .HasConversion(dictConverter)
                    .Metadata.SetValueComparer(dictComparer);

                entity.Property(e => e.AttributesHash)
                    .HasConversion(dictConverter)
                    .Metadata.SetValueComparer(dictComparer);
            });

            modelBuilder.Entity<PolicyDriftEval>(entity =>
            {
                entity.ToTable("PolicyDriftEval");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Differences)
                    .HasConversion(dictConverter)
                    .Metadata.SetValueComparer(dictComparer);
            });

            modelBuilder.Entity<PolicyEvent>(entity =>
            {
                entity.ToTable("PolicyEvents");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Metadata)
                    .HasConversion(dictConverter)
                    .Metadata.SetValueComparer(dictComparer);
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
        public async Task<(int OldVersion, int NewVersion)> UpsertBaselineAsync(
            string policyId,
            Dictionary<string, string> attributes,
            Dictionary<string, string>? hashes = null)
        {
            // 1. Find the current active baseline for this platform
            var existing = await PlatformBaselines
                .FirstOrDefaultAsync(b => b.PlatformId == policyId && b.IsActive);

            int oldVersion = 0;
            int newVersion = 1;

            // 2. Deactivate the old record if it exists
            if (existing != null)
            {
                oldVersion = existing.Version;
                newVersion = existing.Version + 1;
                existing.IsActive = false;
                existing.LastUpdate = DateTime.UtcNow;
            }

            // 3. Insert the new Gold Standard as the active record
            var newBaseline = new PlatformBaseline
            {
                Id = Guid.NewGuid(),
                PlatformId = policyId,
                Attributes = attributes,
                AttributesHash = hashes ?? new Dictionary<string, string>(),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                Version = newVersion
            };

            await PlatformBaselines.AddAsync(newBaseline);

            // 4. Single SaveChanges — deactivation + insertion are atomic
            await SaveChangesAsync();

            return (oldVersion, newVersion);
        }

        /// <summary>
        /// The Fast-Path Logic: Only creates a new Detail row if hashes have changed.
        /// </summary>
        public async Task<Guid> GetOrCreatePolicyDetailIdAsync(string policyId, Dictionary<string, string> attributes, Dictionary<string, string> currentHashes)
        {
            // FIX: With HasConversion, we can now query the full entity safely.
            // The raw SQL workaround is no longer needed, but keeping the optimized
            // projection pattern since we only need Id, DriftVersion, and the hash for comparison.
            var latestDetail = await PolicyDriftEvalDetails
                .AsNoTracking()
                .Where(d => d.PolicyId == policyId)
                .OrderByDescending(d => d.DriftVersion)
                .Select(d => new { d.Id, d.DriftVersion, d.AttributesHash })
                .FirstOrDefaultAsync();

            if (latestDetail != null)
            {
                if (AreHashesEqual(latestDetail.AttributesHash, currentHashes))
                {
                    return latestDetail.Id;
                }
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