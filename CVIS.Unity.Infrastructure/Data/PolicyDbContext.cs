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
        public DbSet<EventBus> UnityEvents { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("unity");

            var dictConverter = new ValueConverter<Dictionary<string, string>, string>(
                v => JsonConvert.SerializeObject(v),
                v => JsonConvert.DeserializeObject<Dictionary<string, string>>(v)
                    ?? new Dictionary<string, string>());

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

            // Keep existing PolicyEvents mapping — do not remove
            modelBuilder.Entity<PolicyEvent>(entity =>
            {
                entity.ToTable("PolicyEvents");
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Metadata)
                    .HasConversion(dictConverter)
                    .Metadata.SetValueComparer(dictComparer);
            });

            // New UnityEvents event bus
            modelBuilder.Entity<EventBus>(entity =>
            {
                entity.ToTable("UnityEvents");
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => new { e.EntityType, e.EntityId })
                    .HasDatabaseName("IX_UnityEvents_EntityType_EntityId");

                entity.HasIndex(e => new { e.Domain, e.SubDomain })
                    .HasDatabaseName("IX_UnityEvents_Domain_SubDomain");

                entity.HasIndex(e => e.CorrelationId)
                    .HasDatabaseName("IX_UnityEvents_CorrelationId");

                entity.HasIndex(e => e.Timestamp)
                    .HasDatabaseName("IX_UnityEvents_Timestamp");

                entity.Property(e => e.Metadata)
                    .HasConversion(dictConverter)
                    .Metadata.SetValueComparer(dictComparer);
            });
        }

        /// <summary>
        /// Domain-agnostic event bus writer.
        /// Replaces SavePolicyEventAsync for all new MCC event writes.
        /// </summary>
        public async Task SaveUnityEventAsync(
            string entityType,
            string entityId,
            string domain,
            string subDomain,
            string eventName,
            string eventType,
            string? correlationId = null,
            string actor = "System",
            object? meta = null)
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
                    normalizedMetadata = new Dictionary<string, string>
                        { { "RawData", meta.ToString() ?? "Unknown" } };
                }
            }

            var unityEvent = new EventBus
            {
                EntityType = entityType,
                EntityId = entityId,
                Domain = domain,
                SubDomain = subDomain,
                EventName = eventName,
                EventType = eventType,
                CorrelationId = correlationId,
                Actor = actor,
                Metadata = normalizedMetadata,
                Timestamp = DateTime.UtcNow
            };

            await UnityEvents.AddAsync(unityEvent);
            await SaveChangesAsync();
        }

        /// <summary>
        /// Legacy — kept for backward compatibility with existing PolicyDrift callers.
        /// New MCC code should use SaveUnityEventAsync directly.
        /// </summary>
        public async Task SavePolicyEventAsync(
            string policyId,
            string eventName,
            string eventType,
            object? meta = null)
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
                    normalizedMetadata = new Dictionary<string, string>
                        { { "RawData", meta.ToString() ?? "Unknown" } };
                }
            }

            // Legacy write — kept during transition period
            var policyEvent = new PolicyEvent
            {
                PolicyId = policyId,
                EventName = eventName,
                EventType = eventType,
                Metadata = normalizedMetadata,
                Timestamp = DateTime.UtcNow,
                Actor = "System"
            };

            await PolicyEvents.AddAsync(policyEvent);

            // Forward write to new event bus
            var unityEvent = new EventBus
            {
                EntityType = "POLICY",
                EntityId = policyId,
                Domain = "PolicyDrift",
                SubDomain = ResolveSubDomain(eventName),
                EventName = eventName,
                EventType = eventType,
                Metadata = normalizedMetadata,
                Timestamp = DateTime.UtcNow,
                Actor = "System"
            };

            await UnityEvents.AddAsync(unityEvent);
            await SaveChangesAsync();
        }

        /// <summary>
        /// Resolves SubDomain from legacy EventName during bridge period.
        /// </summary>
        private static string ResolveSubDomain(string eventName) =>
            eventName.ToUpperInvariant() switch
            {
                var n when n.Contains("BASELINE") => "Baseline",
                var n when n.Contains("DRIFT") => "Evaluation",
                var n when n.Contains("SCAN") => "Scan",
                var n when n.Contains("SNOW") => "Integration",
                _ => "General"
            };

        /// <summary>
        /// Updates an existing baseline or creates a new one.
        /// History is preserved: old record is deactivated, new record inserted with version+1.
        /// </summary>
        public async Task<(int OldVersion, int NewVersion)> UpsertBaselineAsync(
            string policyId,
            Dictionary<string, string> attributes,
            Dictionary<string, string>? hashes = null,
            string? snowTicketId = null)
        {
            var existing = await PlatformBaselines
                .FirstOrDefaultAsync(b => b.PlatformId == policyId && b.IsActive);

            int oldVersion = 0;
            int newVersion = 1;

            if (existing != null)
            {
                oldVersion = existing.Version;
                newVersion = existing.Version + 1;
                existing.IsActive = false;
                existing.LastUpdate = DateTime.UtcNow;
            }

            var newBaseline = new PlatformBaseline
            {
                Id = Guid.NewGuid(),
                PlatformId = policyId,
                Attributes = attributes,
                AttributesHash = hashes ?? new Dictionary<string, string>(),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                Version = newVersion,
                LastSNOWTicket = snowTicketId
            };

            await PlatformBaselines.AddAsync(newBaseline);
            await SaveChangesAsync();

            return (oldVersion, newVersion);
        }

        /// <summary>
        /// The Fast-Path Logic: Only creates a new Detail row if hashes have changed.
        /// </summary>
        public async Task<Guid> GetOrCreatePolicyDetailIdAsync(
            string policyId,
            Dictionary<string, string> attributes,
            Dictionary<string, string> currentHashes)
        {
            var latestDetail = await PolicyDriftEvalDetails
                .AsNoTracking()
                .Where(d => d.PolicyId == policyId)
                .OrderByDescending(d => d.DriftVersion)
                .Select(d => new { d.Id, d.DriftVersion, d.AttributesHash })
                .FirstOrDefaultAsync();

            if (latestDetail != null)
            {
                if (AreHashesEqual(latestDetail.AttributesHash, currentHashes))
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