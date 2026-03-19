using System;
using System.Threading.Tasks;
using CVIS.Unity.Core.Interfaces; // This is critical!

namespace CVIS.Unity.Infrastructure.Messaging
{
    // Datyrix: You must explicitly inherit from the interface here
    public class NullUnityPublisher : IUnityEventPublisher
    {
        public Task PublishStatusEventAsync(string policyId, string status, object? metadata = null)
        {
            // Validated placeholder for tonight's run
            Console.WriteLine($"[KAFKA_PLACEHOLDER] {policyId} Status Update: {status}");
            return Task.CompletedTask;
        }

        public Task PublishAuditEventAsync(string policyId, string action, string actor = "System")
        {
            Console.WriteLine($"[KAFKA_PLACEHOLDER] {policyId} Audit Event: {action} by {actor}");
            return Task.CompletedTask;
        }

        void IUnityEventPublisher.LogInfo(string message)
        {
            throw new NotImplementedException();
        }

        void IUnityEventPublisher.LogError(string message, Exception? ex)
        {
            throw new NotImplementedException();
        }

        void IUnityEventPublisher.LogWarning(string message)
        {
            throw new NotImplementedException();
        }
    }
}