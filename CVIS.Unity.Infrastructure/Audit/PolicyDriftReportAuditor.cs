using CVIS.Unity.Core.Audit;
using CVIS.Unity.Core.Interfaces;

namespace CVIS.Unity.Infrastructure.Audit;

public sealed class PolicyDriftReportAuditor : IPolicyDriftReportAuditor
{
    private const string SqlResourceSuffix = "policy-drift-audit-reference.sql";
    private readonly PolicyDriftAuditService _auditService;
    private readonly IPolicyDriftAuditPublisher _publisher;

    public PolicyDriftReportAuditor(
        PolicyDriftAuditService auditService,
        IPolicyDriftAuditPublisher publisher)
    {
        _auditService = auditService;
        _publisher = publisher;
    }

    public async Task AuditReportAsync(
        string executionId,
        DateOnly reportDate,
        CancellationToken cancellationToken = default)
    {
        var reportTitle = $"{executionId} | {reportDate:yyyy-MM-dd}";
        var request = new PolicyDriftAuditRequest(executionId, reportDate, reportTitle);
        var sqlEvidence = await CreateEmbeddedSqlEvidenceAsync(request, cancellationToken);
        var envelope = await _auditService.BuildAsync(request, sqlEvidence, cancellationToken);
        await _publisher.PublishAsync(envelope, cancellationToken);
    }

    private static async Task<PolicyDriftAuditSqlEvidence> CreateEmbeddedSqlEvidenceAsync(
        PolicyDriftAuditRequest request,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(PolicyDriftReportAuditor).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(SqlResourceSuffix, StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The reference audit SQL is not embedded.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var sqlBytes = buffer.ToArray();
        var hash = System.Security.Cryptography.SHA256.HashData(sqlBytes);

        return new(
            SqlResourceSuffix,
            Convert.ToHexString(hash).ToLowerInvariant(),
            System.Text.Encoding.UTF8.GetString(sqlBytes),
            new Dictionary<string, string>
            {
                ["ExecutionID"] = request.ExecutionId,
                ["ReportDate"] = request.ReportDate.ToString("yyyy-MM-dd"),
                ["ReportTitle"] = request.ReportTitle
            });
    }
}
