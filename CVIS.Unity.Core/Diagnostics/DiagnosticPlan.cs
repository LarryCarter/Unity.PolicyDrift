using System.Collections.Generic;

namespace CVIS.Unity.Core.Diagnostics
{
    /// <summary>
    /// Describes exactly which probes to run and what context to capture
    /// based on the classified exception. Produced by ExceptionClassifier
    /// and consumed by DbDiagnosticInterceptor.
    ///
    /// Design intent: the exception type and error number are the first
    /// triage signal. A SqlException 18456 (auth failed) tells us TCP
    /// and DNS worked — no point probing those layers. A SocketException
    /// tells us nothing worked — full chain required.
    /// </summary>
    public class DiagnosticPlan
    {
        /// <summary>
        /// What we already know from the exception alone —
        /// before running any probes.
        /// </summary>
        public string InitialClassification { get; set; } = string.Empty;

        /// <summary>
        /// Which layer failed based on exception type/number.
        /// Network, Authentication, Database, Application, Unknown
        /// </summary>
        public string FailureLayer { get; set; } = string.Empty;

        /// <summary>
        /// Is this error type known to be intermittent?
        /// Drives whether we do pattern analysis against UnityEvents history.
        /// </summary>
        public bool IsKnownIntermittent { get; set; }

        /// <summary>
        /// Which named probes to run based on the exception.
        /// We do not run DNS if auth failed — TCP already worked.
        /// Order matters: probes run in sequence, each informing the next.
        /// </summary>
        public List<string> ProbeSequence { get; set; } = new();

        /// <summary>
        /// What context to capture regardless of probe results.
        /// Time of day, pool state, retry count, execution context.
        /// These go into the UnityEvent Metadata dictionary.
        /// </summary>
        public List<string> CaptureTargets { get; set; } = new();

        /// <summary>
        /// What question this investigation is trying to answer.
        /// Surfaced in the DiagnosticEvent Metadata so ops knows
        /// exactly what to look for without reading code.
        /// </summary>
        public string InvestigationQuestion { get; set; } = string.Empty;

        /// <summary>
        /// If true, this exception type warrants pattern analysis
        /// across historical UnityEvents to detect time-of-day correlation.
        /// Intermittent auth failures and sporadic network errors both benefit.
        /// </summary>
        public bool RunPatternAnalysis { get; set; }

        /// <summary>
        /// Severity determines how urgently the result is escalated
        /// and which log level is used. CRITICAL, HIGH, MEDIUM, INFO.
        /// </summary>
        public string Severity { get; set; } = "HIGH";
    }
}
