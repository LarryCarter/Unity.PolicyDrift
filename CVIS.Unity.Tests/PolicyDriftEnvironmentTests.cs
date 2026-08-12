using CVIS.Unity.PolicyDrift.Orchestrator.Reporting;
using Microsoft.Extensions.Configuration;

namespace CVIS.Unity.Tests;

[TestFixture]
public class PolicyDriftEnvironmentTests
{
    [TestCase("UAT", "UAT")]
    [TestCase("uat", "UAT")]
    [TestCase("  PROD  ", "PROD")]
    public void Resolve_ValidValue_NormalizesToUppercase(string configured, string expected)
    {
        var configuration = BuildConfiguration(configured);

        Assert.That(PolicyDriftEnvironment.Resolve(configuration), Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("DEV")]
    public void Resolve_MissingOrUnsupportedValue_Throws(string? configured)
    {
        var configuration = BuildConfiguration(configured);

        var exception = Assert.Throws<InvalidOperationException>(
            () => PolicyDriftEnvironment.Resolve(configuration));
        Assert.That(exception!.Message, Does.Contain(PolicyDriftEnvironment.ConfigurationKey));
    }

    [Test]
    public void BuildReportBadge_IncludesNormalizedEnvironment()
    {
        Assert.That(
            PolicyDriftEnvironment.BuildReportBadge("uat"),
            Is.EqualTo("CVIS Unity - Policy Drift Governance | Environment: UAT"));
    }

    [Test]
    public void BuildEmailSubject_IncludesEnvironmentBatchAndDate()
    {
        var generatedAtUtc = new DateTime(2026, 8, 11, 9, 30, 0, DateTimeKind.Utc);

        Assert.That(
            PolicyDriftEnvironment.BuildEmailSubject(
                "PROD", "12345678-90ab-cdef", generatedAtUtc),
            Is.EqualTo("[PROD] Unity Policy Drift Report - Batch 12345678 | 2026-08-11"));
    }

    private static IConfiguration BuildConfiguration(string? value)
    {
        var values = value is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>
            {
                [PolicyDriftEnvironment.ConfigurationKey] = value
            };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
