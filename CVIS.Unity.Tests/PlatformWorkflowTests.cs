using CVIS.Unity.Core.Entities;
using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Infrastructure.Data;
using CVIS.Unity.Infrastructure.Services;
using CVIS.Unity.PolicyDrift.Orchestration.Services;
using CVIS.Unity.PolicyDrift.Orchestrator.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using NUnit.Framework;

namespace CVIS.Unity.Tests
{
    [TestFixture]
    public class PlatformWorkflowTests
    {
        private Mock<IUnityEventPublisher> _publisher;
        private PolicyDbContext _dbContext;
        private PlatformWorkflow _workflow;

        [SetUp]
        public void Setup()
        {
            _publisher = new Mock<IUnityEventPublisher>();

            var options = new DbContextOptionsBuilder<PolicyDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            _dbContext = new PolicyDbContext(options);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();

            _workflow = new PlatformWorkflow(
                new Mock<IFileSystemService>().Object,
                _publisher.Object,
                new Mock<IPolicyDriftPathProvider>().Object,
                config,
                new Mock<FileProcessor>().Object,
                new Mock<ISignalFileService>().Object,
                new DriftComparisonService(config, _publisher.Object),
                _dbContext);
        }

        [TearDown]
        public void TearDown()
        {
            _dbContext?.Dispose();
        }

        [Test]
        public async Task EvalLogic_CleanPath_ShouldRecordNoDrift()
        {
            var policyId = "ANS-MATCH";
            var data = new Dictionary<string, string> { { "INI:Timeout", "200" } };

            await CreateBaseline(policyId, data);

            // Simulate what ProcessSinglePolicy does: compare, then save eval
            var driftReport = _workflow.CompareAttributes(data, data);
            bool hasDrift = driftReport.Count > 0;

            var eval = new PolicyDriftEval
            {
                Id = Guid.NewGuid(),
                PolicyId = policyId,
                BaselinePolicyID = (await _dbContext.PlatformBaselines
                    .FirstAsync(b => b.PlatformId == policyId && b.IsActive)).Id,
                PolicyDriftEvalDetailsID = Guid.Empty,
                Differences = hasDrift ? driftReport : null,
                Status = hasDrift ? "DRIFT" : "NO_DRIFT",
                RunTimestamp = DateTime.UtcNow,
                ExecutionId = "TEST-EXEC"
            };

            await _dbContext.LogDriftEvalAsync(eval);

            var savedEval = await _dbContext.PolicyDriftEvals.FirstAsync(e => e.PolicyId == policyId);
            Assert.That(savedEval.Status, Is.EqualTo("NO_DRIFT"));
            Assert.That(savedEval.Differences, Is.Null);
        }

        [Test]
        public async Task EvalLogic_DriftPath_ShouldRecordDrift()
        {
            var policyId = "ANS-DRIFT";
            var baselineData = new Dictionary<string, string> { { "INI:Timeout", "200" } };
            var currentData = new Dictionary<string, string> { { "INI:Timeout", "500" } };

            await CreateBaseline(policyId, baselineData);

            var driftReport = _workflow.CompareAttributes(baselineData, currentData);
            bool hasDrift = driftReport.Count > 0;

            var eval = new PolicyDriftEval
            {
                Id = Guid.NewGuid(),
                PolicyId = policyId,
                BaselinePolicyID = (await _dbContext.PlatformBaselines
                    .FirstAsync(b => b.PlatformId == policyId && b.IsActive)).Id,
                PolicyDriftEvalDetailsID = Guid.Empty,
                Differences = hasDrift ? driftReport : null,
                Status = hasDrift ? "DRIFT" : "NO_DRIFT",
                RunTimestamp = DateTime.UtcNow,
                ExecutionId = "TEST-EXEC"
            };

            await _dbContext.LogDriftEvalAsync(eval);

            var savedEval = await _dbContext.PolicyDriftEvals.FirstAsync(e => e.PolicyId == policyId);
            Assert.That(savedEval.Status, Is.EqualTo("DRIFT"));
            Assert.That(savedEval.Differences, Is.Not.Null);
            Assert.That(savedEval.Differences!["INI:Timeout"], Does.Contain("MODIFIED"));
        }

        [Test]
        public async Task EvalLogic_OrphanPath_ShouldRecordMissingBaseline()
        {
            var policyId = "ANS-ORPHAN";

            // No baseline exists — simulate the MISSING_BASELINE path
            var baseline = await _dbContext.PlatformBaselines
                .FirstOrDefaultAsync(b => b.PlatformId == policyId && b.IsActive);

            Assert.That(baseline, Is.Null);

            var eval = new PolicyDriftEval
            {
                Id = Guid.NewGuid(),
                PolicyId = policyId,
                BaselinePolicyID = Guid.Empty,
                PolicyDriftEvalDetailsID = Guid.Empty,
                Status = "MISSING_BASELINE",
                RunTimestamp = DateTime.UtcNow,
                ExecutionId = "TEST-EXEC"
            };

            await _dbContext.LogDriftEvalAsync(eval);

            var savedEval = await _dbContext.PolicyDriftEvals.FirstAsync(e => e.PolicyId == policyId);
            Assert.That(savedEval.Status, Is.EqualTo("MISSING_BASELINE"));
            Assert.That(savedEval.BaselinePolicyID, Is.EqualTo(Guid.Empty));
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
}