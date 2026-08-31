using System;
using System.Collections.Generic;
using System.Linq;

namespace CVIS.Unity.Core.Diagnostics
{
    /// <summary>
    /// Rolled-up health report across all registered probes.
    /// Produced by IInfrastructureHealthService.RunAllProbesAsync().
    ///
    /// IsAlive and IsReady map directly to OpenShift/Kubernetes
    /// liveness and readiness probe semantics respectively.
    /// The UnityHealthCheck adapter consumes this for ASP.NET Core IHealthCheck.
    /// </summary>
    public class InfrastructureHealthReport
    {
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public string CorrelationId { get; set; } = string.Empty;
        public List<ProbeResult> Results { get; set; } = new();

        public bool IsFullyHealthy =>
            Results.All(r => r.IsHealthy);

        public bool IsDegraded =>
            Results.Any(r => !r.IsHealthy) && Results.Any(r => r.IsHealthy);

        public bool IsFullyUnhealthy =>
            Results.All(r => !r.IsHealthy);

        public string OverallStatus =>
            IsFullyHealthy   ? "HEALTHY"   :
            IsDegraded       ? "DEGRADED"  :
                               "UNHEALTHY";

        public string OverallSeverity =>
            Results.Any(r => r.Severity == "CRITICAL") ? "CRITICAL" :
            Results.Any(r => r.Severity == "HIGH")     ? "HIGH"     :
            Results.Any(r => r.Severity == "MEDIUM")   ? "MEDIUM"   :
                                                         "INFO";

        public IEnumerable<ProbeResult> FailedProbes =>
            Results.Where(r => !r.IsHealthy);

        public IEnumerable<IGrouping<string, ProbeResult>> ByCategory =>
            Results.GroupBy(r => r.Category);

        /// <summary>
        /// Maps to OpenShift/Kubernetes liveness probe.
        /// Returns false only for CRITICAL failures — a degraded
        /// system is still alive, just not ready for traffic.
        /// </summary>
        public bool IsAlive =>
            !Results.Any(r => r.Severity == "CRITICAL" && !r.IsHealthy);

        /// <summary>
        /// Maps to OpenShift/Kubernetes readiness probe.
        /// Returns false if any probe is unhealthy — pod should
        /// not receive traffic until all probes pass.
        /// </summary>
        public bool IsReady => IsFullyHealthy;

        public Dictionary<string, string> ToEventMetadata() => new()
        {
            ["CorrelationId"]    = CorrelationId,
            ["GeneratedAt"]      = GeneratedAt.ToString("O"),
            ["OverallStatus"]    = OverallStatus,
            ["OverallSeverity"]  = OverallSeverity,
            ["IsAlive"]          = IsAlive.ToString(),
            ["IsReady"]          = IsReady.ToString(),
            ["TotalProbes"]      = Results.Count.ToString(),
            ["HealthyProbes"]    = Results.Count(r => r.IsHealthy).ToString(),
            ["FailedProbes"]     = Results.Count(r => !r.IsHealthy).ToString(),
            ["FailedProbeNames"] = string.Join(",", FailedProbes.Select(r => r.ProbeName))
        };
    }
}
