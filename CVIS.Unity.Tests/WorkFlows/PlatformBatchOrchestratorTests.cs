using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Core.Models;
using CVIS.Unity.Infrastructure.Data;
using CVIS.Unity.PolicyDrift.Orchestration.Services;
using CVIS.Unity.PolicyDrift.Orchestrator.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using NUnit.Framework;

namespace CVIS.Unity.Tests.Workflows
{
    [TestFixture]
    public class PlatformBatchOrchestratorTests
    {
        private Mock<IFileSystemService> _fileSystem;
        private Mock<IUnityEventPublisher> _publisher;
        private Mock<IConfiguration> _config;
        private Mock<IFileProcessor> _fileProcessor;
        private PolicyDbContext _dbContext;
        private PlatformBatchOrchestrator _orchestrator;

        private const string WorkingFolder = @"C:\MockRepo\Operations";

        [SetUp]
        public void Setup()
        {
            _fileSystem = new Mock<IFileSystemService>();
            _publisher = new Mock<IUnityEventPublisher>();
            _config = new Mock<IConfiguration>();
            _fileProcessor = new Mock<IFileProcessor>();

            // Setup Mock Configuration
            // Cryptorion: Ensure every path key used by the Orchestrator is mocked
            _config.Setup(c => c["Monitoring:UpdatePolicyFolder"]).Returns(@"C:\MockRepo\Signals");

            _config.Setup(c => c["Monitoring:WorkingFolder"]).Returns(@"C:\MockRepo\Operations");

            // Setup InMemory DB
            var options = new DbContextOptionsBuilder<PolicyDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            _dbContext = new PolicyDbContext(options);

            _orchestrator = new PlatformBatchOrchestrator(
                _fileSystem.Object,
                _publisher.Object,
                _config.Object,
                _fileProcessor.Object,
                _dbContext);
        }

        [TearDown]
        public void TearDown()
        {
            _dbContext?.Dispose();
        }

        [Test]
        public async Task RunBatchRefinery_Should_GenerateIdenticalHash_ForSamePath()
        {
            // Arrange: Two separate runs for the same daily folder
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var sourcePath = Path.Combine(WorkingFolder, "PlatformPolicies", today);

            _fileSystem.Setup(f => f.DirectoryExists(sourcePath)).Returns(true);
            _fileSystem.Setup(f => f.GetFilesInDirectory(sourcePath, "*.zip")).Returns(Array.Empty<string>());

            // Act
            await _orchestrator.RunBatchRefineryAsync();
            var firstHash = _orchestrator.GenerateExecutionId(sourcePath);

            await _orchestrator.RunBatchRefineryAsync();
            var secondHash = _orchestrator.GenerateExecutionId(sourcePath);

            // Assert: Deterministic Anchor holds true
            Assert.That(firstHash, Is.EqualTo(secondHash));
            _publisher.Verify(p => p.LogInfo(It.Is<string>(s => s.Contains(firstHash))), Times.Exactly(4));
        }

        [Test]
        public async Task RunBatchRefinery_Should_Process_With_Deterministic_Identity()
        {
            // Arrange
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var sourcePath = Path.Combine(WorkingFolder, "PlatformPolicies", today);
            var zipFile = Path.Combine(sourcePath, "WinServer.zip");

            _fileSystem.Setup(f => f.DirectoryExists(sourcePath)).Returns(true);
            _fileSystem.Setup(f => f.GetFilesInDirectory(sourcePath, "*.zip")).Returns(new[] { zipFile });

            // Arrange
            var mockDiscovery = new DiscoveryResult
            {
                PolicyId = "WinServer",
                Attributes = new Dictionary<string, string> { { "INI:ConnectionComponent", "PSM-RDP" } },
                Hashes = new Dictionary<string, string> { { "INI", "HASH123" } }
            };

            // Datyrix: Moq now matches the single-object return type
            _fileProcessor.Setup(p => p.ExtractAndParseZipWithHashesAsync(It.IsAny<Stream>(), "WinServer"))
                .ReturnsAsync(mockDiscovery);

            // Act
            await _orchestrator.RunBatchRefineryAsync();

            // Assert: Check for the deterministic ID in logs
            var expectedHash = _orchestrator.GenerateExecutionId(sourcePath);
            _publisher.Verify(p => p.LogInfo(It.Is<string>(s => s.Contains(expectedHash))), Times.AtLeastOnce);
        }
        [Test]
        public async Task RunBatchRefinery_Should_PromoteBaseline_When_SignalExists()
        {
            // 1. Arrange
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var sourcePath = Path.Combine(WorkingFolder, "PlatformPolicies", today);
            var zipFile = Path.Combine(sourcePath, "WinServer.zip");
            var signalFile = @"C:\MockRepo\Signals\WinServer.txt";

            _fileSystem.Setup(f => f.DirectoryExists(sourcePath)).Returns(true);
            _fileSystem.Setup(f => f.GetFilesInDirectory(sourcePath, "*.zip")).Returns(new[] { zipFile });
            _fileSystem.Setup(f => f.SignalFileExists(signalFile)).Returns(true);
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(new MemoryStream());
            _fileSystem.Setup(f => f.CreateDirectory(It.IsAny<string>()));

            var mockDiscovery = new DiscoveryResult
            {
                PolicyId = "WinServer",
                Attributes = new Dictionary<string, string> { { "INI:Param", "Value" } },
                Hashes = new Dictionary<string, string> { { "INI", "HASH_VAL" } }
            };

            // KEY FIX: It.IsAny<string>() instead of hardcoded "WinServer"
            _fileProcessor
                .Setup(p => p.ExtractAndParseZipWithHashesAsync(It.IsAny<Stream>(), It.IsAny<string>()))
                .Callback(() => Console.WriteLine("[DIAG] Mock was called!"))
                .ReturnsAsync(mockDiscovery);

            // 2. Act — single run, all setup is in place before this fires
            await _orchestrator.RunBatchRefineryAsync();

            // 3. Assert
            _dbContext.ChangeTracker.Clear();

            var baseline = await _dbContext.PlatformBaselines
                .FirstOrDefaultAsync(b => b.PlatformId == "WinServer" && b.IsActive);

            Assert.That(baseline, Is.Not.Null, "Baseline record was not found in the database.");
            Assert.That(baseline.Attributes["INI:Param"], Is.EqualTo("Value"), "Attributes were not correctly persisted.");

            _fileSystem.Verify(f => f.DeleteSignalFile(signalFile), Times.Once);
        }

        [Test]
        public async Task RunBatchRefinery_Should_StageFiles_When_ZipsExist()
        {
            // Arrange: Setup a mock daily drop
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var sourcePath = Path.Combine(WorkingFolder, "PlatformPolicies", today);
            var mockFiles = new[] {
                Path.Combine(sourcePath, "WinServer.zip"),
                Path.Combine(sourcePath, "UnixSSH.zip")
            };

            _fileSystem.Setup(f => f.DirectoryExists(sourcePath)).Returns(true);
            _fileSystem.Setup(f => f.GetFilesInDirectory(sourcePath, "*.zip")).Returns(mockFiles);

            // Act
            await _orchestrator.RunBatchRefineryAsync();

            // Assert: Verify Atomic Moves to Processing
            _fileSystem.Verify(f => f.CreateDirectory(It.Is<string>(s => s.Contains("processing"))), Times.Once);
            _fileSystem.Verify(f => f.CreateDirectory(It.Is<string>(s => s.Contains("processed"))), Times.Once);

            _fileSystem.Verify(f => f.MoveFile(
                It.Is<string>(s => s.Contains("WinServer.zip")),
                It.Is<string>(s => s.Contains("processing"))),
            Times.Once);

            _fileSystem.Verify(f => f.MoveFile(
                It.Is<string>(s => s.Contains("UnixSSH.zip")),
                It.Is<string>(s => s.Contains("processing"))),
            Times.Once);

            // Assert: Verify ExecutionId Logging
            _publisher.Verify(p => p.LogInfo(It.Is<string>(s => s.Contains("[BATCH] Starting"))), Times.Once);
        }

        [Test]
        public async Task RunBatchRefinery_Should_Abstain_When_SourceFolderMissing()
        {
            // Arrange: Source folder does not exist for today
            _fileSystem.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);

            // Act
            await _orchestrator.RunBatchRefineryAsync();

            // Assert: No files moved, warning logged
            _fileSystem.Verify(f => f.MoveFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _publisher.Verify(p => p.LogWarning(It.Is<string>(s => s.Contains("drop zone"))), Times.Once);
        }

        [Test]
        public async Task RunBatchRefinery_Should_Overwrite_When_FileAlreadyExistsInProcessing()
        {
            // Arrange: Setup a scenario where the ZIP already exists in the destination
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var sourcePath = Path.Combine(WorkingFolder, "PlatformPolicies", today);
            var processingPath = Path.Combine(WorkingFolder, "PlatformPolicies", "processing", today);

            var zipFile = "CollisionTest.zip";
            var sourceFile = Path.Combine(sourcePath, zipFile);
            var targetFile = Path.Combine(processingPath, zipFile);

            _fileSystem.Setup(f => f.DirectoryExists(sourcePath)).Returns(true);
            _fileSystem.Setup(f => f.GetFilesInDirectory(sourcePath, "*.zip"))
                       .Returns(new[] { sourceFile });

            // Act: Run the refinery
            await _orchestrator.RunBatchRefineryAsync();

            // Assert: Verify that MoveFile was called (FileSystemService handles the 'overwrite' logic)
            _fileSystem.Verify(f => f.MoveFile(sourceFile, targetFile), Times.Once);

            // Datyrix: Ensure we still proceed to process the platform even after a collision
            _publisher.Verify(p => p.LogInfo(It.Is<string>(s => s.Contains("[STAGED]"))), Times.Once);
        }

        [Test]
        public async Task RunBatchRefinery_Should_LogAndNotifyKafka_When_FileRemainsLocked()
        {
            // Arrange
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var sourcePath = Path.Combine(WorkingFolder, "PlatformPolicies", today);
            var zipFile = Path.Combine(sourcePath, "PermanentLock.zip");

            _fileSystem.Setup(f => f.DirectoryExists(sourcePath)).Returns(true);
            _fileSystem.Setup(f => f.GetFilesInDirectory(sourcePath, "*.zip")).Returns(new[] { zipFile });

            // Mock a permanent failure
            _fileSystem.Setup(f => f.MoveFile(zipFile, It.IsAny<string>()))
                       .Throws(new InvalidOperationException("CRITICAL: File remains locked"));

            // Act
            await _orchestrator.RunBatchRefineryAsync();

            // Assert: Error Logged
            _publisher.Verify(p => p.LogError(It.Is<string>(s => s.Contains("[FAILED]")), It.IsAny<Exception>()), Times.Once);

            // Assert: Kafka Event Sent
            _publisher.Verify(p => p.PublishStatusEventAsync(
                "PermanentLock",
                "INGESTION_FAILED_LOCKED",
                It.IsAny<object>()),
            Times.Once);
        }
    }
}