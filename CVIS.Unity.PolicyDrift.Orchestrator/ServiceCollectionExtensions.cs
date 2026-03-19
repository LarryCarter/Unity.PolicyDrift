using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.PolicyDrift.Orchestration.Services;
using CVIS.Unity.PolicyDrift.Orchestrator.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace CVIS.Unity.PolicyDrift.Orchestration
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddPolicyDriftOrchestration(this IServiceCollection services)
        {
            // The Logic Engine
            services.AddTransient<FileProcessor>();

            // The Workflows - Add more here as the project grows
            services.AddTransient<IPolicyWorkflow, PlatformWorkflow>();

            return services;
        }
    }
}