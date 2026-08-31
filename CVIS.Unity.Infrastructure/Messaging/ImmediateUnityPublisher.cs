using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Infrastructure.Data;

namespace CVIS.Unity.Infrastructure.Messaging
{
    public class ImmediateUnityPublisher : IUnityEventPublisher
    {
        private readonly ILogger<ImmediateUnityPublisher> _logger;
        private readonly PolicyDbContext _db;

        public ImmediateUnityPublisher(
            ILogger<ImmediateUnityPublisher> logger,
            PolicyDbContext db)
        {
            _logger = logger;
            _db     = db;
        }

        public async Task PublishStatusEventAsync(
            string entityType,
            string entityId,
            string domain,
            string subDomain,
            string status,
            object? meta = null)
        {
            _logger.LogInformation(
                "Status Update: [{EntityType}] {EntityId} -> {Status} ({Domain}/{SubDomain})",
                entityType, entityId, status, domain, subDomain);

            await _db.SaveUnityEventAsync(
                entityType : entityType,
                entityId   : entityId,
                domain     : domain,
                subDomain  : subDomain,
                eventName  : status,
                eventType  : "STATUS",
                meta       : meta);
        }

        public async Task PublishAuditEventAsync(
            string entityType,
            string entityId,
            string domain,
            string subDomain,
            string action,
            string actor = "System")
        {
            _logger.LogWarning(
                "Audit: {Action} on [{EntityType}] {EntityId} by {Actor} ({Domain}/{SubDomain})",
                action, entityType, entityId, actor, domain, subDomain);

            await _db.SaveUnityEventAsync(
                entityType : entityType,
                entityId   : entityId,
                domain     : domain,
                subDomain  : subDomain,
                eventName  : action,
                eventType  : "AUDIT",
                actor      : actor,
                meta       : new { PerformedBy = actor });
        }

        public async Task PublishKafkaDriftAsync(
            string entityType,
            string entityId,
            string domain,
            string subDomain,
            Dictionary<string, string> differences,
            Dictionary<string, string> baseline,
            string? correlationId = null)
        {
            _logger.LogWarning(
                "Kafka Drift: [{EntityType}] {EntityId} ({Domain}/{SubDomain}): " +
                "{Count} changes detected.",
                entityType, entityId, domain, subDomain, differences.Count);

            Console.WriteLine(
                $"[KAFKA-DRIFT] [{entityType}] {entityId} ({domain}/{subDomain}): " +
                $"{differences.Count} changes detected.");

            await _db.SaveUnityEventAsync(
                entityType    : entityType,
                entityId      : entityId,
                domain        : domain,
                subDomain     : subDomain,
                eventName     : "DRIFT_DETECTED",
                eventType     : "DRIFT",
                correlationId : correlationId,
                meta          : new
                {
                    DifferenceCount = differences.Count.ToString(),
                    DriftedKeys     = string.Join(",", differences.Keys)
                });
        }

        public async Task PublishDiagnosticEventAsync(
            string entityType,
            string entityId,
            string domain,
            string subDomain,
            string eventName,
            Dictionary<string, string> diagnosticMetadata,
            string? correlationId = null,
            string severity = "HIGH")
        {
            // Always log to Serilog first — the DB itself may be down
            var logLevel = severity switch
            {
                "CRITICAL" => LogLevel.Critical,
                "HIGH"     => LogLevel.Error,
                "MEDIUM"   => LogLevel.Warning,
                _          => LogLevel.Information
            };

            _logger.Log(logLevel,
                "[DIAGNOSTIC] [{EntityType}] {EntityId} | " +
                "{Domain}/{SubDomain} | {EventName} | " +
                "Severity: {Severity} | CorrelationId: {CorrelationId}",
                entityType, entityId,
                domain, subDomain,
                eventName, severity,
                correlationId ?? "none");

            // Always write to Console — survives DB outage
            Console.WriteLine(
                $"[DIAGNOSTIC] [{entityType}] {entityId} " +
                $"({domain}/{subDomain}) | {eventName} | {severity}");

            // Best-effort write to UnityEvent bus
            // If DB is down this fails silently — Serilog and Console already have it
            try
            {
                await _db.SaveUnityEventAsync(
                    entityType    : entityType,
                    entityId      : entityId,
                    domain        : domain,
                    subDomain     : subDomain,
                    eventName     : eventName,
                    eventType     : "DIAGNOSTIC",
                    correlationId : correlationId,
                    meta          : diagnosticMetadata);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[DIAGNOSTIC] Could not persist to UnityEvents — DB unavailable. " +
                    "Serilog and Console records preserved. Error: {Error}", ex.Message);
            }
        }

        public void LogInfo(string message)    => _logger.LogInformation(message);
        public void LogWarning(string message) => _logger.LogWarning(message);
        public void LogError(string message, Exception? ex = null)
            => _logger.LogError(ex, message);

        public Task SendEmailAsync(string to, string subject, string htmlBody)
            => throw new NotImplementedException();
    }
}
