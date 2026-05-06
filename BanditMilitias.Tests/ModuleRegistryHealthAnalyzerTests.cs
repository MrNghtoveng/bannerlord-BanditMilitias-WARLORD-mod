using BanditMilitias.Core.Components;
using BanditMilitias.Core.Registry;
using BanditMilitias.Systems.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

namespace BanditMilitias.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class ModuleRegistryHealthAnalyzerTests
    {
        [TestInitialize]
        public void ResetRegistryBeforeTest()
        {
            ModuleRegistry.Instance.Reset();
        }

        [TestCleanup]
        public void ResetRegistryAfterTest()
        {
            ModuleRegistry.Instance.Reset();
        }

        [TestMethod]
        public void FindColdModules_Returns_Only_Registered_Modules_Without_Health_Signal()
        {
            var cold = new ModuleEntry
            {
                Name = "ColdModule",
                ModuleName = "ColdModule",
                Status = ModuleStatus.Registered,
            };

            var warmByActivity = new ModuleEntry
            {
                Name = "WarmByActivity",
                ModuleName = "WarmByActivity",
                Status = ModuleStatus.Registered,
                SuccessfulOperations = 1,
            };

            var warmByHealthyTimestamp = new ModuleEntry
            {
                Name = "WarmByHealthyTimestamp",
                ModuleName = "WarmByHealthyTimestamp",
                Status = ModuleStatus.Registered,
                LastHealthyUtc = DateTime.UtcNow,
            };

            var failed = new ModuleEntry
            {
                Name = "FailedModule",
                ModuleName = "FailedModule",
                Status = ModuleStatus.Failed,
            };

            var coldModules = ModuleRegistryHealthAnalyzer.FindColdModules(new[] { cold, warmByActivity, warmByHealthyTimestamp, failed });

            Assert.AreEqual(1, coldModules.Count);
            Assert.AreEqual("ColdModule", coldModules.Single().DisplayName);
        }

        [TestMethod]
        public void Capture_Finds_Registered_But_Never_Healthy_Modules()
        {
            var registry = ModuleRegistry.Instance;
            var cold = new ColdBootTestModule();
            var healthy = new HealthyBootTestModule();

            registry.Confirm(cold);
            registry.Confirm(healthy);
            registry.MarkHealthy(healthy, "Initialize");

            ModuleRegistryHealthSnapshot snapshot = ModuleRegistryHealthAnalyzer.Capture(
                registry,
                new AuditOptions
                {
                    RefreshLiveDiagnostics = false,
                    StaleAfter = TimeSpan.FromHours(1),
                    DeadAfter = TimeSpan.FromHours(1),
                });

            Assert.IsTrue(snapshot.ColdModules.Any(entry => entry.DisplayName == cold.ModuleName));
            Assert.IsFalse(snapshot.ColdModules.Any(entry => entry.DisplayName == healthy.ModuleName));
            StringAssert.Contains(snapshot.Summary, "Cold=1");
            StringAssert.Contains(snapshot.BuildDetails(), cold.ModuleName);
        }

        [TestMethod]
        public void Snapshot_Details_List_Ghost_Dead_And_Cold_Modules()
        {
            var audit = new AuditResult();
            audit.Unregistered.Add(new ModuleEntry { Name = "GhostAlpha", ModuleName = "GhostAlpha", Status = ModuleStatus.Discovered });
            audit.Dead.Add(new ModuleEntry { Name = "DeadBravo", ModuleName = "DeadBravo", Status = ModuleStatus.Registered });
            audit.EventLeaks.Add(new ModuleEntry { Name = "LeakCharlie", ModuleName = "LeakCharlie", Status = ModuleStatus.Registered });

            var snapshot = new ModuleRegistryHealthSnapshot(
                audit,
                new[]
                {
                    new ModuleEntry { Name = "ColdDelta", ModuleName = "ColdDelta", Status = ModuleStatus.Registered },
                });

            Assert.IsTrue(snapshot.HasProblems);
            StringAssert.Contains(snapshot.Summary, "Ghost=1");
            StringAssert.Contains(snapshot.Summary, "Dead=1");
            StringAssert.Contains(snapshot.Summary, "Cold=1");

            string details = snapshot.BuildDetails();
            StringAssert.Contains(details, "Ghost: GhostAlpha");
            StringAssert.Contains(details, "Dead: DeadBravo");
            StringAssert.Contains(details, "EventLeak: LeakCharlie");
            StringAssert.Contains(details, "Cold: ColdDelta");
        }

        private sealed class ColdBootTestModule : MilitiaModuleBase
        {
            public override string ModuleName => "ColdBootTestModule";
        }

        private sealed class HealthyBootTestModule : MilitiaModuleBase
        {
            public override string ModuleName => "HealthyBootTestModule";
        }
    }
}
