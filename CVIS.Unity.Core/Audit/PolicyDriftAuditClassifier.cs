namespace CVIS.Unity.Core.Audit;

public static class PolicyDriftAuditClassifier
{
    public static string GetDeletionState(IEnumerable<DateTime?> deletionDates)
    {
        var states = deletionDates.Select(value => value.HasValue).Distinct().ToArray();
        return states.Length switch
        {
            0 => "NOT_FOUND_IN_CA_EPV_REPORTING",
            1 when states[0] => "YES",
            1 => "NO",
            _ => "YES+"
        };
    }

    public static string FilterPolicyIdForAudit(string? policyId)
    {
        if (string.IsNullOrEmpty(policyId))
            return string.Empty;

        // TODO: MERGE INTEGRATION
        // Replace this compatibility implementation with the existing Policy Drift
        // PolicyID filtering function from the source application. The audit must
        // invoke the same production function used during normal processing.
        return new string(policyId.Where(character =>
            character is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            >= '0' and <= '9' or '-').ToArray());
    }

    public static string? GetFilterReason(string original, string filtered) =>
        string.IsNullOrEmpty(filtered)
            ? "CSHARP_POLICY_FILTER_REMOVED_ALL_CHARACTERS"
            : !string.Equals(original, filtered, StringComparison.Ordinal)
                ? "CSHARP_POLICY_FILTER_MUTATED_POLICYID"
                : null;

    public static string GetExclusionReason(
        bool filterMutation,
        string deletionState,
        bool hasHttp400,
        bool hasBadRequest,
        bool hasNoZip)
    {
        var deleted = deletionState is "YES" or "YES+";
        var logIssue = hasHttp400 || hasBadRequest || hasNoZip;

        return (filterMutation, deleted, logIssue) switch
        {
            (true, true, true) => "SPECIAL_CHARACTER_MUTATION_AND_DELETED_AND_LOG_ISSUE",
            (true, true, false) => "SPECIAL_CHARACTER_MUTATION_AND_DELETED",
            (true, false, true) => "SPECIAL_CHARACTER_MUTATION_AND_NO_ZIP_OR_400_LOG_ISSUE",
            (true, false, false) => "SPECIAL_CHARACTER_MUTATION",
            (false, true, true) => "DELETED_AND_LOG_ISSUE",
            (false, true, false) => "DELETED",
            (false, false, true) => "NO_ZIP_OR_400_LOG_ISSUE",
            _ => "DUE TO OTHER REASON"
        };
    }
}
