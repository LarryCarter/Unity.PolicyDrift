using CVIS.Unity.Core.Entities;
using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Infrastructure.Data;
using CVIS.Unity.PolicyDrift.Orchestration.Services;
using CVIS.Unity.PolicyDrift.Orchestrator.Workflows;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace CVIS.Unity.Tests
{
    [TestFixture]
    public class PlatformWorkflowTests
    {
        private Mock<IUnityEventPublisher> _publisher;
        private Mock<FileProcessor> _fileProcessor;
        private PolicyDbContext _dbContext;
        private TestablePlatformWorkflow _workflow;

        [SetUp]
        public void Setup()
        {
            _publisher = new Mock<IUnityEventPublisher>();
            _fileProcessor = new Mock<FileProcessor>();

            // 1. Initialize the InMemory Database
            var options = new DbContextOptionsBuilder<PolicyDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            _dbContext = new PolicyDbContext(options);

            // 2. Resolve Constructor Error: Pass all 3 required dependencies
            _workflow = new TestablePlatformWorkflow(
                _publisher.Object,
                _fileProcessor.Object,
                _dbContext);
        }

        [TearDown]
        public void TearDown()
        {
            // Datyrix: Quench the forge to prevent memory leaks
            _dbContext?.Dispose();
        }

        [Test]
        public async Task HandleDriftCheck_CleanPath_ShouldLogInfo()
        {
            var policyId = "ANS-MATCH";
            var data = new Dictionary<string, string> { { "INI:Timeout", "200" } };

            await CreateBaseline(policyId, data);
            _workflow.MockDiscovery(data);

            await _workflow.TriggerDriftCheck(policyId);

            // Assert Status Trinity: NO_DRIFT
            var eval = await _dbContext.PolicyDriftEvals.FirstAsync(e => e.PolicyId == policyId);
            Assert.That(eval.Status, Is.EqualTo("NO_DRIFT"));
            _publisher.Verify(p => p.LogInfo(It.Is<string>(s => s.Contains("[CLEAN]"))), Times.Once);
        }

        [Test]
        public async Task HandleDriftCheck_DriftPath_ShouldLogCritical()
         {
            var policyId = "ANS-DRIFT";
            await CreateBaseline(policyId, new Dictionary<string, string> { { "INI:Timeout", "200" } });
            _workflow.MockDiscovery(new Dictionary<string, string> { { "INI:Timeout", "500" } });

            await _workflow.TriggerDriftCheck(policyId);

            // Assert Status Trinity: DRIFT
            var eval = await _dbContext.PolicyDriftEvals.FirstAsync(e => e.PolicyId == policyId);
            Assert.That(eval.Status, Is.EqualTo("DRIFT"));
            _publisher.Verify(p => p.LogWarning(It.Is<string>(s => s.Contains("[DRIFT]"))), Times.Once);
        }

        [Test]
        public async Task HandleDriftCheck_OrphanPath_ShouldLogMissing()
        {
            var policyId = "ANS-ORPHAN";
            _workflow.MockDiscovery(new Dictionary<string, string>());

            await _workflow.TriggerDriftCheck(policyId);

            // Assert Status Trinity: MISSING_BASELINE
            var eval = await _dbContext.PolicyDriftEvals.FirstAsync(e => e.PolicyId == policyId);
            Assert.That(eval.Status, Is.EqualTo("MISSING_BASELINE"));
            Assert.That(eval.BaselinePolicyID, Is.EqualTo(Guid.Empty));
        }

        private async Task CreateBaseline(string id, Dictionary<string, string> attr)
        {
            _dbContext.PlatformBaselines.Add(new PlatformBaseline
            {
                PlatformId = id,
                Attributes = attr,
                IsActive = true
            });
            await _dbContext.SaveChangesAsync();
        }
    }

    // High-Integrity Wrapper for NUnit Testing
    public class TestablePlatformWorkflow : PlatformWorkflow
    {
        private Dictionary<string, string> _vAttr;

        public TestablePlatformWorkflow(IUnityEventPublisher pub, FileProcessor fp, PolicyDbContext db)
            : base(null!, pub, null!, fp, db) { }

        // ADD THIS — bypasses null _configuration
        protected override string GetCurrentBatchPath() => @"C:\MockRepo\Operations\PlatformPolicies\test";

        public void MockDiscovery(Dictionary<string, string> data) => _vAttr = data;

        public async Task TriggerDriftCheck(string id) => await HandleDriftCheck(id);

        public new Task<(Dictionary<string, string>, Dictionary<string, string>)> GetDiscoveryData(string id)
        {
            return Task.FromResult((_vAttr, new Dictionary<string, string>()));
        }
    }
}