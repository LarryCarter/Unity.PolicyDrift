using CVIS.Unity.Core.Audit;

namespace CVIS.Unity.Core.Interfaces;

public interface IPolicyDriftAuditPublisher
{
    Task PublishAsync(
        PolicyDriftAuditEnvelope envelope,
        CancellationToken cancellationToken = default);
}
