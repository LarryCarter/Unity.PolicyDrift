using CVIS.Unity.Core.Audit;
using CVIS.Unity.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CVIS.Unity.Infrastructure.Audit;

public sealed class PolicyDriftAuditService
{
    private const string ExportPrefix = "Exporting policy ZIP for platform \"";
    private readonly PolicyDbContext _db;

    public PolicyDriftAuditService(PolicyDbContext db) =>
        _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<PolicyDriftAuditEnvelope> BuildAsync(
        PolicyDriftAuditRequest request,
        PolicyDriftAuditSqlEvidence sqlEvidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutionId);

        var validCaIds = _db.CaEpvReportingAuditRows
            .AsNoTracking()
            .Where(row => row.PolicyId != null && row.PolicyId != "null")
            .Select(row => row.PolicyId! )
            .Distinct();

        var executionRows = _db.PolicyDriftEvals
            .AsNoTracking()
            .Where(row => EF.Functions.Like(row.ExecutionId, request.ExecutionId + "%"));

        var executionPolicyIds = executionRows
            .Where(row => row.PolicyId != null)
            .Select(row => row.PolicyId)
            .Distinct();

        var excludedPolicyIds = await validCaIds
            .Where(policyId => !executionPolicyIds.Contains(policyId))
            .OrderBy(policyId => policyId)
            .ToListAsync(cancellationToken);

        var pdeOnly = await executionPolicyIds
            .Where(policyId => !validCaIds.Contains(policyId))
            .CountAsync(cancellationToken);

        var caRows = await _db.CaEpvReportingAuditRows
            .AsNoTracking()
            .Where(row => row.PolicyId != null && excludedPolicyIds.Contains(row.PolicyId))
            .Select(row => new PolicyDriftAuditCaRow(row.PolicyId!, row.CafDeletionDate))
            .ToListAsync(cancellationToken);

        var logStart = request.ReportDate.AddDays(-1).ToDateTime(TimeOnly.MinValue);
        var logEnd = request.ReportDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var logs = await _db.LogEventAuditRows
            .AsNoTracking()
            .Where(row => row.Timestamp >= logStart && row.Timestamp < logEnd)
            .OrderBy(row => row.Id)
            .Select(row => new PolicyDriftAuditLogRow(row.Id, row.Message, row.Timestamp))
            .ToListAsync(cancellationToken);

        var summary = new PolicyDriftAuditSummary(
            PoliciesInCa: await validCaIds.CountAsync(cancellationToken),
            TotalExcluded: excludedPolicyIds.Count,
            TotalProcessed: await validCaIds.CountAsync(cancellationToken) - excludedPolicyIds.Count,
            NoDrift: await executionRows.CountAsync(row => row.Status == "NO_DRIFT", cancellationToken),
            DriftDetected: await executionRows.CountAsync(row => row.Status == "DRIFT", cancellationToken),
            MissingBaseline: await executionRows.CountAsync(row => row.Status == "MISSING_BASELINE", cancellationToken),
            PdeOnly: pdeOnly);

        var excluded = BuildExcludedPolicyAudits(excludedPolicyIds, caRows, logs);
        var payload = new PolicyDriftAuditPayload(
            request.ReportTitle,
            request.ExecutionId,
            request.ReportDate,
            summary,
            excluded,
            sqlEvidence);

        return PolicyDriftAuditEnvelope.Create(payload);
    }

    public static IReadOnlyList<PolicyDriftExcludedPolicyAudit> BuildExcludedPolicyAudits(
        IEnumerable<string> excludedPolicyIds,
        IEnumerable<PolicyDriftAuditCaRow> caRows,
        IEnumerable<PolicyDriftAuditLogRow> logs)
    {
        var caByPolicy = caRows
            .GroupBy(row => row.PolicyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var findings = ParseLogFindings(logs);

        return excludedPolicyIds.Select(policyId =>
        {
            caByPolicy.TryGetValue(policyId, out var matchingCaRows);
            findings.TryGetValue(policyId, out var finding);
            var deletionState = PolicyDriftAuditClassifier.GetDeletionState(
                matchingCaRows?.Select(row => row.DeletionDate) ?? []);
            var filtered = PolicyDriftAuditClassifier.FilterPolicyIdForAudit(policyId);
            var filterReason = PolicyDriftAuditClassifier.GetFilterReason(policyId, filtered);
            var mutation = filterReason != null;
            var http400 = finding?.HasHttp400 ?? false;
            var badRequest = finding?.HasBadRequest ?? false;
            var noZip = finding?.HasNoZip ?? false;

            return new PolicyDriftExcludedPolicyAudit(
                policyId,
                deletionState,
                filtered,
                mutation,
                filterReason,
                http400,
                badRequest,
                noZip,
                finding?.FirstIssueLogId,
                finding?.FirstIssueTimestamp,
                PolicyDriftAuditClassifier.GetExclusionReason(
                    mutation, deletionState, http400, badRequest, noZip));
        })
        .OrderBy(row => row.ExclusionReason == "DUE TO OTHER REASON" ? 2 : row.IsPolicyIdMutation ? 0 : 1)
        .ThenBy(row => row.PolicyId, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    private static Dictionary<string, LogFinding> ParseLogFindings(
        IEnumerable<PolicyDriftAuditLogRow> logs)
    {
        var blocks = new List<List<PolicyDriftAuditLogRow>>();
        List<PolicyDriftAuditLogRow>? current = null;

        foreach (var log in logs.OrderBy(row => row.Id))
        {
            if (log.Message.StartsWith(ExportPrefix, StringComparison.Ordinal))
            {
                current = [];
                blocks.Add(current);
            }

            current?.Add(log);
        }

        var result = new Dictionary<string, LogFinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in blocks)
        {
            var policyId = block
                .Where(row => row.Message.StartsWith(ExportPrefix, StringComparison.Ordinal))
                .Select(row => ParsePolicyId(row.Message))
                .FirstOrDefault(value => value != null)
                ?? block.Select(row => ParsePolicyId(row.Message)).FirstOrDefault(value => value != null);

            if (policyId == null)
                continue;

            var issues = block.Where(IsIssue).ToArray();
            result[policyId] = new LogFinding(
                block.Any(row => row.Message.Contains(" - 400", StringComparison.Ordinal)),
                block.Any(row => row.Message.Contains("400 Bad Request", StringComparison.Ordinal)),
                block.Any(row => row.Message.Contains("No ZIP content returned for platform \"", StringComparison.Ordinal)),
                issues.Select(row => (long?)row.Id).Min(),
                issues.Select(row => (DateTime?)row.Timestamp).Min());
        }

        return result;
    }

    private static bool IsIssue(PolicyDriftAuditLogRow row) =>
        row.Message.Contains(" - 400", StringComparison.Ordinal) ||
        row.Message.Contains("400 Bad Request", StringComparison.Ordinal) ||
        row.Message.Contains("No ZIP content returned for platform \"", StringComparison.Ordinal);

    private static string? ParsePolicyId(string message)
    {
        const string platformMarker = "platform \"";
        var quotedStart = message.IndexOf(platformMarker, StringComparison.Ordinal);
        if (quotedStart >= 0)
        {
            quotedStart += platformMarker.Length;
            var quotedEnd = message.IndexOf('"', quotedStart);
            if (quotedEnd > quotedStart)
                return message[quotedStart..quotedEnd];
        }

        const string urlStartMarker = "/Platforms/";
        const string urlEndMarker = "/Export";
        var urlStart = message.IndexOf(urlStartMarker, StringComparison.Ordinal);
        var urlEnd = message.IndexOf(urlEndMarker, StringComparison.Ordinal);
        return urlStart >= 0 && urlEnd > urlStart
            ? message[(urlStart + urlStartMarker.Length)..urlEnd]
            : null;
    }

    private sealed record LogFinding(
        bool HasHttp400,
        bool HasBadRequest,
        bool HasNoZip,
        long? FirstIssueLogId,
        DateTime? FirstIssueTimestamp);
}
