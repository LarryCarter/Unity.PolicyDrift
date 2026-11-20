using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Interfaces
{
    public interface IUnityEventPublisher
    {
        // ── Domain-agnostic event bus ─────────────────────────────

        Task PublishStatusEventAsync(
            string entityType,
            string entityId,
            string domain,
            string subDomain,
            string status,
            object? metadata = null);

        Task PublishAuditEventAsync(
            string entityType,
            string entityId,
            string domain,
            string subDomain,
            string action,
            string actor = "System");

        // ── Kafka drift trigger — domain agnostic ─────────────────

        Task PublishKafkaDriftAsync(
            string entityType,
            string entityId,
            string domain,
            string subDomain,
            Dictionary<string, string> differences,
            Dictionary<string, string> baseline,
            string? correlationId = null);

        // ── Forensic diagnostic event ─────────────────────────────

        /// <summary>
        /// Publishes a forensic diagnostic event to the UnityEvent bus.
        ///
        /// Fired at the point of infrastructure failure by DbDiagnosticInterceptor.
        /// Carries the full probe chain results, exception classification, and
        /// captured context as structured metadata so failures are queryable
        /// after the fact without requiring production access.
        ///
        /// EventType on the UnityEvent record will be "DIAGNOSTIC".
        ///
        /// Write is best-effort — if the DB itself is down, Serilog and
        /// Console are the fallback. This method must never throw.
        /// </summary>
        Task PublishDiagnosticEventAsync(
            string entityType,
            string entityId,
            string domain,
            string subDomain,
            string eventName,
            Dictionary<string, string> diagnosticMetadata,
            string? correlationId = null,
            string severity = "HIGH");

        // ── Observability ─────────────────────────────────────────

        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message, Exception? ex = null);
        Task SendEmailAsync(string to, string subject, string htmlBody);
    }
}
