using System.Security.Cryptography;
using CVIS.Unity.Core.Audit;

namespace CVIS.Unity.Infrastructure.Audit;

public static class PolicyDriftAuditSqlEvidenceFactory
{
    public static async Task<PolicyDriftAuditSqlEvidence> CreateAsync(
        string sqlArtifactPath,
        PolicyDriftAuditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlArtifactPath);
        var sqlBytes = await File.ReadAllBytesAsync(sqlArtifactPath, cancellationToken);
        var hash = SHA256.HashData(sqlBytes);

        return new PolicyDriftAuditSqlEvidence(
            Path.GetFileName(sqlArtifactPath),
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
