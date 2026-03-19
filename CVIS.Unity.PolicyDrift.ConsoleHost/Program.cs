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

        private static async Task RunOrchestratorAsync(IServiceProvider services)
        {
            using (var scope = services.CreateScope())
            {
                var scopedProvider = scope.ServiceProvider;

                try
                {
                    // 1. Database & Schema Initialization
                    var context = scopedProvider.GetRequiredService<PolicyDbContext>();
                    var publisher = scopedProvider.GetRequiredService<IUnityEventPublisher>();

                    publisher.LogInfo("Verifying Database Schema 'unity' on THOUSANDSUNNY...");

                    // Cryptorion: Validated. Execute raw SQL for schema before EF handles tables.
                    await context.Database.ExecuteSqlRawAsync(
                        "IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'unity') " +
                        "BEGIN EXEC('CREATE SCHEMA unity') END");

                    // Ensures tables exist without requiring manual migrations 
                    await context.Database.EnsureCreatedAsync();

                    // 2. Workflow Execution
                    var workflows = scopedProvider.GetServices<IPolicyWorkflow>();

                    foreach (var workflow in workflows)
                    {
                        publisher.LogInfo($"--> Launching Workflow: {workflow.WorkflowName}");
                        await workflow.ExecuteAsync();
                    }
                }
                catch (Exception ex)
                {
                    // Datyrix: Ensure we log to Serilog/SQL if the init fails
                    var logger = scopedProvider.GetRequiredService<ILogger<Program>>();
                    logger.LogCritical(ex, "Orchestration failed during initialization or execution.");
                    throw; // Re-throw to be caught by the Main try-catch safety net
                }
            }
        }
    }
}