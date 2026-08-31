using System;
using System.Data.Common;
using System.Reflection;
using System.Net.Sockets;
using CVIS.Unity.Core.Diagnostics;

namespace CVIS.Unity.Infrastructure.Diagnostics
{
    /// <summary>
    /// Reads an exception and returns a DiagnosticPlan — a description
    /// of exactly which probes to run and what context to capture.
    ///
    /// The exception type and SQL error number are the first triage signal.
    /// This classifier encodes the knowledge of which network layer each
    /// error number implies, so probes are not wasted on layers that
    /// clearly worked (e.g. no DNS probe after a SQL 18456 auth failure —
    /// DNS must have worked or we would have a different error).
    ///
    /// Triage tree:
    ///   SqlException 18456  → Auth layer      → PoolState + SqlLogin only
    ///   SqlException 4060   → DB layer         → SqlLogin + EfContext only
    ///   SqlException 10060  → Network layer    → Full chain (your sporadic error)
    ///   SqlException 233    → SQL Server layer → TCP + PoolState + SqlLogin
    ///   SqlException -2     → Timeout          → TCP + PoolState
    ///   SocketException     → Network layer    → Full chain
    ///   TimeoutException    → Unknown          → TCP + PoolState
    ///   Other               → Unknown          → Full chain
    /// </summary>
    public static class ExceptionClassifier
    {
        public static DiagnosticPlan Classify(Exception ex)
        {
            return ex switch
            {
                SocketException  => ClassifySocketException(),
                TimeoutException => ClassifyTimeoutException(),
                DbException db   => ClassifyDatabaseException(db),
                _                => ClassifyUnknown(ex)
            };
        }

        // ─────────────────────────────────────────────────────────
        //  SQL Exception — error number tells us the layer
        // ─────────────────────────────────────────────────────────

        private static DiagnosticPlan ClassifyDatabaseException(DbException ex)
        {
            var errorCode = GetDatabaseErrorCode(ex);
            return errorCode switch
            {
                // ── Auth failure ──────────────────────────────────
                // TCP worked, SQL Server responded — network is fine.
                // Auth failures can be intermittent when passwords rotate
                // or when a service account is locked under AD policy.
                18456 => new DiagnosticPlan
                {
                    InitialClassification = "SQL_AUTH_FAILURE",
                    FailureLayer = "Authentication",
                    Severity = "HIGH",
                    IsKnownIntermittent = true,
                    ProbeSequence = new() { "PoolState", "SqlLogin" },
                    CaptureTargets = new()
                    {
                        "TimeOfDay",
                        "DayOfWeek",
                        "ConnectionPoolState",
                        "ServiceAccount",
                        "RetryCount"
                    },
                    InvestigationQuestion =
                        "Is this a consistent auth failure or intermittent? " +
                        "If intermittent, does it correlate with a time window " +
                        "that matches a password rotation or AD policy cycle?",
                    RunPatternAnalysis = true
                },

                // ── Database not found / offline ──────────────────
                // SQL Server responded — the DB itself is the issue.
                4060 => new DiagnosticPlan
                {
                    InitialClassification = "SQL_DATABASE_NOT_FOUND",
                    FailureLayer = "Database",
                    Severity = "CRITICAL",
                    IsKnownIntermittent = false,
                    ProbeSequence = new() { "SqlLogin", "EfContext" },
                    CaptureTargets = new()
                    {
                        "TimeOfDay",
                        "DatabaseName",
                        "ServerInstance"
                    },
                    InvestigationQuestion =
                        "Is the target database offline, in recovery mode, " +
                        "or has the connection string drifted from the actual " +
                        "database name? Check SSMS for database status.",
                    RunPatternAnalysis = false
                },

                // ── Network unreachable — the sporadic mystery error ─
                // This is the primary target of this framework.
                // Full chain required — we do not know which layer broke.
                10060 or 10061 or -1 => new DiagnosticPlan
                {
                    InitialClassification = "NETWORK_UNREACHABLE",
                    FailureLayer = "Network",
                    Severity = "CRITICAL",
                    IsKnownIntermittent = true,
                    ProbeSequence = new()
                    {
                        "DNS", "TCP", "SqlLogin", "EfContext", "PoolState"
                    },
                    CaptureTargets = new()
                    {
                        "TimeOfDay",
                        "DayOfWeek",
                        "ResolvedIpAddress",
                        "TcpLatencyMs",
                        "ConnectionPoolState",
                        "RetryCount",
                        "ExecutionId"
                    },
                    InvestigationQuestion =
                        "Is this a DNS resolution failure, TCP routing issue, " +
                        "or firewall rule? Does the resolved IP change when the " +
                        "error occurs? Does this correlate with a specific time " +
                        "window, backup job, or VM maintenance event?",
                    RunPatternAnalysis = true
                },

                // ── No process at end of pipe ─────────────────────
                // SQL Server running but not accepting connections.
                // Often seen when SQL Server is starting up or overloaded.
                233 => new DiagnosticPlan
                {
                    InitialClassification = "SQL_NO_PROCESS_AT_PIPE",
                    FailureLayer = "SqlServer",
                    Severity = "HIGH",
                    IsKnownIntermittent = true,
                    ProbeSequence = new() { "TCP", "PoolState", "SqlLogin" },
                    CaptureTargets = new()
                    {
                        "TimeOfDay",
                        "ConnectionPoolState",
                        "TcpLatencyMs",
                        "RetryCount"
                    },
                    InvestigationQuestion =
                        "Is SQL Server overloaded or starting up? " +
                        "Is the connection pool exhausted? " +
                        "Does this correlate with high-load periods?",
                    RunPatternAnalysis = true
                },

                // ── Timeout ───────────────────────────────────────
                // Could be network latency, pool exhaustion, or slow query.
                -2 => new DiagnosticPlan
                {
                    InitialClassification = "SQL_COMMAND_TIMEOUT",
                    FailureLayer = "Database",
                    Severity = "MEDIUM",
                    IsKnownIntermittent = true,
                    ProbeSequence = new() { "TCP", "PoolState" },
                    CaptureTargets = new()
                    {
                        "TimeOfDay",
                        "CommandText",
                        "ElapsedMs",
                        "ConnectionPoolState",
                        "RetryCount"
                    },
                    InvestigationQuestion =
                        "Was this a slow query or network latency? " +
                        "Is this a specific query that always times out " +
                        "or random? Does it correlate with backup windows?",
                    RunPatternAnalysis = true
                },

                // ── Default SQL catch-all ─────────────────────────
                _ => new DiagnosticPlan
                {
                    InitialClassification = $"DATABASE_EXCEPTION_{errorCode}",
                    FailureLayer = "Unknown",
                    Severity = "HIGH",
                    IsKnownIntermittent = false,
                    ProbeSequence = new()
                    {
                        "DNS", "TCP", "SqlLogin", "EfContext", "PoolState"
                    },
                    CaptureTargets = new()
                    {
                        "TimeOfDay", "RetryCount", "ConnectionPoolState"
                    },
                    InvestigationQuestion =
                        $"Database provider error {errorCode} — " +
                        "run full probe chain to determine layer.",
                    RunPatternAnalysis = false
                }
            };
        }

        internal static int GetDatabaseErrorCode(DbException ex)
        {
            var numberProperty = ex.GetType().GetProperty(
                "Number", BindingFlags.Instance | BindingFlags.Public);

            return numberProperty?.PropertyType == typeof(int) &&
                   numberProperty.GetValue(ex) is int providerCode
                ? providerCode
                : ex.ErrorCode;
        }

        // ─────────────────────────────────────────────────────────
        //  Socket Exception — below SQL layer entirely
        //  Same signature as the sporadic DB not found error
        // ─────────────────────────────────────────────────────────

        private static DiagnosticPlan ClassifySocketException() => new()
        {
            InitialClassification = "SOCKET_EXCEPTION",
            FailureLayer          = "Network",
            Severity              = "CRITICAL",
            IsKnownIntermittent   = true,
            ProbeSequence         = new() { "DNS", "TCP", "SqlLogin", "PoolState" },
            CaptureTargets        = new()
            {
                "TimeOfDay",
                "DayOfWeek",
                "ResolvedIpAddress",
                "TcpLatencyMs",
                "ConnectionPoolState"
            },
            InvestigationQuestion =
                "Network layer failure below SQL. " +
                "Full DNS and TCP probe required. " +
                "This is the same signature as the sporadic DB not found error.",
            RunPatternAnalysis = true
        };

        // ─────────────────────────────────────────────────────────
        //  Timeout Exception — could be network or application
        // ─────────────────────────────────────────────────────────

        private static DiagnosticPlan ClassifyTimeoutException() => new()
        {
            InitialClassification = "TIMEOUT_EXCEPTION",
            FailureLayer          = "Unknown",
            Severity              = "MEDIUM",
            IsKnownIntermittent   = true,
            ProbeSequence         = new() { "TCP", "PoolState" },
            CaptureTargets        = new()
            {
                "TimeOfDay", "ElapsedMs", "ConnectionPoolState"
            },
            InvestigationQuestion =
                "Was this a network timeout or application timeout? " +
                "Check TCP latency and pool state.",
            RunPatternAnalysis = true
        };

        // ─────────────────────────────────────────────────────────
        //  Unknown — run everything
        // ─────────────────────────────────────────────────────────

        private static DiagnosticPlan ClassifyUnknown(Exception ex) => new()
        {
            InitialClassification = $"UNKNOWN_{ex.GetType().Name}",
            FailureLayer          = "Unknown",
            Severity              = "HIGH",
            IsKnownIntermittent   = false,
            ProbeSequence         = new()
            {
                "DNS", "TCP", "SqlLogin", "EfContext", "PoolState"
            },
            CaptureTargets = new()
            {
                "TimeOfDay", "RetryCount", "ConnectionPoolState"
            },
            InvestigationQuestion =
                "Unknown exception type — full probe chain required.",
            RunPatternAnalysis = false
        };
    }
}
