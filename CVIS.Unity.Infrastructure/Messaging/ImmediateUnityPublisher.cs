using System;
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

        //ImmediateUnityPublisher takes the PolicyDbContext in its constructor.
        //This allows the 'Status Events' to land in your specialized table while
        //'Info Logs' land in the general Serilog table."

        public ImmediateUnityPublisher(ILogger<ImmediateUnityPublisher> logger, PolicyDbContext db)
        {
            _logger = logger;
            _db = db;
        }

        public async Task PublishStatusEventAsync(string id, string status, object? meta = null)
        {
            // Business Event: Log it and save to the unity.PolicyEvents table
            _logger.LogInformation("Status Update: {PolicyId} -> {Status}", id, status);
            await _db.SavePolicyEventAsync(id, status, "STATUS", meta);
        }

        public async Task PublishAuditEventAsync(string id, string action, string actor = "System")
        {
            // Audit Event: Higher severity log + DB persistence
            _logger.LogWarning("Audit: {Action} on {PolicyId} by {Actor}", action, id, actor);
            await _db.SavePolicyEventAsync(id, action, "AUDIT", new { PerformedBy = actor });
        }

        public void LogInfo(string message) =>
            // Simple trace log (Goes to File and unity.LogEvents via Serilog)
            _logger.LogInformation(message);

        public void LogError(string message, Exception? ex = null) =>
            // Error trace (Red in Console, Detailed in SQL via Serilog)
            _logger.LogError(ex, message);

        public void LogWarning(string message) =>
            // Serilog will capture this as a Warning level
            _logger.LogWarning(message);
    }
}