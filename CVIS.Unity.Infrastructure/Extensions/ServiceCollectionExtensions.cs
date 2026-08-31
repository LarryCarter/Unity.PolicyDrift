using System;
using CVIS.Unity.Core.Diagnostics;
using CVIS.Unity.Infrastructure.Data;
using CVIS.Unity.Infrastructure.Diagnostics;
using CVIS.Unity.Infrastructure.Diagnostics.Probes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CVIS.Unity.Infrastructure.Extensions
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the full Infrastructure Diagnostic Framework:
        ///   - All IInfrastructureProbe implementations
        ///   - IInfrastructureHealthService (probe registry + orchestrator)
        ///   - DbDiagnosticInterceptor (reactive EF interceptor)
        ///   - Named HTTP clients for API probes
        ///
        /// To add a new context probe:
        ///   services.AddScoped<IInfrastructureProbe>(sp => new DbConnectionProbe(
        ///       probeName : "ReportingDb",
        ///       config    : sp.GetRequiredService<IConfiguration>(),
        ///       dbContext : sp.GetRequiredService<ReportingDbContext>(),
        ///       logger    : sp.GetRequiredService<ILogger<DbConnectionProbe>>()));
        ///
        /// To add a new probe type: implement IInfrastructureProbe,
        /// register here with AddScoped<IInfrastructureProbe, YourProbe>().
        /// Nothing else changes.
        /// </summary>
        public static IServiceCollection AddUnityInfrastructureHealth(
            this IServiceCollection services)
        {
            // ── Database probes — one per DbContext ──────────────────
            services.AddScoped<IInfrastructureProbe>(sp => new DbConnectionProbe(
                probeName : "PolicyDb",
                config    : sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
                dbContext : sp.GetRequiredService<PolicyDbContext>(),
                logger    : sp.GetRequiredService<ILogger<DbConnectionProbe>>()));

            // ── Messaging probes ─────────────────────────────────────
            services.AddScoped<IInfrastructureProbe, KafkaProbe>();

            // ── External API probes ───────────────────────────────────
            services.AddScoped<IInfrastructureProbe, ServiceNowProbe>();
            services.AddScoped<IInfrastructureProbe, PvwaProbe>();

            // ── Health service — gets all probes via IEnumerable<IInfrastructureProbe>
            services.AddScoped<IInfrastructureHealthService, InfrastructureHealthService>();

            // ── EF interceptor — reactive, fires at point of failure ──
            services.AddScoped<DbDiagnosticInterceptor>();

            // ── Named HTTP clients for API probes ─────────────────────
            services.AddHttpClient("ServiceNow")
                .SetHandlerLifetime(TimeSpan.FromMinutes(5));

            services.AddHttpClient("PVWA")
                .SetHandlerLifetime(TimeSpan.FromMinutes(5));

            return services;
        }
    }
}
