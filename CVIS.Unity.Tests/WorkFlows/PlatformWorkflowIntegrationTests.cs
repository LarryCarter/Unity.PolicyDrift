using CVIS.Unity.Core.Entities;
using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Core.Models;
using CVIS.Unity.Core.Monitoring;
using CVIS.Unity.Infrastructure.Data;
using CVIS.Unity.Infrastructure.Services;
using CVIS.Unity.PolicyDrift.Orchestration.Services;
using CVIS.Unity.PolicyDrift.Orchestrator.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using NUnit.Framework;
using System.IO.Compression;

namespace CVIS.Unity.Tests.WorkFlows
{
    [TestFixture]
    public class PlatformWorkflowIntegrationTests
    {
        private Mock<IFileSystemService> _fileSystem;
        private Mock<IUnityEventPublisher> _publisher;
        private Mock<IPolicyDriftPathProvider> _driftPath;
        private Mock<ISignalFileService> _signalFiles;
        private Mock<FileProcessor> _fileProcessor;
        private PolicyDbContext _db;
        private IConfiguration _config;
        private PlatformWorkflow _workflow;

        private const string BaselineFolder = @"C:\Baselines";
        private const string EvalRoot = @"C:\Eval";
        private const string DateStamp = "03-21-2026";
        private const string ExecId = "EXEC1234ABCD";

        private static readonly string SourcePath = Path.Combine(EvalRoot, DateStamp);
        private static readonly string ProcessingPath = Path.Combine(SourcePath, "Processing");
        private static readonly string ProcessedPath = Path.Combine(SourcePath, "Processed");

        [SetUp]
        public void SetUp()
        {
            _fileSystem = new Mock<IFileSystemService>();
            _publisher = new Mock<IUnityEventPublisher>();
            _driftPath = new Mock<IPolicyDriftPathProvider>();
            _signalFiles = new Mock<ISignalFileService>();
            _fileProcessor = new Mock<FileProcessor>();

            var options = new DbContextOptionsBuilder<PolicyDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            _db = new PolicyDbContext(options);

            _config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>()).Build();

            BuildWorkflow(_config);
        }

        private void BuildWorkflow(IConfiguration config)
        {
            _workflow = new PlatformWorkflow(
                _fileSystem.Object,
                _publisher.Object,
                _driftPath.Object,
                config,
                _fileProcessor.Object,
                _signalFiles.Object,
                new DriftComparisonService(config, _publisher.Object),
                _db);
        }

        [TearDown]
        public void TearDown() => _db?.Dispose();

        // ═══════════════════════════════════════════════════════
        //  Helpers
        // ═══════════════════════════════════════════════════════

        private void ValidContext()
        {
            var ctx = new PolicyDriftContext(
                BaselineFolder, EvalRoot, SourcePath, ProcessingPath, ProcessedPath, ExecId, DateStamp);
            _driftPath.Setup(d => d.BuildDriftContextAsync())
                .ReturnsAsync(PolicyDriftContextResult.Success(ctx));
            _fileSystem.Setup(f => f.DirectoryExists(SourcePath)).Returns(true);
        }

        private void Zips(params string[] ids)
        {
            _fileSystem.Setup(f => f.GetFilesInDirectory(SourcePath, "*.zip"))
                .Returns(ids.Select(id => Path.Combine(SourcePath, $"{id}.zip")).ToArray());
        }

        private MemoryStream GoodZip(string policyId)
        {
            var ms = new MemoryStream();
            using (var a = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                a.CreateEntry($"policy-{policyId}.xml");
                a.CreateEntry($"policy-{policyId}.ini");
            }
            ms.Position = 0;
            return ms;
        }

        private MemoryStream BadZip()
        {
            var ms = new MemoryStream();
            using (var a = new ZipArchive(ms, ZipArchiveMode.Create, true))
                a.CreateEntry("garbage.txt");
            ms.Position = 0;
            return ms;
        }

        private void MockParse(string id, Dictionary<string, string> attrs,
            Dictionary<string, string>? hashes = null)
        {
            _fileProcessor.Setup(p =>
                    p.ExtractAndParseZipWithHashesAsync(It.IsAny<Stream>(), id))
                .ReturnsAsync(new DiscoveryResult
                {
                    PolicyId = id,
                    Attributes = attrs,
                    Hashes = hashes ?? new Dictionary<string, string> { { "INI", "H1" } }
                });
        }

        private void NoSignal(string id) =>
            _signalFiles.Setup(s => s.Exists(It.IsAny<string>(), id)).Returns(false);

        private void WithSignal(string id, string? ticket = "CHG001")
        {
            _signalFiles.Setup(s => s.Exists(It.IsAny<string>(), id)).Returns(true);
            _signalFiles.Setup(s => s.ReadTicketId(It.IsAny<string>(), id)).Returns(ticket);
            _signalFiles.Setup(s => s.TryDelete(It.IsAny<string>(), id)).Returns(true);
        }

        private async Task SeedBaseline(string id, Dictionary<string, string> attrs)
        {
            _db.PlatformBaselines.Add(new PlatformBaseline
            {
                Id = Guid.NewGuid(),
                PlatformId = id,
                Attributes = attrs,
                AttributesHash = new Dictionary<string, string> { { "INI", "H1" } },
                IsActive = true,
                Version = 1,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }

        // ═══════════════════════════════════════════════════════
        //  1. ExecuteAsync — Early Exit Paths
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_InvalidContext_ExitsImmediately()
        {
            _driftPath.Setup(d => d.BuildDriftContextAsync())
                .ReturnsAsync(PolicyDriftContextResult.Failure("bad config"));

            await _workflow.ExecuteAsync();

            _fileSystem.Verify(f => f.DirectoryExists(It.IsAny<string>()), Times.Never);
        }

        [Test]
        public async Task Execute_SourceMissing_LogsWarning()
        {
            ValidContext();
            _fileSystem.Setup(f => f.DirectoryExists(SourcePath)).Returns(false);

            await _workflow.ExecuteAsync();

            _publisher.Verify(p => p.LogWarning(It.Is<string>(s => s.Contains("does not exist"))));
        }

        [Test]
        public async Task Execute_NoZips_LogsStandingDown()
        {
            ValidContext();
            _fileSystem.Setup(f => f.GetFilesInDirectory(SourcePath, "*.zip"))
                .Returns(Array.Empty<string>());

            await _workflow.ExecuteAsync();

            _publisher.Verify(p => p.LogInfo(It.Is<string>(s => s.Contains("Standing down"))));
        }

        // ═══════════════════════════════════════════════════════
        //  2. NO_DRIFT — Happy Path
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_NoDrift_RecordsCleanEval()
        {
            ValidContext();
            Zips("POL1");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("POL1"));
            MockParse("POL1", new() { { "INI:Key", "Val" } });
            NoSignal("POL1");
            await SeedBaseline("POL1", new() { { "INI:Key", "Val" } });

            await _workflow.ExecuteAsync();

            var eval = await _db.PolicyDriftEvals.FirstAsync(e => e.PolicyId == "POL1");
            Assert.That(eval.Status, Is.EqualTo("NO_DRIFT"));
            Assert.That(eval.Differences, Is.Null);
            _publisher.Verify(p => p.LogInfo(It.Is<string>(s => s.Contains("[CLEAN]"))));
            _publisher.Verify(p => p.LogInfo(It.Is<string>(s => s.Contains("[ARCHIVE]"))));
        }

        // ═══════════════════════════════════════════════════════
        //  3. DRIFT Detected
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_Drift_RecordsDriftAndFiresKafka()
        {
            ValidContext();
            Zips("POL1");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("POL1"));
            MockParse("POL1", new() { { "INI:Timeout", "500" } });
            NoSignal("POL1");
            await SeedBaseline("POL1", new() { { "INI:Timeout", "200" } });

            await _workflow.ExecuteAsync();

            var eval = await _db.PolicyDriftEvals.FirstAsync(e => e.PolicyId == "POL1");
            Assert.That(eval.Status, Is.EqualTo("DRIFT"));
            Assert.That(eval.Differences!["INI:Timeout"], Does.Contain("MODIFIED"));
            _publisher.Verify(p => p.PublishKafkaDriftAsync("POL1",
                It.IsAny<Dictionary<string, string>>(),
                It.IsAny<Dictionary<string, string>>()), Times.Once);
        }

        // ═══════════════════════════════════════════════════════
        //  4. MISSING_BASELINE
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_NoBaseline_RecordsMissingBaseline()
        {
            ValidContext();
            Zips("ORPHAN");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("ORPHAN"));
            MockParse("ORPHAN", new() { { "INI:K", "V" } });
            NoSignal("ORPHAN");

            await _workflow.ExecuteAsync();

            var eval = await _db.PolicyDriftEvals.FirstAsync(e => e.PolicyId == "ORPHAN");
            Assert.That(eval.Status, Is.EqualTo("MISSING_BASELINE"));
            Assert.That(eval.BaselinePolicyID, Is.EqualTo(Guid.Empty));
            _publisher.Verify(p => p.PublishStatusEventAsync(
                "ORPHAN", "POLICY_MISSING_BASELINE", It.IsAny<object>()));
        }

        // ═══════════════════════════════════════════════════════
        //  5. Invalid ZIP — Validation Failure
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_InvalidZip_LogsErrorArchivesAndSkipsEval()
        {
            ValidContext();
            Zips("BAD");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(BadZip());

            await _workflow.ExecuteAsync();

            _publisher.Verify(p => p.LogError(
                It.Is<string>(s => s.Contains("[VALIDATION]") && s.Contains("BAD")), null));
            _publisher.Verify(p => p.PublishStatusEventAsync(
                "BAD", "BATCH_PROCESSING_ERROR", It.IsAny<object>()));

            // Still archived to Processed
            _fileSystem.Verify(f => f.MoveFile(
                It.Is<string>(s => s.Contains("Processing")),
                It.Is<string>(s => s.Contains("Processed"))));

            // No eval recorded
            Assert.That(await _db.PolicyDriftEvals.AnyAsync(e => e.PolicyId == "BAD"), Is.False);
        }

        // ═══════════════════════════════════════════════════════
        //  6. Signal File — Successful Promotion
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_Signal_PromotesBaselineAndConsumes()
        {
            ValidContext();
            Zips("SRV");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("SRV"));
            MockParse("SRV", new() { { "INI:P", "V" } });
            WithSignal("SRV", "CHG999");

            await _workflow.ExecuteAsync();

            _db.ChangeTracker.Clear();
            var bl = await _db.PlatformBaselines.FirstAsync(b => b.PlatformId == "SRV" && b.IsActive);
            Assert.That(bl.Version, Is.EqualTo(1));
            Assert.That(bl.Attributes["INI:P"], Is.EqualTo("V"));

            _signalFiles.Verify(s => s.TryDelete(It.IsAny<string>(), "SRV"), Times.Once);
            _publisher.Verify(p => p.PublishStatusEventAsync(
                "SRV", "BASELINE_PROMOTED", It.IsAny<object>()), Times.Once);
        }

        // ═══════════════════════════════════════════════════════
        //  7. Signal File — Empty Attributes Rejects
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_Signal_EmptyAttributes_RejectsPromotion()
        {
            ValidContext();
            Zips("EMPTY");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("EMPTY"));

            _fileProcessor.Setup(p =>
                    p.ExtractAndParseZipWithHashesAsync(It.IsAny<Stream>(), "EMPTY"))
                .ReturnsAsync(new DiscoveryResult
                {
                    PolicyId = "EMPTY",
                    Attributes = new Dictionary<string, string>(),
                    Hashes = new Dictionary<string, string>()
                });

            _signalFiles.Setup(s => s.Exists(It.IsAny<string>(), "EMPTY")).Returns(true);

            await _workflow.ExecuteAsync();

            _publisher.Verify(p => p.LogError(
                It.Is<string>(s => s.Contains("Refusing to promote empty")), null));
            _signalFiles.Verify(s => s.TryDelete(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.That(await _db.PlatformBaselines.AnyAsync(b => b.PlatformId == "EMPTY"), Is.False);
        }

        // ═══════════════════════════════════════════════════════
        //  8. Signal File — Missing SNOW Ticket (allowed)
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_Signal_NullTicket_PromotesWithMissingSnowEvent()
        {
            ValidContext();
            Zips("SRV");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("SRV"));
            MockParse("SRV", new() { { "INI:K", "V" } });
            WithSignal("SRV", null);

            await _workflow.ExecuteAsync();

            _db.ChangeTracker.Clear();
            var bl = await _db.PlatformBaselines.FirstOrDefaultAsync(b => b.PlatformId == "SRV" && b.IsActive);
            Assert.That(bl, Is.Not.Null);

            _publisher.Verify(p => p.PublishStatusEventAsync(
                "SRV", "BASELINE_MISSING_SNOW_TICKET", It.IsAny<object>()), Times.Once);
            _signalFiles.Verify(s => s.TryDelete(It.IsAny<string>(), "SRV"), Times.Once);
        }

        // ═══════════════════════════════════════════════════════
        //  9. SNOW Ticket Required — Rejection Path
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_SnowRequired_EmptyTicket_RejectsAndPreservesSignal()
        {
            var strictConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Governance:RequireSnowTicket"] = "true"
                }).Build();
            BuildWorkflow(strictConfig);

            ValidContext();
            Zips("SRV");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("SRV"));
            MockParse("SRV", new() { { "INI:K", "V" } });

            _signalFiles.Setup(s => s.Exists(It.IsAny<string>(), "SRV")).Returns(true);
            _signalFiles.Setup(s => s.ReadTicketId(It.IsAny<string>(), "SRV")).Returns((string?)null);

            await _workflow.ExecuteAsync();

            _publisher.Verify(p => p.PublishStatusEventAsync(
                "SRV", "BASELINE_PROMOTION_REJECTED", It.IsAny<object>()), Times.Once);

            // Signal NOT consumed
            _signalFiles.Verify(s => s.TryDelete(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

            // No baseline created
            Assert.That(await _db.PlatformBaselines.AnyAsync(b => b.PlatformId == "SRV"), Is.False);
        }

        [Test]
        public async Task Execute_SnowRequired_WithTicket_PromotesSuccessfully()
        {
            var strictConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Governance:RequireSnowTicket"] = "true"
                }).Build();
            BuildWorkflow(strictConfig);

            ValidContext();
            Zips("SRV");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("SRV"));
            MockParse("SRV", new() { { "INI:K", "V" } });
            WithSignal("SRV", "CHG777");

            await _workflow.ExecuteAsync();

            _db.ChangeTracker.Clear();
            var bl = await _db.PlatformBaselines.FirstOrDefaultAsync(b => b.PlatformId == "SRV" && b.IsActive);
            Assert.That(bl, Is.Not.Null);
            _publisher.Verify(p => p.PublishStatusEventAsync(
                "SRV", "BASELINE_PROMOTED", It.IsAny<object>()), Times.Once);
        }

        // ═══════════════════════════════════════════════════════
        //  10. Processing Error — Continues to Next ZIP
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_FirstZipFails_SecondStillProcessed()
        {
            ValidContext();
            Zips("LOCKED", "GOOD");

            _fileSystem.Setup(f => f.MoveFile(
                    It.Is<string>(s => s.Contains("LOCKED")), It.IsAny<string>()))
                .Throws(new IOException("locked"));

            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("GOOD"));
            MockParse("GOOD", new() { { "INI:K", "V" } });
            NoSignal("GOOD");

            await _workflow.ExecuteAsync();

            _publisher.Verify(p => p.LogError(
                It.Is<string>(s => s.Contains("LOCKED")), It.IsAny<Exception>()));
            _publisher.Verify(p => p.PublishStatusEventAsync(
                "LOCKED", "BATCH_PROCESSING_ERROR", It.IsAny<object>()));
            _publisher.Verify(p => p.LogInfo(
                It.Is<string>(s => s.Contains("[PROCESS]") && s.Contains("GOOD"))));
        }

        // ═══════════════════════════════════════════════════════
        //  11. Baseline Versioning — v1 → v2
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_Signal_ExistingBaseline_IncreasesVersion()
        {
            ValidContext();
            Zips("SRV");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("SRV"));
            MockParse("SRV", new() { { "INI:New", "V2" } });
            WithSignal("SRV", "CHG002");
            await SeedBaseline("SRV", new() { { "INI:Old", "V1" } });

            await _workflow.ExecuteAsync();

            _db.ChangeTracker.Clear();
            var old = await _db.PlatformBaselines.FirstAsync(b => b.PlatformId == "SRV" && !b.IsActive);
            var cur = await _db.PlatformBaselines.FirstAsync(b => b.PlatformId == "SRV" && b.IsActive);
            Assert.That(old.Version, Is.EqualTo(1));
            Assert.That(cur.Version, Is.EqualTo(2));
            Assert.That(cur.Attributes["INI:New"], Is.EqualTo("V2"));
        }

        // ═══════════════════════════════════════════════════════
        //  12. Promotion Then Eval — Same Attributes = NO_DRIFT
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_Signal_ThenEval_SameAttributes_NoDrift()
        {
            ValidContext();
            Zips("SRV");
            var data = new Dictionary<string, string> { { "INI:T", "200" } };
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("SRV"));
            MockParse("SRV", data);
            WithSignal("SRV", "CHG003");

            await _workflow.ExecuteAsync();

            var eval = await _db.PolicyDriftEvals.FirstAsync(e => e.PolicyId == "SRV");
            Assert.That(eval.Status, Is.EqualTo("NO_DRIFT"));
        }

        // ═══════════════════════════════════════════════════════
        //  13. Archive — Valid ZIP Moves to Processed
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_Success_ArchivesToProcessed()
        {
            ValidContext();
            Zips("X");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("X"));
            MockParse("X", new() { { "INI:K", "V" } });
            NoSignal("X");

            await _workflow.ExecuteAsync();

            _fileSystem.Verify(f => f.MoveFile(
                It.Is<string>(s => s.Contains("Processing") && s.Contains("X")),
                It.Is<string>(s => s.Contains("Processed") && s.Contains("X"))));
        }

        // ═══════════════════════════════════════════════════════
        //  14. Staging Created
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_ValidContext_CreatesStaging()
        {
            ValidContext();
            Zips("X");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("X"));
            MockParse("X", new() { { "INI:K", "V" } });
            NoSignal("X");

            await _workflow.ExecuteAsync();

            _driftPath.Verify(d => d.EnsureStagingDirectories(It.IsAny<PolicyDriftContext>()));
        }

        // ═══════════════════════════════════════════════════════
        //  15. Multiple ZIPs — Batch Completes
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_MultipleZips_AllProcessed()
        {
            ValidContext();
            Zips("A", "B");

            // Return a ZIP matching the policyId being opened
            _fileSystem.Setup(f => f.OpenRead(It.Is<string>(s => s.Contains("A"))))
                .Returns(() => GoodZip("A"));
            _fileSystem.Setup(f => f.OpenRead(It.Is<string>(s => s.Contains("B"))))
                .Returns(() => GoodZip("B"));

            MockParse("A", new() { { "INI:K", "VA" } });
            MockParse("B", new() { { "INI:K", "VB" } });
            NoSignal("A");
            NoSignal("B");

            await _workflow.ExecuteAsync();

            var count = await _db.PolicyDriftEvals.CountAsync();
            Assert.That(count, Is.GreaterThanOrEqualTo(2));
            _publisher.Verify(p => p.LogInfo(It.Is<string>(s => s.Contains("complete"))));
        }

        // ═══════════════════════════════════════════════════════
        //  16. Batch Report — Kafka BATCH_GOVERNANCE_REPORT
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_AfterProcessing_FiresBatchGovernanceReport()
        {
            ValidContext();
            Zips("P");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("P"));
            MockParse("P", new() { { "INI:K", "V" } });
            NoSignal("P");

            _publisher.Setup(p => p.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new NotImplementedException());

            await _workflow.ExecuteAsync();

            _publisher.Verify(p => p.PublishStatusEventAsync(
                "BATCH", "BATCH_GOVERNANCE_REPORT", It.IsAny<object>()), Times.Once);
        }

        // ═══════════════════════════════════════════════════════
        //  17. Batch Report — Email NotImplemented Graceful
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_EmailNotImplemented_LogsWarningAndContinues()
        {
            ValidContext();
            Zips("P");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("P"));
            MockParse("P", new() { { "INI:K", "V" } });
            NoSignal("P");

            _publisher.Setup(p => p.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new NotImplementedException());

            await _workflow.ExecuteAsync();

            _publisher.Verify(p => p.LogWarning(
                It.Is<string>(s => s.Contains("Email delivery not yet implemented"))));

            // Pipeline still completes
            _publisher.Verify(p => p.LogInfo(It.Is<string>(s => s.Contains("complete"))));
        }

        // ═══════════════════════════════════════════════════════
        //  18. Batch Report — Email General Error
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_EmailThrowsGenericError_LogsErrorAndContinues()
        {
            ValidContext();
            Zips("P");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("P"));
            MockParse("P", new() { { "INI:K", "V" } });
            NoSignal("P");

            _publisher.Setup(p => p.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new InvalidOperationException("SMTP down"));

            await _workflow.ExecuteAsync();

            _publisher.Verify(p => p.LogError(
                It.Is<string>(s => s.Contains("Failed to send governance report")),
                It.IsAny<Exception>()));

            _publisher.Verify(p => p.LogInfo(It.Is<string>(s => s.Contains("complete"))));
        }

        // ═══════════════════════════════════════════════════════
        //  19. Batch Report — Summary Event Saved to DB
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_AfterProcessing_SavesBatchReportEvent()
        {
            ValidContext();
            Zips("P");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("P"));
            MockParse("P", new() { { "INI:K", "V" } });
            NoSignal("P");

            _publisher.Setup(p => p.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new NotImplementedException());

            await _workflow.ExecuteAsync();

            var reportEvent = await _db.PolicyEvents
                .FirstOrDefaultAsync(e => e.EventName == "BATCH_REPORT_GENERATED");
            Assert.That(reportEvent, Is.Not.Null);
            Assert.That(reportEvent!.PolicyId, Is.EqualTo("BATCH"));
        }

        // ═══════════════════════════════════════════════════════
        //  20. Detail Dedup — Same Hashes Reuse Detail ID
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_SameHashes_OnlyOneDetailRecord()
        {
            ValidContext();
            Zips("DD");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(() => GoodZip("DD"));
            var hashes = new Dictionary<string, string> { { "INI", "SAMEHASH" } };
            MockParse("DD", new() { { "INI:K", "V" } }, hashes);
            NoSignal("DD");

            await _workflow.ExecuteAsync();

            // Call again manually — should reuse the detail
            await _db.GetOrCreatePolicyDetailIdAsync("DD",
                new() { { "INI:K", "V" } }, hashes);

            var count = await _db.PolicyDriftEvalDetails.CountAsync(d => d.PolicyId == "DD");
            Assert.That(count, Is.EqualTo(1));
        }

        // ═══════════════════════════════════════════════════════
        //  21. Drift Scope — DLL Disabled = No Drift
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_DllDisabled_DllChangeNotDrift()
        {
            var scopeConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Governance:DriftScope:DLL"] = "false"
                }).Build();
            BuildWorkflow(scopeConfig);

            ValidContext();
            Zips("POL");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("POL"));
            MockParse("POL", new() { { "INI:K", "V" }, { "DLL:Hash", "new" } });
            NoSignal("POL");
            await SeedBaseline("POL", new() { { "INI:K", "V" }, { "DLL:Hash", "old" } });

            await _workflow.ExecuteAsync();

            var eval = await _db.PolicyDriftEvals.FirstAsync(e => e.PolicyId == "POL");
            Assert.That(eval.Status, Is.EqualTo("NO_DRIFT"));
        }

        // ═══════════════════════════════════════════════════════
        //  22. Signal — No Signal File = Skips Promotion
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_NoSignalFile_SkipsPromotionEntirely()
        {
            ValidContext();
            Zips("SRV");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("SRV"));
            MockParse("SRV", new() { { "INI:K", "V" } });
            NoSignal("SRV");

            await _workflow.ExecuteAsync();

            _signalFiles.Verify(s => s.ReadTicketId(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _signalFiles.Verify(s => s.TryDelete(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        // ═══════════════════════════════════════════════════════
        //  23. POLICY_PROCESSING_STARTED Event Saved
        // ═══════════════════════════════════════════════════════

        [Test]
        public async Task Execute_ProcessingStarted_EventSavedToDb()
        {
            ValidContext();
            Zips("SRV");
            _fileSystem.Setup(f => f.OpenRead(It.IsAny<string>())).Returns(GoodZip("SRV"));
            MockParse("SRV", new() { { "INI:K", "V" } });
            NoSignal("SRV");

            await _workflow.ExecuteAsync();

            var startEvent = await _db.PolicyEvents
                .FirstOrDefaultAsync(e => e.EventName == "POLICY_PROCESSING_STARTED" && e.PolicyId == "SRV");
            Assert.That(startEvent, Is.Not.Null);
        }
    }
}
