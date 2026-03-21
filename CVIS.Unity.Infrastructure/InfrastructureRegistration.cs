using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Infrastructure.Data;
using CVIS.Unity.Infrastructure.Messaging;
using CVIS.Unity.Infrastructure.Monitoring;
using CVIS.Unity.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace CVIS.Unity.Infrastructure
{
    public static class InfrastructureRegistration
    {
        public static IServiceCollection AddPolicyDriftInfrastructure(this IServiceCollection services, IConfiguration config)
        {
            // 1. Database Context (Scoped) SQL 2018 Connection
            var connectionString = config.GetConnectionString("DefaultConnection");
            services.AddDbContext<PolicyDbContext>(options =>
                options.UseSqlServer(connectionString));

            // 2. The Publisher - Changed to Scoped to fix the DI Lifetime issue
            // Unity Placeholder
            // Replace with actual Kafka implementation later
            // The ImmediateUnityPublisher is a simple implementation that logs events directly.
            // Replace with KafkaPublisher when ready.
            services.AddScoped<IUnityEventPublisher, ImmediateUnityPublisher>();

            // 3. Core Services
            
            // System Services
            services.AddTransient<IFileSystemService, FileSystemService>();
            
            // Mock CyberArk Vault Service contains, not real API calls,
            // but simulates the expected behavior for testing and development.
            services.AddTransient<ICyberArkVaultService, MockVaultService>();
            services.AddTransient<DbInitializer>();

            // 4. Telemetry (The OTLP Hook)
            services.AddTransient<IFileSystemService, FileSystemService>();

            // Register the new policy extraction service
            services.AddTransient<IPackageExtractionService, PackageExtractionService>();

            // Register the new Baseline Update Service
            services.AddTransient<ISignalFileService, SignalFileService>();


            // Register the new pathing authority
            services.AddTransient<IPolicyDriftPathProvider, PolicyDriftPathProvider>();

            // Register the new drift comparison service
            services.AddSingleton<IDriftComparisonService, DriftComparisonService>();

            return services;
        }

        public static IServiceCollection AddUnityTelemetry(this IServiceCollection services, IConfiguration config)
        {
            var otlpEndpoint = config["Infrastructure:OtlpEndpoint"];

            // Skip OTLP if no endpoint is defined (Dev-friendly)
            if (string.IsNullOrEmpty(otlpEndpoint)) return services;

            services.AddOpenTelemetry()
                .WithTracing(tracing => tracing
                    .AddHttpClientInstrumentation()
                    .AddSource("CVIS.Unity.PolicyDrift")
                    .AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint)))
                .WithMetrics(metrics => metrics
                    .AddRuntimeInstrumentation()
                    .AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint)));

            return services;
        }
    }
}