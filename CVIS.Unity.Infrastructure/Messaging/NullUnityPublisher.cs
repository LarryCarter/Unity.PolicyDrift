using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CVIS.Unity.Core.Interfaces;

namespace CVIS.Unity.Infrastructure.Messaging
{
    public class NullUnityPublisher : IUnityEventPublisher
    {
        public void LogInfo(string message)    { }
        public void LogWarning(string message) { }
        public void LogError(string message, Exception? ex = null) { }

        public Task PublishStatusEventAsync(
            string entityType, string entityId,
            string domain, string subDomain,
            string status, object? metadata = null)
        {
            Console.WriteLine(
                $"[NULL] [{entityType}] {entityId} -> {status} ({domain}/{subDomain})");
            return Task.CompletedTask;
        }

        public Task PublishAuditEventAsync(
            string entityType, string entityId,
            string domain, string subDomain,
            string action, string actor = "System")
        {
            Console.WriteLine(
                $"[NULL] [{entityType}] {entityId} -> {action} " +
                $"by {actor} ({domain}/{subDomain})");
            return Task.CompletedTask;
        }

        public Task PublishKafkaDriftAsync(
            string entityType, string entityId,
            string domain, string subDomain,
            Dictionary<string, string> differences,
            Dictionary<string, string> baseline,
            string? correlationId = null)
        {
            Console.WriteLine(
                $"[NULL] [{entityType}] {entityId} ({domain}/{subDomain}): " +
                $"{differences.Count} changes detected.");
            return Task.CompletedTask;
        }

        public Task PublishDiagnosticEventAsync(
            string entityType, string entityId,
            string domain, string subDomain,
            string eventName,
            Dictionary<string, string> diagnosticMetadata,
            string? correlationId = null,
            string severity = "HIGH")
        {
            Console.WriteLine(
                $"[NULL-DIAGNOSTIC] [{entityType}] {entityId} " +
                $"({domain}/{subDomain}) | {eventName} | {severity}");

            foreach (var kvp in diagnosticMetadata)
                Console.WriteLine($"  {kvp.Key}: {kvp.Value}");

            return Task.CompletedTask;
        }

        public Task SendEmailAsync(string to, string subject, string htmlBody)
            => throw new NotImplementedException();
    }
}
