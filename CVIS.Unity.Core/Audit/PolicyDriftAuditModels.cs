using System.Text.Json.Serialization;

namespace CVIS.Unity.Core.Audit;

public sealed record PolicyDriftAuditRequest(
    string ExecutionId,
    DateOnly ReportDate,
    string ReportTitle);

public sealed record PolicyDriftAuditSummary(
    int PoliciesInCa,
    int TotalExcluded,
    int TotalProcessed,
    int NoDrift,
    int DriftDetected,
    int MissingBaseline,
    int PdeOnly);

public sealed record PolicyDriftExcludedPolicyAudit(
    string PolicyId,
    string DeletionState,
    string FilteredPolicyId,
    bool IsPolicyIdMutation,
    string? PolicyIdFilterReason,
    bool HasHttp400,
    bool HasBadRequest,
    bool HasNoZip,
    long? FirstIssueLogId,
    DateTime? FirstIssueTimestamp,
    string ExclusionReason);

public sealed record PolicyDriftAuditSqlEvidence(
    string ArtifactName,
    string Sha256,
    string ExecutableSql,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record PolicyDriftAuditPayload(
    string ReportTitle,
    string ExecutionId,
    DateOnly ReportDate,
    PolicyDriftAuditSummary Summary,
    IReadOnlyList<PolicyDriftExcludedPolicyAudit> ExcludedPolicies,
    PolicyDriftAuditSqlEvidence SqlEvidence);

public sealed record PolicyDriftAuditEnvelope(
    string Schema,
    int SchemaVersion,
    Guid EventId,
    DateTime OccurredAtUtc,
    PolicyDriftAuditPayload Audit)
{
    [JsonIgnore]
    public const string CurrentSchema = "com.contollo.policydrift.audit";

    public static PolicyDriftAuditEnvelope Create(
        PolicyDriftAuditPayload audit,
        DateTime? occurredAtUtc = null) =>
        new(CurrentSchema, 1, Guid.NewGuid(), occurredAtUtc ?? DateTime.UtcNow, audit);
}

public sealed record PolicyDriftAuditCaRow(string PolicyId, DateTime? DeletionDate);

public sealed record PolicyDriftAuditEvaluationRow(string PolicyId, string Status);

public sealed record PolicyDriftAuditLogRow(long Id, string Message, DateTime Timestamp);
