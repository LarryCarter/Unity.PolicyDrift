using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CVIS.Unity.Core.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CVIS.Unity.Infrastructure.Diagnostics.Probes
{
    /// <summary>
    /// Kafka broker connectivity probe.
    /// Probes each broker in the BootstrapServers list via TCP.
    /// At least one reachable broker = healthy (degraded if partial).
    /// All brokers unreachable = CRITICAL.
    ///
    /// Config key: Kafka:BootstrapServers (comma-separated host:port list)
    /// </summary>
    public class KafkaProbe : IInfrastructureProbe
    {
        private readonly IConfiguration _config;
        private readonly ILogger<KafkaProbe> _logger;

        public string ProbeName => "Kafka";
        public string Category  => "Messaging";

        private const int TcpTimeoutMs = 3000;

        public KafkaProbe(
            IConfiguration config,
            ILogger<KafkaProbe> logger)
        {
            _config = config  ?? throw new ArgumentNullException(nameof(config));
            _logger = logger  ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ProbeResult> RunAsync(
            string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            var bootstrapServers = _config["Kafka:BootstrapServers"] ?? string.Empty;

            var result = new ProbeResult
            {
                ProbeName     = ProbeName,
                Category      = Category,
                CorrelationId = correlationId ?? Guid.NewGuid().ToString()
            };
            result.Metadata["BootstrapServers"] = bootstrapServers;

            if (string.IsNullOrWhiteSpace(bootstrapServers))
            {
                result.IsHealthy         = false;
                result.RootCause         = "KAFKA_CONFIG_MISSING";
                result.Severity          = "CRITICAL";
                result.RecommendedAction = "Kafka:BootstrapServers is not configured.";
                result.Steps.Add(new ProbeStep
                {
                    StepName    = "ConfigCheck",
                    Success     = false,
                    Message     = "BootstrapServers config key missing",
                    ErrorDetail = "Set Kafka:BootstrapServers in appsettings"
                });
                return result;
            }

            var brokers = bootstrapServers.Split(',');
            foreach (var broker in brokers)
            {
                var parts = broker.Trim().Split(':');
                var host  = parts[0];
                var port  = parts.Length > 1 && int.TryParse(parts[1], out var p)
                    ? p : 9092;

                result.Steps.Add(await ProbeBrokerTcpAsync(host, port, cancellationToken));
            }

            ApplyRootCause(result);
            return result;
        }

        private async Task<ProbeStep> ProbeBrokerTcpAsync(
            string host, int port, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient();
                using var cts    = CancellationTokenSource
                    .CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TcpTimeoutMs);

                await client.ConnectAsync(host, port, cts.Token);
                sw.Stop();

                return new ProbeStep
                {
                    StepName  = $"Broker_{host}:{port}",
                    Success   = true,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message   = $"TCP connected to Kafka broker {host}:{port}"
                };
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return new ProbeStep
                {
                    StepName    = $"Broker_{host}:{port}",
                    Success     = false,
                    ElapsedMs   = sw.ElapsedMilliseconds,
                    Message     = $"Broker {host}:{port} timed out after {TcpTimeoutMs}ms",
                    ErrorDetail = "Possible network block or broker down"
                };
            }
            catch (SocketException ex)
            {
                sw.Stop();
                return new ProbeStep
                {
                    StepName    = $"Broker_{host}:{port}",
                    Success     = false,
                    ElapsedMs   = sw.ElapsedMilliseconds,
                    Message     = $"Broker {host}:{port} refused connection",
                    ErrorDetail = $"SocketException [{ex.SocketErrorCode}]: {ex.Message}"
                };
            }
        }

        private static void ApplyRootCause(ProbeResult result)
        {
            var anySuccess = result.Steps.Exists(s => s.Success);
            var allFailed  = result.Steps.TrueForAll(s => !s.Success);

            if (!allFailed)
            {
                result.IsHealthy         = true;
                result.RootCause         = "NONE";
                result.Severity          = "INFO";
                result.RecommendedAction = anySuccess
                    ? "At least one Kafka broker is reachable."
                    : "All brokers healthy.";
                return;
            }

            result.IsHealthy         = false;
            result.RootCause         = "KAFKA_ALL_BROKERS_UNREACHABLE";
            result.Severity          = "CRITICAL";
            result.RecommendedAction =
                "No Kafka brokers are reachable. Check broker status, " +
                "network routing, and firewall rules for port 9092.";
        }
    }
}
