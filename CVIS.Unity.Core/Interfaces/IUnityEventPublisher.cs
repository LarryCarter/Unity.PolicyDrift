using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Interfaces
{
    public interface IUnityEventPublisher
    {
        // Domain-agnostic event bus
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

        // Kafka drift trigger — domain agnostic
        Task PublishKafkaDriftAsync(
            string entityType,
            string entityId,
            string domain,
            string subDomain,
            Dictionary<string, string> differences,
            Dictionary<string, string> baseline,
            string? correlationId = null);

        // Observability
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message, Exception? ex = null);
        Task SendEmailAsync(string to, string subject, string htmlBody);
    }
}