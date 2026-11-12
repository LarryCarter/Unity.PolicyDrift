using System;
using CVIS.Unity.Core.Diagnostics;

namespace CVIS.Unity.Infrastructure.Diagnostics
{
    /// <summary>
    /// Thrown by DbDiagnosticInterceptor when the probe chain determines
    /// the root cause is non-transient and retrying cannot help.
    ///
    /// Carries the full ProbeResult so callers can log or surface the
    /// diagnostic without re-running the probe chain.
    ///
    /// Non-transient root causes that trigger this:
    ///   DNS_RESOLUTION_FAILURE   — hostname cannot resolve, retrying is pointless
    ///   TCP_TIMEOUT_FIREWALL     — firewall blocking, retrying is pointless
    ///   TCP_CONNECTION_REFUSED   — SQL not listening, retrying is pointless
    ///   SQL_DATABASE_NOT_FOUND   — DB offline/missing, retrying is pointless
    ///   SQL_AUTH_FAILURE         — credentials rejected, retrying is pointless
    ///
    /// Transient root causes allow EF retry strategy to continue:
    ///   SQL_NO_PROCESS_AT_PIPE   — SQL starting up, may succeed on retry
    ///   EF_CONTEXT_FAILURE       — pool issue, may clear on retry
    /// </summary>
    public class InfrastructureConnectionException : Exception
    {
        public string RootCause { get; }
        public string RecommendedAction { get; }
        public ProbeResult Diagnostic { get; }

        public InfrastructureConnectionException(
            string rootCause,
            string recommendedAction,
            ProbeResult diagnostic,
            Exception innerException)
            : base(BuildMessage(rootCause, recommendedAction), innerException)
        {
            RootCause         = rootCause;
            RecommendedAction = recommendedAction;
            Diagnostic        = diagnostic;
        }

        private static string BuildMessage(
            string rootCause,
            string recommendedAction) =>
            $"[InfrastructureConnectionException] " +
            $"RootCause={rootCause} | " +
            $"Action={recommendedAction}";
    }
}
