using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Diagnostics
{
    /// <summary>
    /// Probe registry and orchestrator.
    /// All IInfrastructureProbe implementations registered via DI
    /// are automatically discovered and available here.
    ///
    /// Used by:
    ///   - DbDiagnosticInterceptor (reactive, at point of failure)
    ///   - UnityHealthCheck (OpenShift liveness/readiness adapter)
    ///   - Manual diagnostic triggers
    /// </summary>
    public interface IInfrastructureHealthService
    {
        /// <summary>
        /// Run all registered probes concurrently.
        /// Returns a rolled-up InfrastructureHealthReport.
        /// </summary>
        Task<InfrastructureHealthReport> RunAllProbesAsync(
            string? correlationId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Run a specific subset of probes by name.
        /// Used by DbDiagnosticInterceptor to run only the probes
        /// the DiagnosticPlan specifies — not always the full chain.
        /// </summary>
        Task<InfrastructureHealthReport> RunProbesByNameAsync(
            IEnumerable<string> probeNames,
            string? correlationId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Run a single named probe.
        /// Throws InvalidOperationException if probe name is not registered.
        /// </summary>
        Task<ProbeResult> RunProbeAsync(
            string probeName,
            string? correlationId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the last cached report without re-running probes.
        /// Useful for health endpoint responses that should not trigger
        /// a full probe run on every poll.
        /// </summary>
        InfrastructureHealthReport? GetLastReport();
    }
}
