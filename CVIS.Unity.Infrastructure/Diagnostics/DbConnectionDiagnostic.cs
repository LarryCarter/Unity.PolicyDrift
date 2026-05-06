using System;
using System.Collections.Generic;

namespace CVIS.Unity.Infrastructure.Diagnostics
{
    public class DbConnectionDiagnostic
    {
        public DateTime ProbeTimestamp { get; set; } = DateTime.UtcNow;
        public string ConnectionHost { get; set; } = string.Empty;
        public int ConnectionPort { get; set; } = 1433;
        public string ExecutionId { get; set; } = string.Empty;

        // Per-probe results
        public ProbeResult DnsProbe { get; set; } = new();
        public ProbeResult TcpProbe { get; set; } = new();
        public ProbeResult SqlLoginProbe { get; set; } = new();
        public ProbeResult EfContextProbe { get; set; } = new();
        public ProbeResult PoolStateProbe { get; set; } = new();

        // Rolled-up verdict
        public string RootCause { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string RecommendedAction { get; set; } = string.Empty;

        // All resolved IPs for the host — useful to spot stale DNS
        public List<string> ResolvedAddresses { get; set; } = new();

        public bool IsHealthy =>
            DnsProbe.Success &&
            TcpProbe.Success &&
            SqlLoginProbe.Success &&
            EfContextProbe.Success;

        public Dictionary<string, string> ToMetadataDictionary() => new()
        {
            ["ProbeTimestamp"] = ProbeTimestamp.ToString("O"),
            ["Host"] = ConnectionHost,
            ["Port"] = ConnectionPort.ToString(),
            ["ExecutionId"] = ExecutionId,
            ["DnsProbe"] = DnsProbe.ToString(),
            ["TcpProbe"] = TcpProbe.ToString(),
            ["SqlLoginProbe"] = SqlLoginProbe.ToString(),
            ["EfContextProbe"] = EfContextProbe.ToString(),
            ["PoolStateProbe"] = PoolStateProbe.ToString(),
            ["ResolvedAddresses"] = string.Join(",", ResolvedAddresses),
            ["RootCause"] = RootCause,
            ["Severity"] = Severity,
            ["RecommendedAction"] = RecommendedAction
        };
    }

    public class ProbeResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public long ElapsedMs { get; set; }
        public string? ErrorDetail { get; set; }

        public override string ToString() =>
            Success
                ? $"OK ({ElapsedMs}ms)"
                : $"FAIL ({ElapsedMs}ms) — {ErrorDetail}";
    }
}