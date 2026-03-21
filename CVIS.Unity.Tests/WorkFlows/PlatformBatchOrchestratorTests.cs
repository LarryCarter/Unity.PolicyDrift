using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Core.Models;
using CVIS.Unity.Core.Monitoring;
using CVIS.Unity.Infrastructure.Data;
using CVIS.Unity.PolicyDrift.Orchestrator.Workflows;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CVIS.Unity.Tests.Workflows
{
    [TestFixture]
    public class PlatformBatchOrchestratorTests
    {
        private Mock<IFileSystemService> _fileSystem;
        private Mock<IUnityEventPublisher> _publisher;
        private Mock<IPolicyDriftPathProvider> _driftPath;
        private Mock<IFileProcessor> _fileProcessor;
        private Mock<ISignalFileService> _signalFiles;
        private PolicyDbContext _dbContext;
        private PlatformBatchOrchestrator _orchestrator;

        // Fixed paths — no more manual Path.Combine in every test
        private const string TestBaselineFolder = @"C:\Baselines\Platform";
        private const string TestEvalRoot = @"C:\Eval\Policies";
        private const string TestDateStamp = "2025-07-15";

        private static readonly string TestSourcePath = Path.Combine(TestEvalRoot, TestDateStamp);
        private static readonly string TestProcessingPath = Path.Combine(TestSourcePath, "Processing");
        private static readonly string TestProcessedPath = Path.Combine(TestSourcePath, "Processed");
        private const string TestExecutionId = "ABC123HASH";

        [SetUp]
        public void Setup()
        {
            _fileSystem = new Mock<IFileSystemService>();
            _publisher = new Mock<IUnityEventPublisher>();
            _driftPath = new Mock<IPolicyDriftPathProvider>();
            _fileProcessor = new Mock<IFileProcessor>();
            _signalFiles = new Mock<ISignalFileService>();

            var options = new DbContextOptionsBuilder<PolicyDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            _dbContext = new PolicyDbContext(options);

            _orchestrator = new PlatformBatchOrchestrator(
                _fileSystem.Object,
                _publisher.Object,
                _driftPath.Object,
                _fileProcessor.Object,
                _signalFiles.Object,
                _dbContext);
        }

        [TearDown]
        public void TearDown()
        {
            _dbContext?.Dispose();
        }

        // ─────────────────────────────────────────────────────────
        //  Helper: Setup a valid PolicyDriftContext
        // ─────────────────────────────────────────────────────────

        private PolicyDriftContext BuildTestContext()
        {
            return new PolicyDriftContext(
                TestBaselineFolder, TestEvalRoot, TestSourcePath,
                TestProcessingPath, TestProcessedPath, TestExecutionId, TestDateStamp);
        }

        private void SetupValidContext()
        {
            var ctx = BuildTestContext();
            var successResult = PolicyDriftContextResult.Success(ctx);

            _driftPath.Setup(d => d.BuildDriftContextAsync())
                .ReturnsAsync(successResult);
            _driftPath.Setup(d => d.BuildDriftContextAsync(It.IsAny<DateTime>()))
                .ReturnsAsync(successResult);
        }

        private void SetupFailedContext(string error)
        {
            var failResult = PolicyDriftContextResult.Failure(error);

            _driftPath.Setup(d => d.BuildDriftContextAsync())
                .ReturnsAsync(failResult);
        }

        // ─────────────────────────────────────────────────────────
        //  Config Failure — Provider Already Handles Kafka
        // ─────────────────────────────────────────────────────────

        [Test]
        public async Task RunBatchRefinery_ConfigMissing_ReturnsWithoutProcessing()
        {
            SetupFailedContext("CRITICAL: Config key missing");

            await _orchestrator.RunBatchRefineryAsync();

            _fileSystem.Verify(f => f.MoveFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _fileSystem.Verify(f => f.GetFilesInDirectory(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        // ─────────────────────────────────────────────────────────
        //  Source Gate — No Folder, No Work
        // ─────────────────────────────────────────────────────────

        [Test]
        public async Task RunBatchRefinery_Should_Abstain_When_SourceFolderMissing()
        {
            SetupValidContext();
            _fileSystem.Setup(f => f.DirectoryExists(TestSourcePath)).Returns(false);

            await _orchestrator.RunBatchRefineryAsync();

            _fileSystem.Verify(f => f.MoveFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _publisher.Verify(
                p => p.LogWarning(It.Is<string>(s => s.Contains("Standing down"))),
                Times.Once);
        }

        // ─────────────────────────────────────────────────────────
        //  Staging — Dirs Created When ZIPs Exist
        // ─────────────────────────────────────────────────────────

        [Test]
        public async Task RunBatchRefinery_Should_StageFiles_When_ZipsExist()
        {
            SetupValidContext();
            var mockFiles = new[]
            {
                Path.Combine(TestSourcePath, "WinServer.zip"),
                Path.Combine(TestSourcePath, "UnixSSH.zip")
            };

            _fileSystem.Setup(f => f.DirectoryExists(TestSourcePath)).Returns(true);
            _fileSystem.Setup(f => f.GetFilesInDirectory(TestSourcePath, "*.zip")).Returns(mockFiles);

            await _orchestrator.RunBatchRefineryAsync();

            // Staging directories created via provider
            _driftPath.Verify(d => d.EnsureStagingDirectories(It.IsAny<PolicyDriftContext>()), Times.Once);

            // Both ZIPs moved to Processing
            _fileSystem.Verify(f => f.MoveFile(
                It.Is<string>(s => s.Contains("WinServer.zip")),
                It.Is<string>(s => s.Contains("Processing"))),
                Times.Once);

            _fileSystem.Verify(f => f.MoveFile(
                It.Is<string>(s => s.Contains("UnixSSH.zip")),
                It.Is<string>(s => s.Contains("Processing"))),
                Times.Once);

            _publisher.Verify(
                p => p.LogInfo(It.Is<string>(s => s.Contains("[BATCH] Starting"))),
                Times.Once);
        }

        // ─────────────────────────────────────────────────────────
        //  Execution ID Determinism — Now From Context
        // ─────────────────────────────────────────────────────────

        [Test]
        public async Task RunBatchRefinery_Should_LogExecutionId_FromContext()
        {
            SetupValidContext();

            _fileSystem.Setup(f => f.DirectoryExists(TestSourcePath)).Returns(true);
            _fileSystem.Setup(f => f.GetFilesInDirectory(TestSourcePath, "*.zip"))
                .Returns(Array.Empty<string>());

            await _orchestrator.RunBatchRefineryAsync();

            _publisher.Verify(
                p => p.LogInfo(It.Is<string>(s => s.Contains(TestExecutionId))),
                Times.AtLeastOnce);
        }

        // ─────────────────────────────────────────────────────────
        //  Signal File — Baseline Promotion
        // ─────────────────────────────────────────────────────────

        [Test]
        public async Task RunBatchRefinery_Should_PromoteBaseline_When_SignalExists()
        {
            SetupValidContext();
            var zipFile = Path.Combine(TestSourcePath, "WinServer.zip");

            _fileSystem.Setup(f => f.DirectoryExists(TestSourcePath)).Returns(true);
            _fileSystem.Setup(f => f.GetFilesInDirectory(TestSourcePath, "*.zip"))
                .Returns(new[] { zipFile });
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(new MemoryStream());

            _signalFiles.Setup(s => s.Exists(TestBaselineFolder, "WinServer")).Returns(true);
            _signalFiles.Setup(s => s.ReadTicketId(It.IsAny<string>(), It.Is<string>(p => p == "WinServer")))
                .Returns("CHG0012345");
            _signalFiles.Setup(s => s.TryDelete(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

            var mockDiscovery = new DiscoveryResult
            {
                PolicyId = "WinServer",
                Attributes = new Dictionary<string, string> { { "INI:Param", "Value" } },
                Hashes = new Dictionary<string, string> { { "INI", "HASH_VAL" } }
            };

            _fileProcessor
                .Setup(p => p.ExtractAndParseZipWithHashesAsync(It.IsAny<Stream>(), It.IsAny<string>()))
                .ReturnsAsync(mockDiscovery);

            await _orchestrator.RunBatchRefineryAsync();

            _dbContext.ChangeTracker.Clear();

            var baseline = await _dbContext.PlatformBaselines
                .FirstOrDefaultAsync(b => b.PlatformId == "WinServer" && b.IsActive);

            Assert.That(baseline, Is.Not.Null, "Baseline record was not found in the database.");
            Assert.That(baseline!.Attributes["INI:Param"], Is.EqualTo("Value"));
            Assert.That(baseline.LastSNOWTicket, Is.EqualTo("CHG0012345"));
            _publisher.Verify(
    p => p.PublishStatusEventAsync(
        "WinServer", "BASELINE_PROMOTION_FAILED", It.IsAny<object>()),
    Times.Never);

            _signalFiles.Verify(s => s.ReadTicketId(It.IsAny<string>(), It.Is<string>(p => p == "WinServer")), Times.Once);
            _signalFiles.Verify(s => s.TryDelete(It.IsAny<string>(), It.Is<string>(p => p == "WinServer")), Times.Once);
        }

        [Test]
        public async Task RunBatchRefinery_Should_PromoteBaseline_WithNullTicket_WhenFileEmpty()
        {
            SetupValidContext();
            var zipFile = Path.Combine(TestSourcePath, "WinServer.zip");

            _fileSystem.Setup(f => f.DirectoryExists(TestSourcePath)).Returns(true);
            _fileSystem.Setup(f => f.GetFilesInDirectory(TestSourcePath, "*.zip"))
                .Returns(new[] { zipFile });
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(new MemoryStream());

            _signalFiles.Setup(s => s.Exists(TestBaselineFolder, "WinServer")).Returns(true);
            _signalFiles.Setup(s => s.ReadTicketId(TestBaselineFolder, "WinServer"))
                .Returns((string?)null);
            _signalFiles.Setup(s => s.TryDelete(TestBaselineFolder, "WinServer")).Returns(true);

            var mockDiscovery = new DiscoveryResult
            {
                PolicyId = "WinServer",
                Attributes = new Dictionary<string, string> { { "INI:Param", "Value" } },
                Hashes = new Dictionary<string, string> { { "INI", "HASH_VAL" } }
            };

            _fileProcessor
                .Setup(p => p.ExtractAndParseZipWithHashesAsync(It.IsAny<Stream>(), It.IsAny<string>()))
                .ReturnsAsync(mockDiscovery);

            await _orchestrator.RunBatchRefineryAsync();

            _dbContext.ChangeTracker.Clear();

            var baseline = await _dbContext.PlatformBaselines
                .FirstOrDefaultAsync(b => b.PlatformId == "WinServer" && b.IsActive);

            Assert.That(baseline, Is.Not.Null);
            Assert.That(baseline!.LastSNOWTicket, Is.Null);
        }

        [Test]
        public async Task RunBatchRefinery_Should_NotPromote_When_NoSignalFile()
        {
            SetupValidContext();
            var zipFile = Path.Combine(TestSourcePath, "WinServer.zip");

            _fileSystem.Setup(f => f.DirectoryExists(TestSourcePath)).Returns(true);
            _fileSystem.Setup(f => f.GetFilesInDirectory(TestSourcePath, "*.zip"))
                .Returns(new[] { zipFile });
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(new MemoryStream());

            _signalFiles.Setup(s => s.Exists(TestBaselineFolder, "WinServer")).Returns(false);

            var mockDiscovery = new DiscoveryResult
            {
                PolicyId = "WinServer",
                Attributes = new Dictionary<string, string> { { "INI:Param", "Value" } },
                Hashes = new Dictionary<string, string> { { "INI", "HASH_VAL" } }
            };

            _fileProcessor
                .Setup(p => p.ExtractAndParseZipWithHashesAsync(It.IsAny<Stream>(), It.IsAny<string>()))
                .ReturnsAsync(mockDiscovery);

            await _orchestrator.RunBatchRefineryAsync();

            _signalFiles.Verify(s => s.ReadTicketId(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _signalFiles.Verify(s => s.TryDelete(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        // ─────────────────────────────────────────────────────────
        //  Error Handling — File Lock
        // ─────────────────────────────────────────────────────────

        [Test]
        public async Task RunBatchRefinery_Should_ContinueProcessing_When_SingleFileFailsToStage()
        {
            SetupValidContext();
            var zipFile1 = Path.Combine(TestSourcePath, "LockedFile.zip");
            var zipFile2 = Path.Combine(TestSourcePath, "GoodFile.zip");

            _fileSystem.Setup(f => f.DirectoryExists(TestSourcePath)).Returns(true);
            _fileSystem.Setup(f => f.GetFilesInDirectory(TestSourcePath, "*.zip"))
                .Returns(new[] { zipFile1, zipFile2 });

            _fileSystem.Setup(f => f.MoveFile(
                    It.Is<string>(s => s.Contains("LockedFile")), It.IsAny<string>()))
                .Throws(new InvalidOperationException("CRITICAL: File remains locked"));

            _fileSystem.Setup(f => f.MoveFile(
                    It.Is<string>(s => s.Contains("GoodFile")), It.IsAny<string>()));
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(new MemoryStream());

            var mockDiscovery = new DiscoveryResult
            {
                PolicyId = "GoodFile",
                Attributes = new Dictionary<string, string> { { "INI:Key", "Val" } },
                Hashes = new Dictionary<string, string> { { "INI", "H1" } }
            };

            _fileProcessor
                .Setup(p => p.ExtractAndParseZipWithHashesAsync(It.IsAny<Stream>(), It.IsAny<string>()))
                .ReturnsAsync(mockDiscovery);

            await _orchestrator.RunBatchRefineryAsync();

            // First file errored, second still processed
            _publisher.Verify(
                p => p.LogError(It.Is<string>(s => s.Contains("LockedFile")), It.IsAny<Exception>()),
                Times.Once);

            // GoodFile should have been staged (moved to Processing)
            _fileSystem.Verify(f => f.MoveFile(
                It.Is<string>(s => s.Contains("GoodFile") && s.Contains(TestSourcePath)),
                It.Is<string>(s => s.Contains("Processing"))),
                Times.Once);
        }

        // ─────────────────────────────────────────────────────────
        //  Cleanup — Processing Directory Removed
        // ─────────────────────────────────────────────────────────

        [Test]
        public async Task RunBatchRefinery_Should_CleanupProcessingFolder_AfterCompletion()
        {
            SetupValidContext();

            _fileSystem.Setup(f => f.DirectoryExists(TestSourcePath)).Returns(true);
            _fileSystem.Setup(f => f.DirectoryExists(TestProcessingPath)).Returns(true);
            _fileSystem.Setup(f => f.GetFilesInDirectory(TestSourcePath, "*.zip"))
                .Returns(Array.Empty<string>());

            await _orchestrator.RunBatchRefineryAsync();

            _fileSystem.Verify(
                f => f.DeleteDirectory(TestProcessingPath, true),
                Times.Once);

            _publisher.Verify(
                p => p.LogInfo(It.Is<string>(s => s.Contains("[CLEANUP]"))),
                Times.Once);
        }

        [Test]
        public async Task RunBatchRefinery_Should_SkipCleanup_When_ProcessingFolderMissing()
        {
            SetupValidContext();

            _fileSystem.Setup(f => f.DirectoryExists(TestSourcePath)).Returns(true);
            _fileSystem.Setup(f => f.DirectoryExists(TestProcessingPath)).Returns(false);
            _fileSystem.Setup(f => f.GetFilesInDirectory(TestSourcePath, "*.zip"))
                .Returns(Array.Empty<string>());

            await _orchestrator.RunBatchRefineryAsync();

            _fileSystem.Verify(
                f => f.DeleteDirectory(It.IsAny<string>(), It.IsAny<bool>()),
                Times.Never);
        }

        // ─────────────────────────────────────────────────────────
        //  Archive — Files Move From Processing to Processed
        // ─────────────────────────────────────────────────────────

        [Test]
        public async Task RunBatchRefinery_Should_ArchiveToProcessed_AfterSuccess()
        {
            SetupValidContext();
            var zipFile = Path.Combine(TestSourcePath, "TestPolicy.zip");

            _fileSystem.Setup(f => f.DirectoryExists(TestSourcePath)).Returns(true);
            _fileSystem.Setup(f => f.DirectoryExists(TestProcessingPath)).Returns(true);
            _fileSystem.Setup(f => f.GetFilesInDirectory(TestSourcePath, "*.zip"))
                .Returns(new[] { zipFile });
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(new MemoryStream());
            _signalFiles.Setup(s => s.Exists(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

            var mockDiscovery = new DiscoveryResult
            {
                PolicyId = "TestPolicy",
                Attributes = new Dictionary<string, string> { { "INI:Key", "Val" } },
                Hashes = new Dictionary<string, string> { { "INI", "H1" } }
            };

            _fileProcessor
                .Setup(p => p.ExtractAndParseZipWithHashesAsync(It.IsAny<Stream>(), It.IsAny<string>()))
                .ReturnsAsync(mockDiscovery);

            await _orchestrator.RunBatchRefineryAsync();

            // File should move from Processing → Processed
            _fileSystem.Verify(f => f.MoveFile(
                It.Is<string>(s => s.Contains("Processing") && s.Contains("TestPolicy.zip")),
                It.Is<string>(s => s.Contains("Processed") && s.Contains("TestPolicy.zip"))),
                Times.Once);
        }
    }

}