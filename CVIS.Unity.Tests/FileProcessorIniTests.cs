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
            var content = Encoding.UTF8.GetBytes(SampleIni);
            var attributes = new Dictionary<string, string>();

            _processor.ParseIni(content, attributes);

            Assert.Multiple(() =>
            {
                Assert.That(attributes["INI:Timeout"], Is.EqualTo("200"));
                Assert.That(attributes["INI:PasswordLength"], Is.EqualTo("16"));
                Assert.That(attributes["INI:AutoUnlock"], Is.EqualTo("Yes"));
                Assert.That(attributes.ContainsKey("[Section]"), Is.False);
                Assert.That(attributes.Count, Is.EqualTo(3));
            });
        }

        [Test]
        public void ParseIniCore_ShouldHandleDuplicateKeysByOverwriting()
        {
            var duplicateContent = "Key1=OldValue\nKey1=NewValue";
            var attributes = new Dictionary<string, string>();

            _processor.ParseIniCore(duplicateContent, attributes);

            Assert.That(attributes["INI:Key1"], Is.EqualTo("NewValue"));
        }

        [Test]
        public void ParseIni_EmptyContent_ShouldReturnEmptyAttributes()
        {
            var content = Encoding.UTF8.GetBytes("");
            var attributes = new Dictionary<string, string>();

            _processor.ParseIni(content, attributes);

            Assert.That(attributes.Count, Is.EqualTo(0));
        }

        [Test]
        public void ParseIni_OnlyCommentsAndHeaders_ShouldReturnEmptyAttributes()
        {
            var content = Encoding.UTF8.GetBytes(
                "[SectionA]\n; comment line\n[SectionB]\n; another comment");
            var attributes = new Dictionary<string, string>();

            _processor.ParseIni(content, attributes);

            Assert.That(attributes.Count, Is.EqualTo(0));
        }

        [Test]
        public void ParseIni_ValueContainsEqualsSign_ShouldSplitOnFirstOnly()
        {
            var content = Encoding.UTF8.GetBytes("ConnectionString=Server=prod;DB=Unity");
            var attributes = new Dictionary<string, string>();

            _processor.ParseIni(content, attributes);

            Assert.That(attributes["INI:ConnectionString"], Is.EqualTo("Server=prod;DB=Unity"));
        }

        [Test]
        public void ParseIni_WhitespaceAroundKeysAndValues_ShouldTrim()
        {
            var content = Encoding.UTF8.GetBytes("  Timeout  =  200  ");
            var attributes = new Dictionary<string, string>();

            _processor.ParseIni(content, attributes);

            Assert.That(attributes["INI:Timeout"], Is.EqualTo("200"));
        }
    }
}