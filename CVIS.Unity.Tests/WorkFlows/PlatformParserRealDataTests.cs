using CVIS.Unity.PolicyDrift.Orchestration.Services;
using NUnit.Framework;
using System.Collections.Generic;
using System.Text;

namespace CVIS.Unity.Tests.WorkFlows
{
    [TestFixture]
    public class PlatformParserRealDataTests
    {
        private FileProcessor _processor;

        [SetUp]
        public void Setup() => _processor = new FileProcessor();

        [Test]
        public void ParseIni_RealWorldData_VerifiesScopedPrefixes()
        {
            // Data from image_3562e1.jpg
            var iniContent = @"
                PolicyID=ANS-SA-N-R
                Timeout=200
                PasswordLength=16
                [ADExtraInfo]
                EnforcePasswordPolicyOnManualChange=No
                [ExtraInfo]
                UseSSL=Yes";

            var attributes = new Dictionary<string, string>();
            _processor.ParseIni(Encoding.UTF8.GetBytes(iniContent), attributes);

            // Assertions based on visual evidence
            Assert.Multiple(() =>
            {
                Assert.That(attributes["INI:PolicyID"], Is.EqualTo("ANS-SA-N-R"));
                Assert.That(attributes["INI:Timeout"], Is.EqualTo("200"));
                Assert.That(attributes["INI:PasswordLength"], Is.EqualTo("16"));
                Assert.That(attributes["INI:UseSSL"], Is.EqualTo("Yes"));
                // Verify section header was ignored
                Assert.That(attributes.ContainsKey("INI:[ExtraInfo]"), Is.False);
            });
        }

        [Test]
        public void ParseXml_RealWorldData_VerifiesSyntheticKeys()
        {
            // Data from image_3562da.jpg
            var xmlContent = @"
                <Device Name='Imported Platforms'>
                    <Policies>
                        <Policy ID='ANS-SA-N-R' PlatformBaseID='UnixSSH' PlatformBaseProtocol='SSH'>
                            <Properties>
                                <Required>
                                    <Property Name='Address'/>
                                </Required>
                            </Properties>
                        </Policy>
                    </Policies>
                </Device>";

            var attributes = new Dictionary<string, string>();
            _processor.ParseXml(Encoding.UTF8.GetBytes(xmlContent), attributes);

            // Assertions based on visual evidence
            Assert.Multiple(() =>
            {
                // Verify Attribute Mapping
                Assert.That(attributes["XML:Policy_PlatformBaseID"], Is.EqualTo("UnixSSH"));
                Assert.That(attributes["XML:Policy_PlatformBaseProtocol"], Is.EqualTo("SSH"));
                // Verify Nested Attribute Mapping
                Assert.That(attributes["XML:Property_Name"], Is.EqualTo("Address"));
            });
        }
    }
}