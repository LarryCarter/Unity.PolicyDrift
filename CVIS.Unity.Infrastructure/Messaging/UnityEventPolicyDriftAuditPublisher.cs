using System.Text.Json;
using CVIS.Unity.Core.Audit;
using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Infrastructure.Data;

namespace CVIS.Unity.Infrastructure.Messaging;

public sealed class UnityEventPolicyDriftAuditPublisher : IPolicyDriftAuditPublisher
{
    private readonly PolicyDbContext _db;

    public UnityEventPolicyDriftAuditPublisher(PolicyDbContext db) => _db = db;

    public Task PublishAsync(
        PolicyDriftAuditEnvelope envelope,
        CancellationToken cancellationToken = default) =>
        _db.SaveUnityEventAsync(
            entityType: "POLICY_DRIFT_REPORT",
            entityId: envelope.Audit.ExecutionId,
            domain: "PolicyDrift",
            subDomain: "Audit",
            eventName: "REPORT_AUDIT_COMPLETED",
            eventType: "AUDIT",
            correlationId: envelope.EventId.ToString(),
            meta: new Dictionary<string, string>
            {
                ["Schema"] = envelope.Schema,
                ["SchemaVersion"] = envelope.SchemaVersion.ToString(),
                ["AuditJson"] = JsonSerializer.Serialize(envelope),
                ["SqlArtifact"] = envelope.Audit.SqlEvidence.ArtifactName,
                ["SqlSha256"] = envelope.Audit.SqlEvidence.Sha256,
                ["TransportStatus"] = "PERSISTED_NOT_SENT_TO_KAFKA"
            });
}
