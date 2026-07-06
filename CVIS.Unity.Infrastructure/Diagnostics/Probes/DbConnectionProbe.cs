using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CVIS.Unity.Core.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CVIS.Unity.Infrastructure.Diagnostics.Probes
{
    /// <summary>
    /// Database connectivity probe — DNS → TCP → SqlLogin → EfContext → PoolState.
    ///
    /// The same class handles any DbContext. ProbeName is injected at registration
    /// so PolicyDb and any future ReportingDb are separate named probes using
    /// the same implementation.
    ///
    /// Registration example (ServiceCollectionExtensions):
    ///   services.AddScoped<IInfrastructureProbe>(sp => new DbConnectionProbe(
    ///       probeName : "PolicyDb",
    ///       config    : sp.GetRequiredService<IConfiguration>(),
    ///       dbContext : sp.GetRequiredService<PolicyDbContext>(),
    ///       logger    : sp.GetRequiredService<ILogger<DbConnectionProbe>>()));
    /// </summary>
    public class DbConnectionProbe : IInfrastructureProbe
    {
        private readonly IConfiguration _config;
        private readonly DbContext _dbContext;
        private readonly ILogger<DbConnectionProbe> _logger;

        private const int TcpTimeoutMs  = 3000;
        private const int SqlTimeoutSec = 5;

        public string ProbeName { get; }
        public string Category  => "Database";

        public DbConnectionProbe(
            string probeName,
            IConfiguration config,
            DbContext dbContext,
            ILogger<DbConnectionProbe> logger)
        {
            ProbeName  = probeName;
            _config    = config    ?? throw new ArgumentNullException(nameof(config));
            _dbContext = dbContext  ?? throw new ArgumentNullException(nameof(dbContext));
            _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ProbeResult> RunAsync(
            string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            var connString   = _dbContext.Database.GetConnectionString() ?? string.Empty;
            var (host, port) = ParseHostPort(connString);

            var result = new ProbeResult
            {
                ProbeName     = ProbeName,
                Category      = Category,
                CorrelationId = correlationId ?? Guid.NewGuid().ToString(),
                Metadata      =
                {
                    ["Host"]        = host,
                    ["Port"]        = port.ToString(),
                    ["ContextType"] = _dbContext.GetType().Name
                }
            };

            // ── Step 1: DNS ──────────────────────────────────────────
            var dnsStep = await ProbeDnsAsync(host, result, cancellationToken);
            result.Steps.Add(dnsStep);

            // ── Step 2: TCP ──────────────────────────────────────────
            var tcpStep = dnsStep.Success
                ? await ProbeTcpAsync(host, port, cancellationToken)
                : ProbeStep.Skip("TCP", "Skipped — DNS failed");
            result.Steps.Add(tcpStep);

            // ── Step 3: SQL Login ────────────────────────────────────
            var sqlStep = tcpStep.Success && !tcpStep.Skipped
                ? await ProbeSqlLoginAsync(connString, cancellationToken)
                : ProbeStep.Skip("SqlLogin", "Skipped — TCP failed");
            result.Steps.Add(sqlStep);

            // ── Step 4: EF Context ───────────────────────────────────
            var efStep = sqlStep.Success && !sqlStep.Skipped
                ? await ProbeEfContextAsync(cancellationToken)
                : ProbeStep.Skip("EfContext", "Skipped — SQL login failed");
            result.Steps.Add(efStep);

            // ── Step 5: Pool State — always runs ─────────────────────
            result.Steps.Add(ProbePoolState(connString));

            // ── Root Cause Analysis ──────────────────────────────────
            ApplyRootCause(result);

            return result;
        }

        private async Task<ProbeStep> ProbeDnsAsync(
            string host,
            ProbeResult result,
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                sw.Stop();

                var ips = addresses.Select(a => a.ToString()).ToList();
                result.Metadata["ResolvedAddresses"] = string.Join(",", ips);
                result.Metadata["AddressCount"]      = ips.Count.ToString();

                return new ProbeStep
                {
                    StepName  = "DNS",
                    Success   = true,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message   = $"Resolved {ips.Count} address(es): {string.Join(", ", ips)}"
                };
            }
            catch (SocketException ex)
            {
                sw.Stop();
                return new ProbeStep
                {
                    StepName    = "DNS",
                    Success     = false,
                    ElapsedMs   = sw.ElapsedMilliseconds,
                    Message     = "DNS resolution failed",
                    ErrorDetail = $"SocketException [{ex.SocketErrorCode}]: {ex.Message}"
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ProbeStep
                {
                    StepName    = "DNS",
                    Success     = false,
                    ElapsedMs   = sw.ElapsedMilliseconds,
                    Message     = "DNS probe threw unexpected error",
                    ErrorDetail = ex.Message
                };
            }
        }

        private async Task<ProbeStep> ProbeTcpAsync(
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
                    StepName  = "TCP",
                    Success   = true,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message   = $"TCP connected to {host}:{port}"
                };
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return new ProbeStep
                {
                    StepName    = "TCP",
                    Success     = false,
                    ElapsedMs   = sw.ElapsedMilliseconds,
                    Message     = $"TCP timed out after {TcpTimeoutMs}ms",
                    ErrorDetail = $"No response from {host}:{port} — possible firewall block"
                };
            }
            catch (SocketException ex)
            {
                sw.Stop();
                return new ProbeStep
                {
                    StepName    = "TCP",
                    Success     = false,
                    ElapsedMs   = sw.ElapsedMilliseconds,
                    Message     = "TCP connection refused or unreachable",
                    ErrorDetail = $"SocketException [{ex.SocketErrorCode}]: {ex.Message}"
                };
            }
        }

        private async Task<ProbeStep> ProbeSqlLoginAsync(
            string connString, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var builder = new SqlConnectionStringBuilder(connString)
                {
                    ConnectTimeout = SqlTimeoutSec
                };

                await using var conn = new SqlConnection(builder.ConnectionString);
                await conn.OpenAsync(cancellationToken);
                sw.Stop();

                return new ProbeStep
                {
                    StepName  = "SqlLogin",
                    Success   = true,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message   = $"Login OK | Server: {conn.DataSource} | DB: {conn.Database}"
                };
            }
            catch (SqlException ex)
            {
                sw.Stop();
                return new ProbeStep
                {
                    StepName    = "SqlLogin",
                    Success     = false,
                    ElapsedMs   = sw.ElapsedMilliseconds,
                    Message     = $"SQL login failed — error {ex.Number}",
                    ErrorDetail = $"SqlException [{ex.Number}] State {ex.State}: {ex.Message}"
                };
            }
        }

        private async Task<ProbeStep> ProbeEfContextAsync(
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await _dbContext.Database
                    .ExecuteSqlRawAsync("SELECT 1", cancellationToken);
                sw.Stop();

                return new ProbeStep
                {
                    StepName  = "EfContext",
                    Success   = true,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message   = "EF SELECT 1 succeeded"
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ProbeStep
                {
                    StepName    = "EfContext",
                    Success     = false,
                    ElapsedMs   = sw.ElapsedMilliseconds,
                    Message     = "EF context query failed",
                    ErrorDetail = ex.Message
                };
            }
        }

        private static ProbeStep ProbePoolState(string connString)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var b = new SqlConnectionStringBuilder(connString);
                sw.Stop();
                return new ProbeStep
                {
                    StepName  = "PoolState",
                    Success   = true,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message   =
                        $"MaxPool={b.MaxPoolSize} | " +
                        $"MinPool={b.MinPoolSize} | " +
                        $"Pooling={b.Pooling} | " +
                        $"Timeout={b.ConnectTimeout}s"
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ProbeStep
                {
                    StepName    = "PoolState",
                    Success     = false,
                    ElapsedMs   = sw.ElapsedMilliseconds,
                    Message     = "Pool state read failed",
                    ErrorDetail = ex.Message
                };
            }
        }

        private static void ApplyRootCause(ProbeResult result)
        {
            var failed = result.Steps.FirstOrDefault(s => !s.Success && !s.Skipped);

            if (failed == null)
            {
                result.IsHealthy         = true;
                result.RootCause         = "NONE";
                result.Severity          = "INFO";
                result.RecommendedAction = "All database probes passed.";
                return;
            }

            result.IsHealthy = false;

            (result.RootCause, result.Severity, result.RecommendedAction) =
                failed.StepName switch
                {
                    "DNS" => (
                        "DNS_RESOLUTION_FAILURE", "CRITICAL",
                        "Hostname cannot be resolved. Check DNS server, VM hostname " +
                        "registration, and connection string. Run 'ipconfig /registerdns' " +
                        "on the SQL Server VM if recently restarted."),

                    "TCP" when failed.ErrorDetail?.Contains("timed out") == true => (
                        "TCP_TIMEOUT_FIREWALL", "CRITICAL",
                        "DNS resolved but TCP timed out. Check Windows Firewall, " +
                        "network ACLs, and that SQL Server is listening on port 1433."),

                    "TCP" => (
                        "TCP_CONNECTION_REFUSED", "CRITICAL",
                        "DNS resolved but TCP was refused. SQL Server service may be " +
                        "stopped or listening on a non-standard port."),

                    "SqlLogin" when failed.ErrorDetail?.Contains("18456") == true => (
                        "SQL_AUTH_FAILURE", "CRITICAL",
                        "Service account credentials rejected. Check password expiry " +
                        "and SQL login is enabled."),

                    "SqlLogin" when failed.ErrorDetail?.Contains("4060") == true => (
                        "SQL_DATABASE_NOT_FOUND", "CRITICAL",
                        "SQL Server reachable but target database is offline or missing. " +
                        "Check database status in SSMS."),

                    "SqlLogin" when failed.ErrorDetail?.Contains("233") == true => (
                        "SQL_NO_PROCESS_AT_PIPE", "CRITICAL",
                        "SQL Server not responding on pipe — may be starting up or overloaded."),

                    "SqlLogin" => (
                        "SQL_LOGIN_FAILURE", "CRITICAL",
                        "Raw SQL login failed. See ErrorDetail for SqlException number."),

                    "EfContext" => (
                        "EF_CONTEXT_FAILURE", "HIGH",
                        "Raw SQL login succeeded but EF context query failed. " +
                        "Check pending migrations or connection pool exhaustion."),

                    _ => (
                        "UNKNOWN_FAILURE", "HIGH",
                        $"Probe step '{failed.StepName}' failed unexpectedly.")
                };
        }

        private static (string host, int port) ParseHostPort(string connString)
        {
            try
            {
                var b          = new SqlConnectionStringBuilder(connString);
                var dataSource = b.DataSource;

                if (dataSource.Contains(","))
                {
                    var parts = dataSource.Split(',');
                    return (parts[0].Trim(), int.Parse(parts[1].Trim()));
                }

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
    }
}
