using NUnit.Framework;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CVIS.Unity.PolicyDrift.Orchestration.Services;

namespace CVIS.Unity.Tests
{
    [TestFixture]
    public class FileProcessorIniTests
    {
        private FileProcessor _processor;
        private const string SampleIni = @"
            [Section]
            ; This is a comment
            Timeout=200
            PasswordLength=16
            
            [Extra]
            AutoUnlock=Yes";

        [SetUp]
        public void Setup()
        {
            _processor = new FileProcessor();
        }

        [Test]
        public void ParseIni_ByteArray_ShouldApplyPrefixAndSkipNoise()
        {
            // Arrange
            var content = Encoding.UTF8.GetBytes(SampleIni);
            var attributes = new Dictionary<string, string>();

            // Act
            _processor.ParseIni(content, attributes);

            // Assert: Verify "which is which" prefixing
            Assert.Multiple(() =>
            {
                Assert.That(attributes["INI:Timeout"], Is.EqualTo("200"));
                Assert.That(attributes["INI:PasswordLength"], Is.EqualTo("16"));
                Assert.That(attributes["INI:AutoUnlock"], Is.EqualTo("Yes"));
                Assert.That(attributes.ContainsKey("[Section]"), Is.False); // Headers skipped
                Assert.That(attributes.Count, Is.EqualTo(3));
            });
        }

        [Test]
        public async Task ParseIni_Stream_ShouldProduceIdenticalResults()
        {
            // Arrange
            var attributes = new Dictionary<string, string>();
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(SampleIni));

            // Act
            await _processor.ParseIni(ms, attributes);

            // Assert: Logical parity with byte array version
            Assert.That(attributes["INI:Timeout"], Is.EqualTo("200"));
            Assert.That(attributes.ContainsKey("INI:AutoUnlock"), Is.True);
        }

        [Test]
        public void ParseIniCore_ShouldHandleDuplicateKeysByOverwriting()
        {
            // Arrange: Testing the "Last Value Wins" rule
            var duplicateContent = "Key1=OldValue\nKey1=NewValue";
            var attributes = new Dictionary<string, string>();

            // Act
            _processor.ParseIniCore(duplicateContent, attributes);

            // Assert
            Assert.That(attributes["INI:Key1"], Is.EqualTo("NewValue"));
        }
    }
}