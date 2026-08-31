using Microsoft.Extensions.Configuration;

namespace CVIS.Unity.PolicyDrift.Orchestrator.Reporting;

public static class PolicyDriftEnvironment
{
    public const string ConfigurationKey = "POLICYDRIFT_ENVIRONMENT";

    public static string Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var value = configuration[ConfigurationKey]?.Trim().ToUpperInvariant();
        return value switch
        {
            "UAT" => value,
            "PROD" => value,
            _ => throw new InvalidOperationException(
                $"Configuration '{ConfigurationKey}' must be set to UAT or PROD.")
        };
    }

    public static string BuildReportBadge(string environment) =>
        $"CVIS Unity - Policy Drift Governance | Environment: {Validate(environment)}";

    public static string BuildEmailSubject(
        string environment,
        string executionId,
        DateTime generatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        var batchId = executionId.Length > 8 ? executionId[..8] : executionId;
        return $"[{Validate(environment)}] Unity Policy Drift Report - Batch {batchId} | {generatedAtUtc:yyyy-MM-dd}";
    }

    private static string Validate(string environment)
    {
        var value = environment?.Trim().ToUpperInvariant();
        return value is "UAT" or "PROD"
            ? value
            : throw new ArgumentException("Environment must be UAT or PROD.", nameof(environment));
    }
}
