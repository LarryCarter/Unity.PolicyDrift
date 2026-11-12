using CVIS.Unity.Core.Interfaces;
using CVIS.Unity.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CVIS.Unity.Tests
{
    [TestFixture]
    public class DriftComparisonServiceTests
    {
        private Mock<IUnityEventPublisher> _publisher;

        [SetUp]
        public void SetUp()
        {
            _publisher = new Mock<IUnityEventPublisher>();
        }

        private DriftComparisonService CreateService(Dictionary<string, string?>? configValues = null)
        {
            var defaults = new Dictionary<string, string?>
            {
                ["Governance:RequireSnowTicket"] = "false",
                ["Governance:DriftScope:XML"] = "true",
                ["Governance:DriftScope:DLL"] = "true",
                ["Governance:DriftScope:EXE"] = "true"
            };

            if (configValues != null)
            {
                foreach (var kvp in configValues)
                    defaults[kvp.Key] = kvp.Value;
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(defaults)
                .Build();

            return new DriftComparisonService(configuration, _publisher.Object);
        }

        // ─────────────────────────────────────────────────────────
        //  SNOW Ticket Enforcement
        // ─────────────────────────────────────────────────────────

        [Test]
        public void IsPromotionAllowed_RequireFalse_AllowsWithoutTicket()
        {
            var service = CreateService(new Dictionary<string, string?>
            {
                ["Governance:RequireSnowTicket"] = "false"
            });

            Assert.That(service.IsPromotionAllowed(null), Is.True);
            Assert.That(service.IsPromotionAllowed(""), Is.True);
        }

        [Test]
        public void IsPromotionAllowed_RequireTrue_RejectsWithoutTicket()
        {
            var service = CreateService(new Dictionary<string, string?>
            {
                ["Governance:RequireSnowTicket"] = "true"
            });

            Assert.That(service.IsPromotionAllowed(null), Is.False);
            Assert.That(service.IsPromotionAllowed(""), Is.False);
            Assert.That(service.IsPromotionAllowed("   "), Is.False);
        }

        [Test]
        public void IsPromotionAllowed_RequireTrue_AllowsWithTicket()
        {
            var service = CreateService(new Dictionary<string, string?>
            {
                ["Governance:RequireSnowTicket"] = "true"
            });

            Assert.That(service.IsPromotionAllowed("CHG0012345"), Is.True);
        }

        [Test]
        public void RequireSnowTicket_DefaultsFalse_WhenNotConfigured()
        {
            var service = CreateService(new Dictionary<string, string?>
            {
                ["Governance:RequireSnowTicket"] = null
            });

            Assert.That(service.RequireSnowTicket, Is.False);
        }

        // ─────────────────────────────────────────────────────────
        //  Drift Scope Configuration
        // ─────────────────────────────────────────────────────────

        [Test]
        public void GetActiveDriftScopes_AllEnabled_ReturnsFourScopes()
        {
            var service = CreateService();
            var scopes = service.GetActiveDriftScopes();

            Assert.That(scopes, Does.Contain("INI"));
            Assert.That(scopes, Does.Contain("XML"));
            Assert.That(scopes, Does.Contain("DLL"));
            Assert.That(scopes, Does.Contain("EXE"));
            Assert.That(scopes.Count, Is.EqualTo(4));
        }

        [Test]
        public void GetActiveDriftScopes_DllDisabled_ExcludesDll()
        {
            var service = CreateService(new Dictionary<string, string?>
            {
                ["Governance:DriftScope:DLL"] = "false"
            });

            var scopes = service.GetActiveDriftScopes();

            Assert.That(scopes, Does.Contain("INI"));
            Assert.That(scopes, Does.Contain("XML"));
            Assert.That(scopes, Does.Not.Contain("DLL"));
            Assert.That(scopes, Does.Contain("EXE"));
        }

        [Test]
        public void GetActiveDriftScopes_OnlyIniEnabled()
        {
            var service = CreateService(new Dictionary<string, string?>
            {
                ["Governance:DriftScope:XML"] = "false",
                ["Governance:DriftScope:DLL"] = "false",
                ["Governance:DriftScope:EXE"] = "false"
            });

            var scopes = service.GetActiveDriftScopes();

            Assert.That(scopes.Count, Is.EqualTo(1));
            Assert.That(scopes, Does.Contain("INI"));
        }

        [Test]
        public void GetActiveDriftScopes_IniAlwaysPresent_CannotBeDisabled()
        {
            // INI has no config key — it's always on
            var service = CreateService();
            var scopes = service.GetActiveDriftScopes();

            Assert.That(scopes, Does.Contain("INI"));
        }

        [Test]
        public void GetActiveDriftScopes_DefaultsToEnabled_WhenNotConfigured()
        {
            var service = CreateService(new Dictionary<string, string?>
            {
                ["Governance:DriftScope:XML"] = null,
                ["Governance:DriftScope:DLL"] = null,
                ["Governance:DriftScope:EXE"] = null
            });

            var scopes = service.GetActiveDriftScopes();
            Assert.That(scopes.Count, Is.EqualTo(4));
        }

        // ─────────────────────────────────────────────────────────
        //  CompareAttributes — Scope Filtering
        // ─────────────────────────────────────────────────────────

        [Test]
        public void CompareAttributes_AllScopesEnabled_DetectsAllDrift()
        {
            var service = CreateService();

            var baseline = new Dictionary<string, string>
            {
                { "INI:Timeout", "200" },
                { "XML:PlatformBaseID", "UnixSSH" },
                { "DLL:FileHash", "sha256:old" }
            };
            var current = new Dictionary<string, string>
            {
                { "INI:Timeout", "500" },
                { "XML:PlatformBaseID", "WinSSH" },
                { "DLL:FileHash", "sha256:new" }
            };

            var drift = service.CompareAttributes(baseline, current);

            Assert.That(drift.Count, Is.EqualTo(3));
            Assert.That(drift.ContainsKey("INI:Timeout"), Is.True);
            Assert.That(drift.ContainsKey("XML:PlatformBaseID"), Is.True);
            Assert.That(drift.ContainsKey("DLL:FileHash"), Is.True);
        }

        [Test]
        public void CompareAttributes_DllDisabled_IgnoresDllChanges()
        {
            var service = CreateService(new Dictionary<string, string?>
            {
                ["Governance:DriftScope:DLL"] = "false"
            });

            var baseline = new Dictionary<string, string>
            {
                { "INI:Timeout", "200" },
                { "DLL:FileHash", "sha256:old" }
            };
            var current = new Dictionary<string, string>
            {
                { "INI:Timeout", "500" },
                { "DLL:FileHash", "sha256:new" }
            };

            var drift = service.CompareAttributes(baseline, current);

            Assert.That(drift.Count, Is.EqualTo(1));
            Assert.That(drift.ContainsKey("INI:Timeout"), Is.True);
            Assert.That(drift.ContainsKey("DLL:FileHash"), Is.False);
        }

        [Test]
        public void CompareAttributes_XmlAndExeDisabled_OnlyIniAndDllCount()
        {
            var service = CreateService(new Dictionary<string, string?>
            {
                ["Governance:DriftScope:XML"] = "false",
                ["Governance:DriftScope:EXE"] = "false"
            });

            var baseline = new Dictionary<string, string>
            {
                { "INI:Timeout", "200" },
                { "XML:AutoChangeOnAdd", "Yes" },
                { "DLL:FileHash", "sha256:old" },
                { "EXE:FileHash", "sha256:old_exe" }
            };
            var current = new Dictionary<string, string>
            {
                { "INI:Timeout", "500" },
                { "XML:AutoChangeOnAdd", "No" },
                { "DLL:FileHash", "sha256:new" },
                { "EXE:FileHash", "sha256:new_exe" }
            };

            var drift = service.CompareAttributes(baseline, current);

            Assert.That(drift.Count, Is.EqualTo(2));
            Assert.That(drift.ContainsKey("INI:Timeout"), Is.True);
            Assert.That(drift.ContainsKey("DLL:FileHash"), Is.True);
            Assert.That(drift.ContainsKey("XML:AutoChangeOnAdd"), Is.False);
            Assert.That(drift.ContainsKey("EXE:FileHash"), Is.False);
        }

        [Test]
        public void CompareAttributes_DisabledScope_StillIgnoresNoiseKeys()
        {
            var service = CreateService();

            var baseline = new Dictionary<string, string>
            {
                { "INI:ApiVersion", "v1" },
                { "INI:Timeout", "200" }
            };
            var current = new Dictionary<string, string>
            {
                { "INI:ApiVersion", "v2" },
                { "INI:Timeout", "200" }
            };

            var drift = service.CompareAttributes(baseline, current);

            Assert.That(drift.Count, Is.EqualTo(0));
        }

        [Test]
        public void CompareAttributes_NoDrift_ReturnsEmpty()
        {
            var service = CreateService();

            var data = new Dictionary<string, string>
            {
                { "INI:Timeout", "200" },
                { "XML:PlatformBaseID", "UnixSSH" }
            };

            var drift = service.CompareAttributes(data, data);

            Assert.That(drift.Count, Is.EqualTo(0));
        }

        [Test]
        public void CompareAttributes_AddedAndRemoved_DetectedForActiveScopes()
        {
            var service = CreateService(new Dictionary<string, string?>
            {
                ["Governance:DriftScope:DLL"] = "false"
            });

            var baseline = new Dictionary<string, string>
            {
                { "INI:OldSetting", "val" },
                { "DLL:FileHash", "sha256:old" }
            };
            var current = new Dictionary<string, string>
            {
                { "INI:NewSetting", "val" },
                { "DLL:FileHash", "sha256:new" }
            };

            var drift = service.CompareAttributes(baseline, current);

            // INI:OldSetting = REMOVED, INI:NewSetting = ADDED, DLL skipped
            Assert.That(drift.Count, Is.EqualTo(2));
            Assert.That(drift["INI:OldSetting"], Does.Contain("REMOVED"));
            Assert.That(drift["INI:NewSetting"], Does.Contain("ADDED"));
            Assert.That(drift.ContainsKey("DLL:FileHash"), Is.False);
        }

        // ─────────────────────────────────────────────────────────
        //  Constructor Guards
        // ─────────────────────────────────────────────────────────

        [Test]
        public void Constructor_NullConfiguration_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new DriftComparisonService(null!, _publisher.Object));
        }

        [Test]
        public void Constructor_NullPublisher_Throws()
        {
            var config = new ConfigurationBuilder().Build();
            Assert.Throws<ArgumentNullException>(() =>
                new DriftComparisonService(config, null!));
        }
    }
}
