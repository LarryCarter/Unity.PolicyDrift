using System.Threading.Tasks;

namespace CVIS.Unity.Infrastructure.Diagnostics
{
    public interface IDbConnectionProbe
    {
        Task<DbConnectionDiagnostic> RunFullProbeAsync(
            string? executionId = null);
    }
}