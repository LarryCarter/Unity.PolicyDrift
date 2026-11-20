using Microsoft.EntityFrameworkCore;
using CVIS.Unity.Core.Entities;
using CVIS.Unity.Infrastructure.Data;
using NUnit.Framework;

namespace CVIS.Unity.Tests
{
    [TestFixture]
    public class PolicyDbContextTests
    {
        private PolicyDbContext _context;

        [SetUp]
        public void Setup()
        {
            var options = new DbContextOptionsBuilder<PolicyDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            _context = new PolicyDbContext(options);
        }

        [TearDown]
        public void TearDown()
        {
            _context.Dispose();
        }

        [Test]
        public async Task GetOrCreatePolicyDetailId_DedupesCorrectHashes()
        {
            // Arrange: Scoped hashes for INI and DLL
            var policyId = "ANS-SA-N-R";
            var attr = new Dictionary<string, string> { { "INI:Timeout", "200" } };
            var hashes = new Dictionary<string, string>
            {
                { "INI", "HASH_123" },
                { "DLL", "HASH_456" }
            };

            // Act: First discovery
            var id1 = await _context.GetOrCreatePolicyDetailIdAsync(policyId, attr, hashes);

            // Act: Second discovery (same files)
            var id2 = await _context.GetOrCreatePolicyDetailIdAsync(policyId, attr, hashes);

            // Assert: Arsenal remains lean
            Assert.That(id1, Is.EqualTo(id2));
            Assert.That(await _context.PolicyDriftEvalDetails.CountAsync(), Is.EqualTo(1));
        }

        [Test]
        public async Task UpsertBaseline_Correctlly_Applies_SourcePrefixes()
        {
            // Arrange
            var policyId = "ANS-SA-N-R";
            var attributes = new Dictionary<string, string>
            {
                { "INI:PasswordLength", "16" },
                { "XML:AutoChangeOnAdd", "Yes" }
            };
            var hashes = new Dictionary<string, string> { { "INI", "H1" }, { "XML", "H2" } };

            // Act
            await _context.UpsertBaselineAsync(policyId, attributes, hashes);

            // Assert
            var baseline = await _context.PlatformBaselines.FirstAsync(b => b.PlatformId == policyId);
            Assert.That(baseline.Attributes["INI:PasswordLength"], Is.EqualTo("16"));
            Assert.That(baseline.AttributesHash["XML"], Is.EqualTo("H2"));
        }

        [Test]
        public async Task SavePolicyEvent_Normalizes_Anonymous_Metadata()
        {
            // Arrange: Datyrix flexible metadata
            var meta = new { Version = 2, Status = "Verified" };

            // Act
            await _context.SavePolicyEventAsync("ANS-TEST", "DRIFT_CHECK", "AUDIT", meta);

            // Assert
            var evt = await _context.PolicyEvents.FirstAsync();
            Assert.That(evt.Metadata["Version"], Is.EqualTo("2"));
            Assert.That(evt.Metadata["Status"], Is.EqualTo("Verified"));
        }
    }
}