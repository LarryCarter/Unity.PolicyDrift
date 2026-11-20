using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Core.Monitoring
{
    /// ─────────────────────────────────────────────────────────────
    //  Result envelope — success or a reason the run can't proceed
    // ─────────────────────────────────────────────────────────────
    public sealed class PolicyDriftContextResult
    {
        public PolicyDriftContext? Context { get; }
        public string? Error { get; }
        public bool IsValid => Context is not null;

        private PolicyDriftContextResult(PolicyDriftContext? context, string? error)
        {
            Context = context;
            Error = error;
        }

        public static PolicyDriftContextResult Success(PolicyDriftContext ctx) => new(ctx, null);
        public static PolicyDriftContextResult Failure(string error) => new(null, error);
    }
}
