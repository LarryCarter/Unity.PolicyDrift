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
                var scopedProvider = scope.ServiceProvider;

                try
                {
                    // 1. Initialise the Arsenal (Schema & Migrations)
                    var initializer = scopedProvider.GetRequiredService<DbInitializer>();
                    await initializer.InitializeAsync();

                    // 2. Resolve Core Dependencies
                    var publisher = scopedProvider.GetRequiredService<IUnityEventPublisher>();
                    var policyWorkflows = scopedProvider.GetServices<IPolicyWorkflow>();

                    // 3. Execution Loop
                    foreach (var workflow in policyWorkflows)
                    {
                        publisher.LogInfo($"--> Launching Workflow: {workflow.WorkflowName}");

                        // Datyrix: Execute the individual platform audit
                        await workflow.ExecuteAsync();
                    }
                }
                catch (Exception ex)
                {
                    // Ensure the failure is captured in the System of Record (SQL/Serilog)
                    var logger = scopedProvider.GetRequiredService<ILogger<Program>>();
                    logger.LogCritical(ex, "Orchestration failed during initialization or execution.");

                    // Re-throw to trigger the Main catch-block safety net
                    throw;
                }
            }
        }
    }
}