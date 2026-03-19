using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Configuration;
using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Infrastructure.Data;
using CVIS.Unity.PolicyDrift.Orchestrator.Workflows;
using CVIS.Unity.PolicyDrift.Orchestration.Services;

namespace CVIS.Unity.Tests
{
    [TestFixture]
    public class PlatformDriftTests
    {
        private PlatformWorkflow _workflow;
        private Mock<IUnityEventPublisher> _mockPublisher;

        [SetUp]
        public void Setup()
        {
            // Mocks for dependencies not being tested
            var mockFileSystem = new Mock<IFileSystemService>();
            _mockPublisher = new Mock<IUnityEventPublisher>();
            var mockConfig = new Mock<IConfiguration>();
            var mockFileProcessor = new Mock<FileProcessor>();

            // Note: DB context is not needed for this pure logic test
            _workflow = new PlatformWorkflow(
                mockFileSystem.Object,
                _mockPublisher.Object,
                mockConfig.Object,
                mockFileProcessor.Object,
                null!);
        }

        [Test]
        public void CompareAttributes_Should_Ignore_Noise_Keys()
        {
            // Arrange: Baseline and Current differ ONLY by an ignored key
            var baseline = new Dictionary<string, string>
            {
                { "INI:Timeout", "200" },
                { "INI:ApiVersion", "v1" }
            };
            var current = new Dictionary<string, string>
            {
                { "INI:Timeout", "200" },
                { "INI:ApiVersion", "v2" } // Changed but should be ignored
            };

            // Act
            var drift = _workflow.CompareAttributes(baseline, current);

            // Assert: Drift report should be empty
            Assert.That(drift.Count, Is.EqualTo(0), "Drift should be empty when only ignored keys change.");
        }

        [Test]
        public void CompareAttributes_Should_Detect_All_Drift_Types()
        {
            // Arrange
            var baseline = new Dictionary<string, string>
            {
                { "INI:Timeout", "200" },
                { "INI:PasswordLength", "16" }
            };
            var current = new Dictionary<string, string>
            {
                { "INI:Timeout", "500" },        // MODIFIED
                { "XML:AutoChangeOnAdd", "No" }  // ADDED
                // PasswordLength is REMOVED
            };

            // Act
            var drift = _workflow.CompareAttributes(baseline, current);

            // Assert
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
            // Arrange: A new key appears that is in the ignore list
            var baseline = new Dictionary<string, string>();
            var current = new Dictionary<string, string>
            {
                { "INI:ApiVersion", "v2" }
            };

            // Act
            var drift = _workflow.CompareAttributes(baseline, current);

            // Assert
            Assert.That(drift.ContainsKey("INI:ApiVersion"), Is.False);
        }

        [Test]
        public void CompareAttributes_Should_Detect_Drift_Across_All_Three_Scopes()
        {
            // Arrange: A baseline representing a full CyberArk Platform ZIP
            var baseline = new Dictionary<string, string>
            {
                { "INI:Timeout", "200" },                        // INI Setting
                { "XML:PlatformBaseID", "UnixSSH" },             // XML Metadata
                { "DLL:FileHash", "sha256:old_binary_hash" }      // Binary Fingerprint
            };

            // Act: Current state where all three have drifted
            var current = new Dictionary<string, string>
            {
                { "INI:Timeout", "500" },                        // Modified
                { "XML:PlatformBaseID", "UnixSSH" },             // No Change
                // XML:AutoChangeOnAdd is MISSING                // Removed
                { "DLL:FileHash", "sha256:new_binary_hash" }      // Modified (Critical!)
            };

            var drift = _workflow.CompareAttributes(baseline, current);

            // Assert: Verify the Arsenal caught all three
            Assert.Multiple(() =>
            {
                // 1. Check INI Drift
                Assert.That(drift["INI:Timeout"], Does.Contain("MODIFIED"));

                // 2. Check Binary Tampering
                Assert.That(drift["DLL:FileHash"], Does.Contain("MODIFIED"));

                // 3. Check for the absence of XML drift where none occurred
                Assert.That(drift.ContainsKey("XML:PlatformBaseID"), Is.False);
            });
        }
    }
}