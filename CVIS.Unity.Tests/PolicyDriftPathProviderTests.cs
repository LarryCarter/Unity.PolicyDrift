using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Infrastructure.Monitoring;
using Microsoft.DotNet.Scaffolding.Shared;
using Microsoft.Extensions.Configuration;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Tests.Monitoring
{
    [TestFixture]
    public class PolicyDriftPathProviderTests
    {
        // ─────────────────────────────────────────────────────────
        //  Test harness — shared fakes & builder
        // ─────────────────────────────────────────────────────────

        private Mock<IFileSystem> _fileSystem;
        private Mock<IUnityEventPublisher> _publisher;

        private const string TestBaselineFolder = @"C:\Baselines\Platform";
        private const string TestEvalRoot = @"C:\Eval\Policies";

        [SetUp]
        public void SetUp()
        {
            _fileSystem = new Mock<IFileSystem>();
            _publisher = new Mock<IUnityEventPublisher>();
        }

        /// <summary>
        /// Builds a provider with the given config dictionary.
        /// Pass null values or omit keys to simulate missing config.
        /// </summary>
        private PolicyDriftPathProvider CreateProvider(Dictionary<string, string?>? configValues = null)
        {
            var defaults = new Dictionary<string, string?>
            {
                ["Monitoring:PolicyBaselineFolder"] = TestBaselineFolder,
                ["Monitoring:PolicyEvalFolder"] = TestEvalRoot
            };

            if (configValues != null)
            {
                foreach (var kvp in configValues)
                    defaults[kvp.Key] = kvp.Value;
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(defaults)
                .Build();

            return new PolicyDriftPathProvider(configuration, _fileSystem.Object, _publisher.Object);
        }

        /// <summary>
        /// Helper: compute the expected execution ID the same way the provider does.
        /// </summary>
        private static string ExpectedExecutionId(string path)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(path.ToLowerInvariant());
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }

        // ─────────────────────────────────────────────────────────
        //  Config Validation — Missing Keys → Hard Stop + Kafka
        // ─────────────────────────────────────────────────────────

        [Test]
        public async Task BuildDriftContext_MissingBaselineFolder_ReturnsFailure()
        {
            var provider = CreateProvider(new Dictionary<string, string?>
            {
                ["Monitoring:PolicyBaselineFolder"] = null
            });

            var result = await provider.BuildDriftContextAsync();

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Context, Is.Null);
            Assert.That(result.Error, Does.Contain("PolicyBaselineFolder"));
        }

        [Test]
        public async Task BuildDriftContext_MissingBaselineFolder_LogsErrorAndFiresKafka()
        {
            var provider = CreateProvider(new Dictionary<string, string?>
            {
                ["Monitoring:PolicyBaselineFolder"] = null
            });

            await provider.BuildDriftContextAsync();

            _publisher.Verify(
                p => p.LogError(It.Is<string>(s => s.Contains("CRITICAL")), null),
                Times.Once);

            _publisher.Verify(
                p => p.PublishKafkaDriftAsync(
                    It.Is<string>(id => id.Contains("CONFIG:")),
                    It.IsAny<Dictionary<string, string>>(),
                    It.IsAny<Dictionary<string, string>>()),
                Times.Once);
        }

        [Test]
        public async Task BuildDriftContext_MissingEvalRoot_ReturnsFailure()
        {
            var provider = CreateProvider(new Dictionary<string, string?>
            {
                ["Monitoring:PolicyEvalFolder"] = null
            });

            var result = await provider.BuildDriftContextAsync();

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Error, Does.Contain("PolicyEvalFolder"));
        }

        [Test]
        public async Task BuildDriftContext_MissingEvalRoot_LogsErrorAndFiresKafka()
        {
            var provider = CreateProvider(new Dictionary<string, string?>
            {
                ["Monitoring:PolicyEvalFolder"] = null
            });

            await provider.BuildDriftContextAsync();

            _publisher.Verify(
                p => p.LogError(It.Is<string>(s => s.Contains("CRITICAL")), null),
                Times.Once);

            _publisher.Verify(
                p => p.PublishKafkaDriftAsync(
                    It.Is<string>(id => id == "CONFIG:Monitoring:PolicyEvalFolder"),
                    It.IsAny<Dictionary<string, string>>(),
                    It.IsAny<Dictionary<string, string>>()),
                Times.Once);
        }

        [TestCase("")]
        [TestCase("   ")]
        public async Task BuildDriftContext_EmptyOrWhitespaceBaselineFolder_TreatedAsMissing(string value)
        {
            var provider = CreateProvider(new Dictionary<string, string?>
            {
                ["Monitoring:PolicyBaselineFolder"] = value
            });

            var result = await provider.BuildDriftContextAsync();

            Assert.That(result.IsValid, Is.False);
        }

        // ─────────────────────────────────────────────────────────
        //  Root Folder Assurance — Auto-Create + Log Warning
        // ─────────────────────────────────────────────────────────

        [Test]
        public async Task BuildDriftContext_RootFoldersDoNotExist_CreatesThemWithWarning()
        {
            _fileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(false);

            var provider = CreateProvider();
            var result = await provider.BuildDriftContextAsync();

            Assert.That(result.IsValid, Is.True);

            _fileSystem.Verify(fs => fs.CreateDirectory(TestBaselineFolder), Times.Once);
            _fileSystem.Verify(fs => fs.CreateDirectory(TestEvalRoot), Times.Once);

            _publisher.Verify(
                p => p.LogWarning(It.Is<string>(s => s.Contains(TestBaselineFolder))),
                Times.Once);
            _publisher.Verify(
                p => p.LogWarning(It.Is<string>(s => s.Contains(TestEvalRoot))),
                Times.Once);
        }

        [Test]
        public async Task BuildDriftContext_RootFoldersAlreadyExist_NoCreationNoWarning()
        {
            _fileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);

            var provider = CreateProvider();
            await provider.BuildDriftContextAsync();

            _fileSystem.Verify(fs => fs.CreateDirectory(TestBaselineFolder), Times.Never);
            _fileSystem.Verify(fs => fs.CreateDirectory(TestEvalRoot), Times.Never);

            _publisher.Verify(
                p => p.LogWarning(It.IsAny<string>()),
                Times.Never);
        }

        // ─────────────────────────────────────────────────────────
        //  Happy Path — Full Context Built Correctly
        // ─────────────────────────────────────────────────────────

        [Test]
        public async Task BuildDriftContext_ValidConfig_ReturnsCorrectPaths()
        {
            _fileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
            var fixedDate = new DateTime(2025, 7, 15, 10, 30, 0, DateTimeKind.Utc);

            var provider = CreateProvider();
            var result = await provider.BuildDriftContextAsync(fixedDate);

            Assert.That(result.IsValid, Is.True);
            var ctx = result.Context!;

            Assert.That(ctx.BaselineFolder, Is.EqualTo(TestBaselineFolder));
            Assert.That(ctx.EvalRoot, Is.EqualTo(TestEvalRoot));
            Assert.That(ctx.DateStamp, Is.EqualTo("2025-07-15"));
            Assert.That(ctx.SourcePath, Is.EqualTo(Path.Combine(TestEvalRoot, "2025-07-15")));
            Assert.That(ctx.ProcessingPath, Is.EqualTo(Path.Combine(TestEvalRoot, "2025-07-15", "Processing")));
            Assert.That(ctx.ProcessedPath, Is.EqualTo(Path.Combine(TestEvalRoot, "2025-07-15", "Processed")));
        }

        [Test]
        public async Task BuildDriftContext_ValidConfig_LogsInfoWithExecutionId()
        {
            _fileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);

            var provider = CreateProvider();
            await provider.BuildDriftContextAsync(new DateTime(2025, 7, 15, 0, 0, 0, DateTimeKind.Utc));

            _publisher.Verify(
                p => p.LogInfo(It.Is<string>(s =>
                    s.Contains("[PolicyDriftPath]") &&
                    s.Contains("Context built") &&
                    s.Contains("ExecutionId"))),
                Times.Once);
        }

        // ─────────────────────────────────────────────────────────
        //  Execution ID — Determinism & Group Anchor
        // ─────────────────────────────────────────────────────────

        [Test]
        public async Task ExecutionId_SameDateSameConfig_ProducesSameHash()
        {
            _fileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
            var fixedDate = new DateTime(2025, 7, 15, 0, 0, 0, DateTimeKind.Utc);

            var provider = CreateProvider();
            var result1 = await provider.BuildDriftContextAsync(fixedDate);
            var result2 = await provider.BuildDriftContextAsync(fixedDate);

            Assert.That(result1.Context!.ExecutionId, Is.EqualTo(result2.Context!.ExecutionId));
        }

        [Test]
        public async Task ExecutionId_DifferentDates_ProduceDifferentHashes()
        {
            _fileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);

            var provider = CreateProvider();
            var result1 = await provider.BuildDriftContextAsync(new DateTime(2025, 7, 15, 0, 0, 0, DateTimeKind.Utc));
            var result2 = await provider.BuildDriftContextAsync(new DateTime(2025, 7, 16, 0, 0, 0, DateTimeKind.Utc));

            Assert.That(result1.Context!.ExecutionId, Is.Not.EqualTo(result2.Context!.ExecutionId));
        }

        [Test]
        public async Task ExecutionId_MatchesExpectedSha256()
        {
            _fileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
            var fixedDate = new DateTime(2025, 7, 15, 0, 0, 0, DateTimeKind.Utc);
            var expectedPath = Path.Combine(TestEvalRoot, "2025-07-15");

            var provider = CreateProvider();
            var result = await provider.BuildDriftContextAsync(fixedDate);

            Assert.That(result.Context!.ExecutionId, Is.EqualTo(ExpectedExecutionId(expectedPath)));
        }

        [Test]
        public void GenerateExecutionId_IsCaseInsensitive()
        {
            var provider = CreateProvider();

            var id1 = provider.GenerateExecutionId(@"C:\Eval\Policies\2025-07-15");
            var id2 = provider.GenerateExecutionId(@"c:\eval\policies\2025-07-15");

            Assert.That(id1, Is.EqualTo(id2));
        }

        // ─────────────────────────────────────────────────────────
        //  Staging Directories — Explicit Opt-In
        // ─────────────────────────────────────────────────────────

        [Test]
        public async Task EnsureStagingDirectories_CreatesProcessingAndProcessed()
        {
            _fileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
            var fixedDate = new DateTime(2025, 7, 15, 0, 0, 0, DateTimeKind.Utc);

            var provider = CreateProvider();
            var result = await provider.BuildDriftContextAsync(fixedDate);
            var ctx = result.Context!;

            provider.EnsureStagingDirectories(ctx);

            _fileSystem.Verify(fs => fs.CreateDirectory(ctx.ProcessingPath), Times.Once);
            _fileSystem.Verify(fs => fs.CreateDirectory(ctx.ProcessedPath), Times.Once);
        }

        [Test]
        public async Task EnsureStagingDirectories_LogsEachDirectoryCreation()
        {
            _fileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
            var fixedDate = new DateTime(2025, 7, 15, 0, 0, 0, DateTimeKind.Utc);

            var provider = CreateProvider();
            var result = await provider.BuildDriftContextAsync(fixedDate);

            provider.EnsureStagingDirectories(result.Context!);

            _publisher.Verify(
                p => p.LogInfo(It.Is<string>(s => s.Contains("Staging ready") && s.Contains("Processing"))),
                Times.Once);
            _publisher.Verify(
                p => p.LogInfo(It.Is<string>(s => s.Contains("Staging ready") && s.Contains("Processed"))),
                Times.Once);
        }

        [Test]
        public async Task BuildDriftContext_DoesNotCreateStagingDirectories()
        {
            _fileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
            var fixedDate = new DateTime(2025, 7, 15, 0, 0, 0, DateTimeKind.Utc);

            var provider = CreateProvider();
            var result = await provider.BuildDriftContextAsync(fixedDate);
            var ctx = result.Context!;

            _fileSystem.Verify(fs => fs.CreateDirectory(ctx.ProcessingPath), Times.Never);
            _fileSystem.Verify(fs => fs.CreateDirectory(ctx.ProcessedPath), Times.Never);
        }

        // ─────────────────────────────────────────────────────────
        //  Backfill / Reprocessing — Date Override
        // ─────────────────────────────────────────────────────────

        [Test]
        public async Task BuildDriftContext_HistoricalDate_UsesProvidedDateNotUtcNow()
        {
            _fileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
            var historicalDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var provider = CreateProvider();
            var result = await provider.BuildDriftContextAsync(historicalDate);

            Assert.That(result.Context!.DateStamp, Is.EqualTo("2024-01-01"));
            Assert.That(result.Context.SourcePath, Does.Contain("2024-01-01"));
        }

        // ─────────────────────────────────────────────────────────
        //  Constructor Guards
        // ─────────────────────────────────────────────────────────

        [Test]
        public void Constructor_NullConfiguration_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new PolicyDriftPathProvider(null!, _fileSystem.Object, _publisher.Object));
        }

        [Test]
        public void Constructor_NullFileSystem_Throws()
        {
            var config = new ConfigurationBuilder().Build();
            Assert.Throws<ArgumentNullException>(() =>
                new PolicyDriftPathProvider(config, null!, _publisher.Object));
        }

        [Test]
        public void Constructor_NullPublisher_Throws()
        {
            var config = new ConfigurationBuilder().Build();
            Assert.Throws<ArgumentNullException>(() =>
                new PolicyDriftPathProvider(config, _fileSystem.Object, null!));
        }

        // ─────────────────────────────────────────────────────────
        //  Kafka Alert Shape — Verify the drift event carries
        //  the right policyId and diagnostic data
        // ─────────────────────────────────────────────────────────

        [Test]
        public async Task KafkaAlert_MissingConfig_PolicyIdContainsConfigKey()
        {
            var provider = CreateProvider(new Dictionary<string, string?>
            {
                ["Monitoring:PolicyEvalFolder"] = null
            });

            await provider.BuildDriftContextAsync();

            _publisher.Verify(
                p => p.PublishKafkaDriftAsync(
                    "CONFIG:Monitoring:PolicyEvalFolder",
                    It.Is<Dictionary<string, string>>(d =>
                        d.ContainsKey("MissingConfigKey") &&
                        d.ContainsKey("ErrorDetail") &&
                        d.ContainsKey("DetectedAtUtc")),
                    It.Is<Dictionary<string, string>>(b =>
                        b.ContainsKey("ExpectedState") &&
                        b.ContainsKey("ActualState"))),
                Times.Once);
        }

        [Test]
        public async Task KafkaAlert_MissingBaselineFolder_DoesNotCheckEvalRoot()
        {
            var provider = CreateProvider(new Dictionary<string, string?>
            {
                ["Monitoring:PolicyBaselineFolder"] = null,
                ["Monitoring:PolicyEvalFolder"] = null
            });

            await provider.BuildDriftContextAsync();

            // Only one Kafka alert — the first failure wins
            _publisher.Verify(
                p => p.PublishKafkaDriftAsync(
                    It.IsAny<string>(),
                    It.IsAny<Dictionary<string, string>>(),
                    It.IsAny<Dictionary<string, string>>()),
                Times.Once);
        }
    }
}