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
    public class PlatformDriftTests
    {
        private PlatformWorkflow _workflow;
        private Mock<IUnityEventPublisher> _mockPublisher;
        private PolicyDbContext _dbContext;

        [SetUp]
        public void Setup()
        {
            var mockFileSystem = new Mock<IFileSystemService>();
            _mockPublisher = new Mock<IUnityEventPublisher>();
            var mockDriftPath = new Mock<IPolicyDriftPathProvider>();
            var mockFileProcessor = new Mock<FileProcessor>();
            var mockSignalFiles = new Mock<ISignalFileService>();

            // Real config with all defaults — all scopes enabled, SNOW not required
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();

            var driftComparison = new DriftComparisonService(config, _mockPublisher.Object);

            var options = new DbContextOptionsBuilder<PolicyDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            _dbContext = new PolicyDbContext(options);

            _workflow = new PlatformWorkflow(
                mockFileSystem.Object,
                _mockPublisher.Object,
                mockDriftPath.Object,
                config,
                mockFileProcessor.Object,
                mockSignalFiles.Object,
                driftComparison,
                _dbContext);
        }

        [TearDown]
        public void TearDown()
        {
            _dbContext?.Dispose();
        }

        [Test]
        public void CompareAttributes_Should_Ignore_Noise_Keys()
        {
            var baseline = new Dictionary<string, string>
            {
                { "INI:Timeout", "200" },
                { "INI:ApiVersion", "v1" }
            };
            var current = new Dictionary<string, string>
            {
                { "INI:Timeout", "200" },
                { "INI:ApiVersion", "v2" }
            };

            var drift = _workflow.CompareAttributes(baseline, current);

            Assert.That(drift.Count, Is.EqualTo(0),
                "Drift should be empty when only ignored keys change.");
        }

        [Test]
        public void CompareAttributes_Should_Detect_All_Drift_Types()
        {
            var baseline = new Dictionary<string, string>
            {
                { "INI:Timeout", "200" },
                { "INI:PasswordLength", "16" }
            };
            var current = new Dictionary<string, string>
            {
                { "INI:Timeout", "500" },
                { "XML:AutoChangeOnAdd", "No" }
            };

            var drift = _workflow.CompareAttributes(baseline, current);

            Assert.Multiple(() =>
            {
                Assert.That(drift["INI:Timeout"], Does.Contain("MODIFIED"));
                Assert.That(drift["XML:AutoChangeOnAdd"], Does.Contain("ADDED"));
                Assert.That(drift["INI:PasswordLength"], Does.Contain("REMOVED"));
                Assert.That(drift.Count, Is.EqualTo(3));
            });
        }

        [Test]
        public void CompareAttributes_Should_Prioritize_IgnoreList_Over_Added()
        {
            var baseline = new Dictionary<string, string>();
            var current = new Dictionary<string, string>
            {
                { "INI:ApiVersion", "v2" }
            };

            var drift = _workflow.CompareAttributes(baseline, current);

            Assert.That(drift.ContainsKey("INI:ApiVersion"), Is.False);
        }

        [Test]
        public void CompareAttributes_Should_Detect_Drift_Across_All_Three_Scopes()
        {
            var baseline = new Dictionary<string, string>
            {
                { "INI:Timeout", "200" },
                { "XML:PlatformBaseID", "UnixSSH" },
                { "DLL:FileHash", "sha256:old_binary_hash" }
            };

            var current = new Dictionary<string, string>
            {
                { "INI:Timeout", "500" },
                { "XML:PlatformBaseID", "UnixSSH" },
                { "DLL:FileHash", "sha256:new_binary_hash" }
            };

            var drift = _workflow.CompareAttributes(baseline, current);

            Assert.Multiple(() =>
            {
                Assert.That(drift["INI:Timeout"], Does.Contain("MODIFIED"));
                Assert.That(drift["DLL:FileHash"], Does.Contain("MODIFIED"));
                Assert.That(drift.ContainsKey("XML:PlatformBaseID"), Is.False);
            });
        }
    }
}