using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using CVIS.Unity.Core.Diagnostics;
using CVIS.Unity.Core.Interfaces;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CVIS.Unity.Infrastructure.Diagnostics
{
    /// <summary>
    /// EF Core command interceptor that fires the diagnostic probe chain
    /// at the exact point of connection failure — before EF decides whether
    /// to retry. If DNS or TCP is broken, retries are immediately aborted
    /// and the root cause is escalated via InfrastructureConnectionException.
    ///
    /// Registration: add to PolicyDbContext via OnConfiguring:
    ///   optionsBuilder.AddInterceptors(_interceptor);
    ///
    /// DI: registered as Scoped in ServiceCollectionExtensions.
    ///
    /// Decision flow:
    ///   Connection fails
    ///     → ExceptionClassifier.Classify(ex) → DiagnosticPlan
    ///     → InfrastructureHealthService.RunProbesByNameAsync(plan.ProbeSequence)
    ///     → CaptureContext(plan, ex)
    ///     → ShouldAbortRetry(dbProbe) decision
    ///     → EmitForensicDiagnosticAsync → UnityEvent bus as DIAGNOSTIC
    ///     → If abort: throw InfrastructureConnectionException
    ///     → If transient: log, allow EF execution strategy to retry
    /// </summary>
    public class DbDiagnosticInterceptor : DbCommandInterceptor
    {
        private readonly IInfrastructureHealthService _health;
        private readonly IUnityEventPublisher _publisher;
        private readonly ILogger<DbDiagnosticInterceptor> _logger;

        private readonly Dictionary<string, int> _retryCount = new();
        private readonly object _lock = new();

        public DbDiagnosticInterceptor(
            IInfrastructureHealthService health,
            IUnityEventPublisher publisher,
            ILogger<DbDiagnosticInterceptor> logger)
        {
            _health    = health    ?? throw new ArgumentNullException(nameof(health));
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
            _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
        }

        public override async Task ConnectionFailedAsync(
            DbConnection connection,
            ConnectionErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            var correlationId = Guid.NewGuid().ToString();
            var contextName   = eventData.Context?.GetType().Name ?? "UnknownContext";
            var attempt       = IncrementRetryCount(correlationId);
            var ex            = eventData.Exception;

            // ── Step 1: Classify — determine which probes to run ──────
            var plan = ExceptionClassifier.Classify(ex);

            _logger.LogWarning(
                "[DbInterceptor] {Classification} | Layer: {Layer} | " +
                "Context: {Context} | Attempt: {Attempt} | Question: {Question}",
                plan.InitialClassification,
                plan.FailureLayer,
                contextName,
                attempt,
                plan.InvestigationQuestion);

            // ── Step 2: Run only the probes the plan specifies ────────
            var report  = await _health.RunProbesByNameAsync(
                plan.ProbeSequence, correlationId, cancellationToken);

            var dbProbe = report.Results.Find(r => r.Category == "Database");

            // ── Step 3: Capture context targets ───────────────────────
            var capturedContext = CaptureContext(plan, ex, attempt, contextName);

            // ── Step 4: Abort decision ────────────────────────────────
            var shouldAbort = dbProbe != null && ShouldAbortRetry(dbProbe);

            // ── Step 5: Emit forensic record to UnityEvent bus ────────
            await EmitForensicDiagnosticAsync(
                plan, dbProbe, capturedContext,
                contextName, attempt, shouldAbort, correlationId);

            if (shouldAbort)
            {
                _logger.LogCritical(
                    "[DbInterceptor] ABORTING RETRY | Cause: {Cause} | Action: {Action}",
                    dbProbe?.RootCause,
                    plan.InvestigationQuestion);

                throw new InfrastructureConnectionException(
                    dbProbe?.RootCause ?? plan.InitialClassification,
                    plan.InvestigationQuestion,
                    dbProbe ?? new ProbeResult(),
                    ex);
            }

            _logger.LogWarning(
                "[DbInterceptor] Transient failure — allowing EF retry | " +
                "Cause: {Cause} | Attempt: {Attempt}",
                plan.InitialClassification, attempt);
        }

        // ─────────────────────────────────────────────────────────────
        //  Abort decision
        //  Network-layer failures cannot be resolved by retrying the
        //  same SQL command — abort immediately and escalate.
        // ─────────────────────────────────────────────────────────────

        private static bool ShouldAbortRetry(ProbeResult dbProbe) =>
            dbProbe.RootCause switch
            {
                "DNS_RESOLUTION_FAILURE"   => true,
                "TCP_TIMEOUT_FIREWALL"     => true,
                "TCP_CONNECTION_REFUSED"   => true,
                "SQL_DATABASE_NOT_FOUND"   => true,
                "SQL_AUTH_FAILURE"         => true,
                "SQL_NO_PROCESS_AT_PIPE"   => false,
                "EF_CONTEXT_FAILURE"       => false,
                "NONE"                     => false,
                _                          => false
            };

        // ─────────────────────────────────────────────────────────────
        //  Context capture — records the targets the plan specified
        // ─────────────────────────────────────────────────────────────

        private static Dictionary<string, string> CaptureContext(
            DiagnosticPlan plan,
            Exception ex,
            int attempt,
            string contextName)
        {
            var now     = DateTime.UtcNow;
            var context = new Dictionary<string, string>
            {
                ["ContextName"]      = contextName,
                ["RetryAttempt"]     = attempt.ToString(),
                ["ExceptionType"]    = ex.GetType().Name,
                ["ExceptionMessage"] = ex.Message
            };

            foreach (var target in plan.CaptureTargets)
            {
                switch (target)
                {
                    case "TimeOfDay":
                        context["TimeOfDay"] = now.ToString("HH:mm:ss");
                        context["HourOfDay"] = now.Hour.ToString();
                        break;

                    case "DayOfWeek":
                        context["DayOfWeek"] = now.DayOfWeek.ToString();
                        break;

                    case "ConnectionPoolState":
                        context["PoolStateCaptured"] = "true";
                        break;

                    case "CommandText":
                        context["CommandText"] = ex is SqlException sql
                            ? $"SqlError {sql.Number}"
                            : "Unknown";
                        break;

                    case "RetryCount":
                        context["RetryCount"] = attempt.ToString();
                        break;

                    case "ServiceAccount":
                        // Do not log the actual account name — flag only
                        context["ServiceAccountFailure"] = "true";
                        break;
                }
            }

            return context;
        }

        // ─────────────────────────────────────────────────────────────
        //  Emit forensic diagnostic event to UnityEvent bus
        // ─────────────────────────────────────────────────────────────

        private async Task EmitForensicDiagnosticAsync(
            DiagnosticPlan plan,
            ProbeResult? dbProbe,
            Dictionary<string, string> capturedContext,
            string contextName,
            int attempt,
            bool aborted,
            string correlationId)
        {
            var metadata = new Dictionary<string, string>
            {
                ["Classification"]        = plan.InitialClassification,
                ["FailureLayer"]          = plan.FailureLayer,
                ["Severity"]              = plan.Severity,
                ["IsKnownIntermittent"]   = plan.IsKnownIntermittent.ToString(),
                ["InvestigationQuestion"] = plan.InvestigationQuestion,
                ["ProbesRun"]             = string.Join(",", plan.ProbeSequence),
                ["RetryAborted"]          = aborted.ToString(),
                ["RetryAttempt"]          = attempt.ToString(),
                ["ContextName"]           = contextName
            };

            foreach (var kvp in capturedContext)
                metadata[kvp.Key] = kvp.Value;

            if (dbProbe != null)
                foreach (var kvp in dbProbe.ToEventMetadata())
                    metadata[$"Probe_{kvp.Key}"] = kvp.Value;

            await _publisher.PublishDiagnosticEventAsync(
                entityType         : "INFRASTRUCTURE",
                entityId           : dbProbe?.Metadata
                                        .GetValueOrDefault("Host", contextName)
                                     ?? contextName,
                domain             : "Unity",
                subDomain          : "Database",
                eventName          : aborted
                    ? $"DB_RETRY_ABORTED_{plan.InitialClassification}"
                    : $"DB_FAILURE_{plan.InitialClassification}",
                diagnosticMetadata : metadata,
                correlationId      : correlationId,
                severity           : plan.Severity);
        }

        // ─────────────────────────────────────────────────────────────
        //  Retry counter — tracks attempts per connection string hash
        // ─────────────────────────────────────────────────────────────

        private int IncrementRetryCount(string key)
        {
            lock (_lock)
            {
                if (!_retryCount.ContainsKey(key))
                    _retryCount[key] = 0;
                return ++_retryCount[key];
            }
        }
    }
}
