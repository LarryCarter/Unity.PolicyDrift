using System.Threading;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Diagnostics
{
    /// <summary>
    /// Contract every infrastructure probe implements.
    /// A probe is a self-contained diagnostic unit that answers
    /// one specific operational question:
    ///   DbConnectionProbe  → "Was it the database?"
    ///   KafkaProbe         → "Was it the message broker?"
    ///   PvwaProbe          → "Was it CyberArk?"
    ///   ServiceNowProbe    → "Was it ServiceNow?"
    ///
    /// New probes are added by implementing this interface and
    /// registering in ServiceCollectionExtensions.AddUnityInfrastructureHealth().
    /// No existing code changes required.
    /// </summary>
    public interface IInfrastructureProbe
    {
        /// <summary>
        /// Unique name for this probe — used as the registry key.
        /// Examples: "PolicyDb", "ReportingDb", "Kafka", "ServiceNow", "PVWA"
        /// </summary>
        string ProbeName { get; }

        /// <summary>
        /// Category drives how results are grouped in reports and UnityEvents.
        /// Examples: "Database", "Messaging", "ExternalApi", "Platform"
        /// </summary>
        string Category { get; }

        /// <summary>
        /// Run the full probe chain and return a structured result.
        /// Implementations must not throw — all exceptions should be
        /// caught and returned as a failed ProbeResult.
        /// </summary>
        Task<ProbeResult> RunAsync(
            string? correlationId = null,
            CancellationToken cancellationToken = default);
    }
}
