using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CVIS.Unity.Core.Diagnostics;
using CVIS.Unity.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace CVIS.Unity.Infrastructure.Diagnostics
{
    public class InfrastructureHealthService : IInfrastructureHealthService
    {
        private readonly IEnumerable<IInfrastructureProbe> _probes;
        private readonly IUnityEventPublisher _publisher;
        private readonly ILogger<InfrastructureHealthService> _logger;

        private InfrastructureHealthReport? _lastReport;
        private readonly object _lock = new();

        public InfrastructureHealthService(
            IEnumerable<IInfrastructureProbe> probes,
            IUnityEventPublisher publisher,
            ILogger<InfrastructureHealthService> logger)
        {
            _probes    = probes    ?? throw new ArgumentNullException(nameof(probes));
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
            _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<InfrastructureHealthReport> RunAllProbesAsync(
            string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            return await RunProbeSetAsync(
                _probes, correlationId, cancellationToken);
        }

        public async Task<InfrastructureHealthReport> RunProbesByNameAsync(
            IEnumerable<string> probeNames,
            string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            var nameSet      = new HashSet<string>(
                probeNames, StringComparer.OrdinalIgnoreCase);
            var targetProbes = _probes.Where(p => nameSet.Contains(p.ProbeName));

            return await RunProbeSetAsync(
                targetProbes, correlationId, cancellationToken);
        }

        public async Task<ProbeResult> RunProbeAsync(
            string probeName,
            string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            var probe = _probes.FirstOrDefault(p =>
                p.ProbeName.Equals(probeName, StringComparison.OrdinalIgnoreCase));

            if (probe == null)
                throw new InvalidOperationException(
                    $"No probe registered with name '{probeName}'. " +
                    $"Registered: {string.Join(", ", _probes.Select(p => p.ProbeName))}");

            return await RunSingleProbeAsync(
                probe, correlationId ?? Guid.NewGuid().ToString(), cancellationToken);
        }

        public InfrastructureHealthReport? GetLastReport() => _lastReport;

        // ─────────────────────────────────────────────────────────
        //  Private helpers
        // ─────────────────────────────────────────────────────────

        private async Task<InfrastructureHealthReport> RunProbeSetAsync(
            IEnumerable<IInfrastructureProbe> probes,
            string? correlationId,
            CancellationToken cancellationToken)
        {
            var id     = correlationId ?? Guid.NewGuid().ToString();
            var report = new InfrastructureHealthReport
            {
                CorrelationId = id,
                GeneratedAt   = DateTime.UtcNow
            };

            var probeList = probes.ToList();

            _logger.LogInformation(
                "[Health] Running {Count} probe(s) | Correlation: {Id}",
                probeList.Count, id);

            var tasks   = probeList.Select(p =>
                RunSingleProbeAsync(p, id, cancellationToken));
            var results = await Task.WhenAll(tasks);
            report.Results.AddRange(results);

            lock (_lock) { _lastReport = report; }

            await EmitReportEventAsync(report);

            _logger.LogInformation(
                "[Health] Probe run complete | Status: {Status} | " +
                "Healthy: {Healthy}/{Total}",
                report.OverallStatus,
                report.Results.Count(r => r.IsHealthy),
                report.Results.Count);

            return report;
        }

        private async Task<ProbeResult> RunSingleProbeAsync(
            IInfrastructureProbe probe,
            string correlationId,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await probe.RunAsync(correlationId, cancellationToken);

                if (!result.IsHealthy)
                {
                    _logger.LogCritical(
                        "[Health] Probe FAILED | Name: {Name} | Category: {Cat} | " +
                        "RootCause: {Cause} | Action: {Action}",
                        result.ProbeName,
                        result.Category,
                        result.RootCause,
                        result.RecommendedAction);
                }

                // Per-probe event — best effort, non-blocking
                try
                {
                    await _publisher.PublishDiagnosticEventAsync(
                        entityType          : "INFRASTRUCTURE",
                        entityId            : result.ProbeName,
                        domain              : "Unity",
                        subDomain           : result.Category,
                        eventName           : result.IsHealthy
                                                ? $"{result.ProbeName}_HEALTHY"
                                                : $"{result.ProbeName}_FAILED",
                        diagnosticMetadata  : result.ToEventMetadata(),
                        correlationId       : correlationId,
                        severity            : result.Severity);
                }
                catch (Exception evtEx)
                {
                    _logger.LogWarning(
                        "[Health] Could not emit probe event for {Name} — {Err}",
                        probe.ProbeName, evtEx.Message);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[Health] Probe {Name} threw an unhandled exception",
                    probe.ProbeName);

                return new ProbeResult
                {
                    ProbeName         = probe.ProbeName,
                    Category          = probe.Category,
                    CorrelationId     = correlationId,
                    IsHealthy         = false,
                    RootCause         = "PROBE_THREW_EXCEPTION",
                    Severity          = "CRITICAL",
                    RecommendedAction = $"Probe {probe.ProbeName} threw: {ex.Message}",
                    Steps             = new()
                    {
                        new ProbeStep
                        {
                            StepName    = "ProbeExecution",
                            Success     = false,
                            Message     = "Unhandled exception in probe",
                            ErrorDetail = ex.ToString()
                        }
                    }
                };
            }
        }

        private async Task EmitReportEventAsync(InfrastructureHealthReport report)
        {
            try
            {
                await _publisher.PublishDiagnosticEventAsync(
                    entityType         : "INFRASTRUCTURE",
                    entityId           : "HEALTH_REPORT",
                    domain             : "Unity",
                    subDomain          : "HealthCheck",
                    eventName          : $"HEALTH_{report.OverallStatus}",
                    diagnosticMetadata : report.ToEventMetadata(),
                    correlationId      : report.CorrelationId,
                    severity           : report.OverallSeverity);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[Health] Could not emit health report event — {Err}", ex.Message);
            }
        }
    }
}
