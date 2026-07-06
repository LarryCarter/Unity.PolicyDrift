using System;
using System.Collections.Generic;
using System.Linq;

namespace CVIS.Unity.Core.Diagnostics
{
    /// <summary>
    /// Unified result model for all probe types.
    /// Each probe produces one ProbeResult containing
    /// ordered ProbeStep records — one per layer tested.
    ///
    /// The ToEventMetadata() method flattens everything into
    /// a Dictionary<string, string> ready to drop into
    /// UnityEvent.Metadata as a DIAGNOSTIC event.
    /// </summary>
    public class ProbeResult
    {
        public string ProbeName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public DateTime ProbeTimestamp { get; set; } = DateTime.UtcNow;
        public bool IsHealthy { get; set; }
        public string RootCause { get; set; } = "NONE";
        public string Severity { get; set; } = "INFO";
        public string RecommendedAction { get; set; } = string.Empty;

        /// <summary>
        /// Individual step results within the probe.
        /// Steps run in order: DNS → TCP → SqlLogin → EfContext → PoolState.
        /// A skipped step means the prior step already identified the failure layer.
        /// </summary>
        public List<ProbeStep> Steps { get; set; } = new();

        /// <summary>
        /// Arbitrary key/value diagnostic data specific to each probe type.
        /// Examples: Host, Port, ContextType, ResolvedAddresses, BootstrapServers.
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; } = new();

        public long TotalElapsedMs =>
            Steps.Sum(s => s.ElapsedMs);

        public ProbeStep? FirstFailedStep =>
            Steps.FirstOrDefault(s => !s.Success && !s.Skipped);

        /// <summary>
        /// Flattens the full probe result into a string dictionary
        /// suitable for UnityEvent.Metadata JSON serialization.
        /// </summary>
        public Dictionary<string, string> ToEventMetadata()
        {
            var dict = new Dictionary<string, string>
            {
                ["ProbeName"]         = ProbeName,
                ["Category"]          = Category,
                ["CorrelationId"]     = CorrelationId,
                ["ProbeTimestamp"]    = ProbeTimestamp.ToString("O"),
                ["IsHealthy"]         = IsHealthy.ToString(),
                ["RootCause"]         = RootCause,
                ["Severity"]          = Severity,
                ["RecommendedAction"] = RecommendedAction,
                ["TotalElapsedMs"]    = TotalElapsedMs.ToString(),
                ["FirstFailedStep"]   = FirstFailedStep?.StepName ?? "None"
            };

            foreach (var step in Steps)
            {
                dict[$"Step_{step.StepName}"] = step.ToString();
            }

            foreach (var kvp in Metadata)
            {
                dict[kvp.Key] = kvp.Value;
            }

            return dict;
        }
    }

    /// <summary>
    /// A single step within a probe chain.
    /// Use ProbeStep.Skip() when a prior step failure makes
    /// this step unnecessary to run.
    /// </summary>
    public class ProbeStep
    {
        public string StepName { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public long ElapsedMs { get; set; }
        public string? ErrorDetail { get; set; }
        public bool Skipped { get; set; }

        public static ProbeStep Skip(string stepName, string reason) => new()
        {
            StepName    = stepName,
            Success     = false,
            Skipped     = true,
            ElapsedMs   = 0,
            Message     = reason,
            ErrorDetail = reason
        };

        public override string ToString() =>
            Skipped  ? $"SKIP — {Message}" :
            Success  ? $"OK ({ElapsedMs}ms) — {Message}" :
                       $"FAIL ({ElapsedMs}ms) — {ErrorDetail}";
    }
}
