namespace CVIS.Unity.Core.Interfaces;

public interface IPolicyDriftReportAuditor
{
    Task AuditReportAsync(
        string executionId,
        DateOnly reportDate,
        CancellationToken cancellationToken = default);
}
