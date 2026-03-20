using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Infrastructure;
using CVIS.Unity.Infrastructure.Data;
using CVIS.Unity.PolicyDrift.Orchestration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using Serilog;
using Serilog.Sinks.MSSqlServer;

namespace CVIS.Unity.PolicyDrift.ConsoleHost
{

    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Build temporary config to get the ConnectionString for the Logger
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json")
                .Build();

            var connectionString = config.GetConnectionString("DefaultConnection");

            // Define the SQL Sink Options
            var sinkOptions = new MSSqlServerSinkOptions
            {
                TableName = "LogEvents",
                SchemaName = "unity", // Explicitly set schema
                AutoCreateSqlTable = true
            };

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File("logs/CVIS_Drift_Log_.txt", rollingInterval: RollingInterval.Day)
                .WriteTo.MSSqlServer(
                    connectionString: connectionString,
                    sinkOptions: sinkOptions)
                .WriteTo.OpenTelemetry(options =>
                {
                    options.Endpoint = "http://your-internal-otel-collector:4317"; // OTLP Port
                    options.Protocol = (Serilog.Sinks.OpenTelemetry.OtlpProtocol)OtlpExportProtocol.Grpc;
                })
                .CreateLogger();

            try
            {
                Log.Information("≛ CVIS.Unity.PolicyDrift Starting Up...");
                IHost host = CreateHostBuilder(args).Build();
                await RunOrchestratorAsync(host.Services);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "≛ Application Terminated Unexpectedly.");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseSerilog() // This "plugs" Serilog into the MS DI system
                .ConfigureServices((hostContext, services) =>
                {
                    // Infrastructure (DB, FileSystem, Kafka Placeholder)
                    services.AddPolicyDriftInfrastructure(hostContext.Configuration);

                    // Orchestration (FileProcessor, Workflows)
                    services.AddPolicyDriftOrchestration();
                });

        // Program.cs -> RunOrchestratorAsync
        private static async Task RunOrchestratorAsync(IServiceProvider services)
        {
            using (var scope = services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<PolicyDbContext>();
                var publisher = scope.ServiceProvider.GetRequiredService<IUnityEventPublisher>();

                try
                {
                    // 1. Apply all pending migrations to THOUSANDSUNNY
                    // This will create the 'unity' schema and all tables in your migration file.
                    publisher.LogInfo("Applying Database Migrations...");
                    await context.Database.MigrateAsync();

                    publisher.LogInfo("Database is up to date.");
                }
                catch (Exception ex)
                {
                    // If the schema 'unity' already exists and causes a conflict, 
                    // we catch it here to prevent the whole app from dying.
                    publisher.LogWarning($"Migration Note: {ex.Message}");
                }

                // 2. Launch the workflows
                var workflows = scope.ServiceProvider.GetServices<IPolicyWorkflow>();
                foreach (var workflow in workflows)
                {
                    await workflow.ExecuteAsync();
                }
            }
        }
    }
}