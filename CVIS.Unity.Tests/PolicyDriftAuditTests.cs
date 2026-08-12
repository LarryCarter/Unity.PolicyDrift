using CVIS.Unity.Core.Audit;
using CVIS.Unity.Infrastructure.Audit;
using NUnit.Framework;

namespace CVIS.Unity.Tests;

[TestFixture]
public class PolicyDriftAuditTests
{
    [TestCase(false, "NO", false, "DUE TO OTHER REASON")]
    [TestCase(false, "NO", true, "NO_ZIP_OR_400_LOG_ISSUE")]
    [TestCase(false, "YES", false, "DELETED")]
    [TestCase(false, "YES+", true, "DELETED_AND_LOG_ISSUE")]
    [TestCase(true, "NO", false, "SPECIAL_CHARACTER_MUTATION")]
    [TestCase(true, "NO", true, "SPECIAL_CHARACTER_MUTATION_AND_NO_ZIP_OR_400_LOG_ISSUE")]
    [TestCase(true, "YES", false, "SPECIAL_CHARACTER_MUTATION_AND_DELETED")]
    [TestCase(true, "YES+", true, "SPECIAL_CHARACTER_MUTATION_AND_DELETED_AND_LOG_ISSUE")]
    public void ExclusionReason_PreservesSqlPrecedence(
        bool mutation,
        string deletionState,
        bool logIssue,
        string expected)
    {
        var actual = PolicyDriftAuditClassifier.GetExclusionReason(
            mutation, deletionState, logIssue, false, false);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void DeletionState_DistinguishesAllThreeCaStates()
    {
        Assert.Multiple((TestDelegate)(() =>
        {
            Assert.That(
                PolicyDriftAuditClassifier.GetDeletionState(new DateTime?[] { null, null }),
                Is.EqualTo("NO"));
            Assert.That(
                PolicyDriftAuditClassifier.GetDeletionState(new DateTime?[] { DateTime.UtcNow }),
                Is.EqualTo("YES"));
            Assert.That(
                PolicyDriftAuditClassifier.GetDeletionState(new DateTime?[] { null, DateTime.UtcNow }),
                Is.EqualTo("YES+"));
        }));
    }

    [TestCase("ABC-123", "ABC-123", null)]
    [TestCase("AB_C 123", "ABC123", "CSHARP_POLICY_FILTER_MUTATED_POLICYID")]
    [TestCase("$%^", "", "CSHARP_POLICY_FILTER_REMOVED_ALL_CHARACTERS")]
    public void CompatibilityFilter_MatchesReferenceSql(
        string policyId,
        string expectedFiltered,
        string? expectedReason)
    {
        var filtered = PolicyDriftAuditClassifier.FilterPolicyIdForAudit(policyId);

        Assert.Multiple((TestDelegate)(() =>
        {
            Assert.That(filtered, Is.EqualTo(expectedFiltered));
            Assert.That(
                PolicyDriftAuditClassifier.GetFilterReason(policyId, filtered),
                Is.EqualTo(expectedReason));
        }));
    }

    [Test]
    public void BuildExcludedPolicyAudits_CorrelatesLogBlockAndEvidence()
    {
        var issueTime = new DateTime(2026, 5, 4, 12, 30, 0, DateTimeKind.Utc);
        var result = PolicyDriftAuditService.BuildExcludedPolicyAudits(
            new[] { "Policy_1" },
            new[]
            {
                new PolicyDriftAuditCaRow("Policy_1", null),
                new PolicyDriftAuditCaRow("Policy_1", issueTime)
            },
            new[]
            {
                new PolicyDriftAuditLogRow(
                    10, "Exporting policy ZIP for platform \"Policy_1\"", issueTime.AddMinutes(-1)),
                new PolicyDriftAuditLogRow(
                    11, "Export failed - 400 Bad Request", issueTime)
            });

        var audit = result.Single();
        Assert.Multiple((TestDelegate)(() =>
        {
            Assert.That(audit.DeletionState, Is.EqualTo("YES+"));
            Assert.That(audit.FilteredPolicyId, Is.EqualTo("Policy1"));
            Assert.That(audit.HasHttp400, Is.True);
            Assert.That(audit.HasBadRequest, Is.True);
            Assert.That(audit.FirstIssueLogId, Is.EqualTo(11));
            Assert.That(audit.FirstIssueTimestamp, Is.EqualTo(issueTime));
            Assert.That(
                audit.ExclusionReason,
                Is.EqualTo("SPECIAL_CHARACTER_MUTATION_AND_DELETED_AND_LOG_ISSUE"));
        }));
    }

    [Test]
    public void ParityComparison_FailsForPerPolicyReasonMismatch()
    {
        var summary = new PolicyDriftAuditSummary(1, 1, 0, 0, 0, 0, 0);
        var evidence = new PolicyDriftAuditSqlEvidence(
            "audit.sql", "abc", "SELECT 1;", new Dictionary<string, string>());
        var referencePolicy = new PolicyDriftExcludedPolicyAudit(
            "Policy-1", "NO", "Policy-1", false, null,
            false, false, false, null, null, "DUE TO OTHER REASON");
        var actualPolicy = referencePolicy with { ExclusionReason = "DELETED" };
        var reference = new PolicyDriftAuditPayload(
            "title", "execution", new DateOnly(2026, 5, 4),
            summary, new[] { referencePolicy }, evidence);
        var actual = reference with { ExcludedPolicies = new[] { actualPolicy } };

        var report = PolicyDriftAuditParity.Compare(reference, actual);

        Assert.Multiple((TestDelegate)(() =>
        {
            Assert.That(report.IsMatch, Is.False);
            Assert.That(report.Mismatches, Has.Count.EqualTo(1));
            Assert.That(report.Mismatches[0].PolicyId, Is.EqualTo("Policy-1"));
            Assert.That(report.Mismatches[0].Field, Is.EqualTo("ExclusionReason"));
        }));
    }
}
