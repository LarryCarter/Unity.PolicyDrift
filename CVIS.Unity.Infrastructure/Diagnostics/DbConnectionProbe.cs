using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CVIS.Unity.Infrastructure.Diagnostics
{
    public class DbConnectionProbe : IDbConnectionProbe
    {
        private readonly IConfiguration _config;
        private readonly PolicyDbContext _db;
        private readonly IUnityEventPublisher _publisher;
        private readonly ILogger<DbConnectionProbe> _logger;

        private const int TcpTimeoutMs = 3000;
        private const int SqlTimeoutSec = 5;

        public DbConnectionProbe(
            IConfiguration config,
            PolicyDbContext db,
            IUnityEventPublisher publisher,
            ILogger<DbConnectionProbe> logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<DbConnectionDiagnostic> RunFullProbeAsync(
            string? executionId = null)
        {
            var connString = _config.GetConnectionString("DefaultConnection") ?? string.Empty;
            var (host, port) = ParseHostPort(connString);

            var diag = new DbConnectionDiagnostic
            {
                ConnectionHost = host,
                ConnectionPort = port,
                ExecutionId = executionId ?? Guid.NewGuid().ToString()
            };

            _logger.LogWarning(
                "[DbProbe] Starting connectivity probe | Host: {Host}:{Port} | Execution: {ExecId}",
                host, port, diag.ExecutionId);

            // ── Probe 1: DNS ─────────────────────────────────────────
            diag.DnsProbe = await ProbeDnsAsync(host, diag);

            // ── Probe 2: TCP — only if DNS resolved ──────────────────
            if (diag.DnsProbe.Success)
                diag.TcpProbe = await ProbeTcpAsync(host, port);
            else
                diag.TcpProbe = Skipped("Skipped — DNS failed");

            // ── Probe 3: SQL Login — only if TCP succeeded ────────────
            if (diag.TcpProbe.Success)
                diag.SqlLoginProbe = await ProbeSqlLoginAsync(connString);
            else
                diag.SqlLoginProbe = Skipped("Skipped — TCP failed");

            // ── Probe 4: EF Context — only if SQL login succeeded ─────
            if (diag.SqlLoginProbe.Success)
                diag.EfContextProbe = await ProbeEfContextAsync();
            else
                diag.EfContextProbe = Skipped("Skipped — SQL login failed");

            // ── Probe 5: Pool State — always runs ─────────────────────
            diag.PoolStateProbe = ProbePoolState(connString);

            // ── Root Cause Analysis ───────────────────────────────────
            ApplyRootCauseAnalysis(diag);

            // ── Emit to all outputs ───────────────────────────────────
            await EmitDiagnosticsAsync(diag);

            return diag;
        }

        // ─────────────────────────────────────────────────────────────
        //  Probe 1 — DNS Resolution
        // ─────────────────────────────────────────────────────────────

        private async Task<ProbeResult> ProbeDnsAsync(
            string host, DbConnectionDiagnostic diag)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host);
                sw.Stop();

                var ips = addresses.Select(a => a.ToString()).ToList();
                diag.ResolvedAddresses = ips;

                return new ProbeResult
                {
                    Success = true,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = $"Resolved {ips.Count} address(es): {string.Join(", ", ips)}"
                };
            }
            catch (SocketException ex)
            {
                sw.Stop();
                return new ProbeResult
                {
                    Success = false,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = "DNS resolution failed",
                    ErrorDetail = $"SocketException [{ex.SocketErrorCode}]: {ex.Message}"
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ProbeResult
                {
                    Success = false,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = "DNS resolution threw unexpected error",
                    ErrorDetail = ex.Message
                };
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Probe 2 — TCP Connectivity
        // ─────────────────────────────────────────────────────────────

        private async Task<ProbeResult> ProbeTcpAsync(string host, int port)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TcpTimeoutMs);

                await client.ConnectAsync(host, port, cts.Token);
                sw.Stop();

                return new ProbeResult
                {
                    Success = true,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = $"TCP connection established to {host}:{port}"
                };
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return new ProbeResult
                {
                    Success = false,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = $"TCP connection timed out after {TcpTimeoutMs}ms",
                    ErrorDetail = $"Host {host}:{port} did not respond — possible firewall or port block"
                };
            }
            catch (SocketException ex)
            {
                sw.Stop();
                return new ProbeResult
                {
                    Success = false,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = "TCP connection refused or unreachable",
                    ErrorDetail = $"SocketException [{ex.SocketErrorCode}]: {ex.Message}"
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ProbeResult
                {
                    Success = false,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = "TCP probe threw unexpected error",
                    ErrorDetail = ex.Message
                };
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Probe 3 — Raw SQL Login
        // ─────────────────────────────────────────────────────────────

        private async Task<ProbeResult> ProbeSqlLoginAsync(string connString)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // Build a minimal connection string with short timeout
                var builder = new SqlConnectionStringBuilder(connString)
                {
                    ConnectTimeout = SqlTimeoutSec
                };

                await using var conn = new SqlConnection(builder.ConnectionString);
                await conn.OpenAsync();
                sw.Stop();

                return new ProbeResult
                {
                    Success = true,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = $"SQL login succeeded | Server: {conn.DataSource} | DB: {conn.Database}"
                };
            }
            catch (SqlException ex)
            {
                sw.Stop();
                return new ProbeResult
                {
                    Success = false,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = $"SQL login failed — Number: {ex.Number}",
                    ErrorDetail = $"SqlException [{ex.Number}] State {ex.State}: {ex.Message}"
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ProbeResult
                {
                    Success = false,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = "SQL login probe threw unexpected error",
                    ErrorDetail = ex.Message
                };
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Probe 4 — EF Context Health Check
        // ─────────────────────────────────────────────────────────────

        private async Task<ProbeResult> ProbeEfContextAsync()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // Cheapest possible EF round-trip
                await _db.Database.ExecuteSqlRawAsync("SELECT 1");
                sw.Stop();

                return new ProbeResult
                {
                    Success = true,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = "EF context executed SELECT 1 successfully"
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ProbeResult
                {
                    Success = false,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = "EF context query failed",
                    ErrorDetail = ex.Message
                };
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Probe 5 — Connection Pool State
        // ─────────────────────────────────────────────────────────────

        private ProbeResult ProbePoolState(string connString)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // SqlConnection.ClearPool forces the pool to report its state
                // We use this as a non-destructive pool inspection
                var builder = new SqlConnectionStringBuilder(connString);
                var poolInfo = $"MaxPoolSize={builder.MaxPoolSize} | " +
                                 $"MinPoolSize={builder.MinPoolSize} | " +
                                 $"Pooling={builder.Pooling} | " +
                                 $"ConnectTimeout={builder.ConnectTimeout}s";
                sw.Stop();

                return new ProbeResult
                {
                    Success = true,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = poolInfo
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ProbeResult
                {
                    Success = false,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = "Pool state probe failed",
                    ErrorDetail = ex.Message
                };
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Root Cause Analysis
        // ─────────────────────────────────────────────────────────────

        private static void ApplyRootCauseAnalysis(DbConnectionDiagnostic diag)
        {
            if (!diag.DnsProbe.Success)
            {
                diag.RootCause = "DNS_RESOLUTION_FAILURE";
                diag.Severity = "CRITICAL";
                diag.RecommendedAction =
                    "DNS cannot resolve the SQL Server hostname. " +
                    "Check DNS server availability, VM hostname registration, " +
                    "and whether the connection string hostname matches the current " +
                    "DNS A-record. On-prem VMs that restart may need DNS re-registration " +
                    "via 'ipconfig /registerdns'.";
                return;
            }

            if (!diag.TcpProbe.Success)
            {
                // DNS resolved but TCP failed — check if it resolved to unexpected IP
                var hasMultipleAddresses = diag.ResolvedAddresses.Count > 1;

                diag.RootCause = diag.TcpProbe.ErrorDetail?.Contains("timed out") == true
                    ? "TCP_TIMEOUT_FIREWALL"
                    : "TCP_CONNECTION_REFUSED";

                diag.Severity = "CRITICAL";
                diag.RecommendedAction =
                    $"DNS resolved to [{string.Join(", ", diag.ResolvedAddresses)}] " +
                    $"but TCP connection to port {diag.ConnectionPort} failed. " +
                    (hasMultipleAddresses
                        ? "Multiple IPs resolved — possible stale DNS cache or CNAME misconfiguration. "
                        : string.Empty) +
                    "Check: SQL Server service is running, port 1433 is open in Windows Firewall, " +
                    "and no network ACL is blocking the connection.";
                return;
            }

            if (!diag.SqlLoginProbe.Success)
            {
                var errorDetail = diag.SqlLoginProbe.ErrorDetail ?? string.Empty;

                // SQL error number classification
                // 18456 = login failed, 4060 = cannot open database,
                // 233 = no process at end of pipe, 10060 = network unreachable
                diag.RootCause = errorDetail.Contains("18456") ? "SQL_AUTH_FAILURE"
                    : errorDetail.Contains("4060") ? "SQL_DATABASE_NOT_FOUND"
                    : errorDetail.Contains("233") ? "SQL_NAMED_PIPES_NO_PROCESS"
                    : "SQL_LOGIN_FAILURE";

                diag.Severity = "CRITICAL";
                diag.RecommendedAction = diag.RootCause switch
                {
                    "SQL_AUTH_FAILURE" =>
                        "Login credentials rejected. Check service account password " +
                        "has not expired and SQL login is enabled.",
                    "SQL_DATABASE_NOT_FOUND" =>
                        "SQL Server is reachable but the target database does not exist " +
                        "or is offline. Check database status in SSMS.",
                    "SQL_NAMED_PIPES_NO_PROCESS" =>
                        "SQL Server process is not responding on the pipe. " +
                        "SQL Server may be starting up or overloaded.",
                    _ =>
                        "Raw SQL connection failed. Review SqlException number " +
                        "in ErrorDetail for specific remediation."
                };
                return;
            }

            if (!diag.EfContextProbe.Success)
            {
                diag.RootCause = "EF_CONTEXT_FAILURE";
                diag.Severity = "HIGH";
                diag.RecommendedAction =
                    "Raw SQL login succeeded but EF context query failed. " +
                    "Likely cause: schema migration not applied, connection pool " +
                    "exhausted under load, or EF execution strategy conflict. " +
                    "Check pending migrations and pool saturation.";
                return;
            }

            // All probes passed
            diag.RootCause = "NONE";
            diag.Severity = "INFO";
            diag.RecommendedAction = "All probes passed. Connectivity is healthy.";
        }

        // ─────────────────────────────────────────────────────────────
        //  Emit to all outputs
        // ─────────────────────────────────────────────────────────────

        private async Task EmitDiagnosticsAsync(DbConnectionDiagnostic diag)
        {
            var summary =
                $"[DbProbe] {diag.RootCause} | " +
                $"DNS={diag.DnsProbe} | " +
                $"TCP={diag.TcpProbe} | " +
                $"SQL={diag.SqlLoginProbe} | " +
                $"EF={diag.EfContextProbe} | " +
                $"Pool={diag.PoolStateProbe}";

            // Console
            Console.WriteLine(summary);

            // Serilog
            if (diag.IsHealthy)
                _logger.LogInformation(summary);
            else
                _logger.LogCritical(
                    "[DbProbe] {RootCause} | Severity: {Severity} | Action: {Action}",
                    diag.RootCause, diag.Severity, diag.RecommendedAction);

            // UnityEvents event bus — best effort, may itself fail if DB is down
            try
            {
                await _publisher.PublishStatusEventAsync(
                    entityType: "INFRASTRUCTURE",
                    entityId: diag.ConnectionHost,
                    domain: "Unity",
                    subDomain: "Database",
                    status: diag.IsHealthy
                                    ? "DB_PROBE_HEALTHY"
                                    : $"DB_PROBE_FAILED_{diag.RootCause}",
                    metadata: diag.ToMetadataDictionary());
            }
            catch
            {
                // If the DB is down this write will fail — that's expected
                // Serilog and Console already captured the diagnostic
                _logger.LogWarning(
                    "[DbProbe] Could not write diagnostic to UnityEvents — DB may be unavailable. " +
                    "Diagnostic captured in Serilog and Console.");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────

        private static (string host, int port) ParseHostPort(string connString)
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(connString);
                var dataSource = builder.DataSource; // e.g. "myserver" or "myserver,1433"

                if (dataSource.Contains(","))
                {
                    var parts = dataSource.Split(',');
                    return (parts[0].Trim(), int.Parse(parts[1].Trim()));
                }

                // Strip instance name if present (e.g. "myserver\INSTANCE")
                var host = dataSource.Contains("\\")
                    ? dataSource.Split('\\')[0]
                    : dataSource;

                return (host, 1433);
            }
            catch
            {
                return ("unknown", 1433);
            }
        }

        private static ProbeResult Skipped(string reason) => new()
        {
            Success = false,
            ElapsedMs = 0,
            Message = reason,
            ErrorDetail = reason
        };
    }
}