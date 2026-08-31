namespace CVIS.Unity.Core.Audit;

public sealed record PolicyDriftAuditParityMismatch(
    string Scope,
    string Field,
    string? PolicyId,
    string Expected,
    string Actual);

public sealed record PolicyDriftAuditParityReport(
    bool IsMatch,
    IReadOnlyList<PolicyDriftAuditParityMismatch> Mismatches);

public static class PolicyDriftAuditParity
{
    public static PolicyDriftAuditParityReport Compare(
        PolicyDriftAuditPayload reference,
        PolicyDriftAuditPayload application)
    {
        var mismatches = new List<PolicyDriftAuditParityMismatch>();
        CompareSummary(reference.Summary, application.Summary, mismatches);

        var expectedByPolicy = reference.ExcludedPolicies.ToDictionary(
            row => row.PolicyId, StringComparer.OrdinalIgnoreCase);
        var actualByPolicy = application.ExcludedPolicies.ToDictionary(
            row => row.PolicyId, StringComparer.OrdinalIgnoreCase);

        foreach (var policyId in expectedByPolicy.Keys.Union(
                     actualByPolicy.Keys, StringComparer.OrdinalIgnoreCase))
        {
            expectedByPolicy.TryGetValue(policyId, out var expected);
            actualByPolicy.TryGetValue(policyId, out var actual);
            if (expected == null || actual == null)
            {
                mismatches.Add(new(
                    "Policy", "Presence", policyId,
                    expected == null ? "absent" : "present",
                    actual == null ? "absent" : "present"));
                continue;
            }

            if (!string.Equals(
                    expected.ExclusionReason,
                    actual.ExclusionReason,
                    StringComparison.Ordinal))
            {
                mismatches.Add(new(
                    "Policy", "ExclusionReason", policyId,
                    expected.ExclusionReason, actual.ExclusionReason));
            }
        }

        return new(mismatches.Count == 0, mismatches);
    }

    private static void CompareSummary(
        PolicyDriftAuditSummary expected,
        PolicyDriftAuditSummary actual,
        ICollection<PolicyDriftAuditParityMismatch> mismatches)
    {
        Compare("PoliciesInCa", expected.PoliciesInCa, actual.PoliciesInCa);
        Compare("TotalExcluded", expected.TotalExcluded, actual.TotalExcluded);
        Compare("TotalProcessed", expected.TotalProcessed, actual.TotalProcessed);
        Compare("NoDrift", expected.NoDrift, actual.NoDrift);
        Compare("DriftDetected", expected.DriftDetected, actual.DriftDetected);
        Compare("MissingBaseline", expected.MissingBaseline, actual.MissingBaseline);
        Compare("PdeOnly", expected.PdeOnly, actual.PdeOnly);
        return;

        void Compare(string field, int expectedValue, int actualValue)
        {
            if (expectedValue != actualValue)
                mismatches.Add(new(
                    "Summary", field, null,
                    expectedValue.ToString(), actualValue.ToString()));
        }
    }
}
