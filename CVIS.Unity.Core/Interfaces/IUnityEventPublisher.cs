using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Interfaces
{
    public interface IUnityEventPublisher
    {
        // Status/Audit (Future Kafka Topics)
        Task PublishStatusEventAsync(string policyId, string status, object? metadata = null);
        Task PublishAuditEventAsync(string policyId, string action, string actor = "System");
        // New Kafka trigger for Drift Reporting
        Task PublishKafkaDriftAsync(string policyId, Dictionary<string, string> differences, Dictionary<string, string> baseline);

        // Observability (Serilog / SQL Logs)
        void LogInfo(string message);
        void LogWarning(string message); // Add this line
        void LogError(string message, Exception? ex = null);
        Task SendEmailAsync(string to, string subject, string htmlBody);
    }
}
