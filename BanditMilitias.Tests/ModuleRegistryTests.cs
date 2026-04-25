using BanditMilitias.Core.Components;
using BanditMilitias.Core.Registry;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

namespace BanditMilitias.Tests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class ModuleRegistryTests
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
        public void Discover_Is_Idempotent_And_Merges_Assemblies()
        {
            var registry = ModuleRegistry.Instance;

            registry.Discover(typeof(MilitiaModuleBase).Assembly);
            int firstCount = registry.All.Count;

            registry.Discover(typeof(MilitiaModuleBase).Assembly);
            int secondCount = registry.All.Count;

            registry.Discover(typeof(ModuleRegistryTests).Assembly);
            int mergedCount = registry.All.Count;

            Assert.AreEqual(firstCount, secondCount);
            Assert.IsTrue(mergedCount > secondCount);
            Assert.IsNotNull(registry.Get(typeof(AliasNamedTestModule)));
        }

        [TestMethod]
        public void Get_Resolves_Type_Name_And_Module_Alias()
        {
            var registry = ModuleRegistry.Instance;
            var module = new AliasNamedTestModule();

            registry.Discover(typeof(ModuleRegistryTests).Assembly);
            registry.Confirm(module);

            ModuleEntry? byTypeName = registry.Get(nameof(AliasNamedTestModule));
            ModuleEntry? byAlias = registry.Get(module.ModuleName);

            Assert.IsNotNull(byTypeName);
            Assert.IsNotNull(byAlias);
            Assert.AreEqual(nameof(AliasNamedTestModule), byTypeName!.Name);
            Assert.AreEqual(module.ModuleName, byAlias!.ModuleName);
            Assert.AreEqual(byTypeName.Name, byAlias.Name);
        }

        [TestMethod]
        public void Lifecycle_Status_Transitions_Are_Tracked()
        {
            var registry = ModuleRegistry.Instance;
            var module = new AliasNamedTestModule();

            registry.Discover(typeof(ModuleRegistryTests).Assembly);
            registry.Confirm(module);
            Assert.AreEqual(ModuleStatus.Registered, registry.Get(module.ModuleName)!.Status);

            registry.MarkFailed(module, "boom");
            ModuleEntry failed = registry.Get(module.ModuleName)!;
            Assert.AreEqual(ModuleStatus.Failed, failed.Status);
            Assert.AreEqual("boom", failed.FailReason);

            registry.MarkHealthy(module);
            ModuleEntry healthy = registry.Get(module.ModuleName)!;
            Assert.AreEqual(ModuleStatus.Registered, healthy.Status);
            Assert.IsNull(healthy.FailReason);

            registry.MarkRemoved(module);
            Assert.AreEqual(ModuleStatus.Removed, registry.Get(module.ModuleName)!.Status);
        }

        [TestMethod]
        public void Delegate_Subscriptions_Resolve_Closure_Owners_And_Reset_Counters()
        {
            var registry = ModuleRegistry.Instance;
            var module = new ClosureSubscriberModule();

            registry.Discover(typeof(ModuleRegistryTests).Assembly);
            registry.Confirm(module);

            Action<TestGameEvent> handler = module.CreateHandler();
            registry.RecordEventSubscription(handler, subscribed: true);

            ModuleEntry subscribed = registry.Get(module.ModuleName)!;
            Assert.AreEqual(1, subscribed.SubscribeCount);
            Assert.AreEqual(0, subscribed.UnsubscribeCount);
            Assert.IsTrue(subscribed.HasEventLeak);

            registry.ResetEventSubscriptions();

            ModuleEntry cleared = registry.Get(module.ModuleName)!;
            Assert.AreEqual(ModuleStatus.Registered, cleared.Status);
            Assert.AreEqual(0, cleared.SubscribeCount);
            Assert.AreEqual(0, cleared.UnsubscribeCount);
            Assert.IsFalse(cleared.HasEventLeak);
        }

        [TestMethod]
        public void Audit_TotalRegistered_Counts_All_NonDiscovered_States()
        {
            var registry = ModuleRegistry.Instance;

            var healthy = new AliasNamedTestModule();
            var disabled = new DisabledAliasNamedTestModule();
            var removed = new RemovedAliasNamedTestModule();
            var failed = new FailedAliasNamedTestModule();

            registry.Discover(typeof(ModuleRegistryTests).Assembly);
            registry.Confirm(healthy);
            registry.Confirm(disabled);
            registry.Confirm(removed);
            registry.Confirm(failed);
            registry.MarkRemoved(removed);
            registry.MarkFailed(failed, "sync");

            AuditResult audit = registry.Audit();

            Assert.IsTrue(audit.Unregistered.Any(entry => entry.Name == nameof(DiscoveredOnlyTestModule)));
            Assert.AreEqual(
                audit.Healthy.Count + audit.Disabled.Count + audit.Failed.Count + audit.Removed.Count,
                audit.TotalRegistered);
        }

        [TestMethod]
        public void Audit_Detects_Silent_Broken_Modules()
        {
            var registry = ModuleRegistry.Instance;
            var module = new SilentBrokenDiagnosticsTestModule();

            registry.Discover(typeof(ModuleRegistryTests).Assembly);
            registry.Confirm(module);
            registry.MarkHealthy(module, "Initialize");

            AuditResult audit = registry.Audit(new AuditOptions
            {
                RefreshLiveDiagnostics = true,
            });

            var matches = audit.SilentBroken.FindAll(entry => entry.Name == nameof(SilentBrokenDiagnosticsTestModule));
            Assert.AreEqual(1, matches.Count);

            ModuleEntry detected = matches.Single();
            StringAssert.Contains(detected.DiagnosticIssue ?? string.Empty, "Error:");
            Assert.IsTrue(audit.HasProblems);
        }

        [TestMethod]
        public void Audit_Ignores_Broken_ModuleName_Getters()
        {
            var registry = ModuleRegistry.Instance;
            var healthy = new AliasNamedTestModule();
            var broken = new ThrowingModuleNameTestModule();

            registry.Discover(typeof(ModuleRegistryTests).Assembly);
            registry.Confirm(healthy);
            registry.MarkHealthy(healthy, "Initialize");
            registry.Confirm(broken);
            registry.MarkHealthy(broken, "Initialize");

            Exception? ex = null;
            try
            {
                _ = registry.Audit(new AuditOptions
                {
                    RefreshLiveDiagnostics = true,
                });
            }
            catch (Exception caught)
            {
                ex = caught;
            }

            Assert.IsNull(ex);
            Assert.IsNotNull(registry.Get(nameof(ThrowingModuleNameTestModule)));
        }

        [TestMethod]
        public void Audit_Detects_Stale_Modules()
        {
            var registry = ModuleRegistry.Instance;
            var module = new StaleHeartbeatTestModule();

            registry.Discover(typeof(ModuleRegistryTests).Assembly);
            registry.Confirm(module);
            registry.MarkHealthy(module, "Initialize");

            ModuleEntry tracked = registry.Get(module.ModuleName)!;
            DateTime staleNow = tracked.LastActivityUtc!.Value.AddMinutes(31);

            AuditResult audit = registry.Audit(new AuditOptions
            {
                UtcNow = staleNow,
                StaleAfter = TimeSpan.FromMinutes(30),
                DeadAfter = TimeSpan.FromMinutes(10),
                RefreshLiveDiagnostics = false,
            });

            Assert.IsTrue(audit.Stale.Any(entry => entry.Name == nameof(StaleHeartbeatTestModule)));
            Assert.IsFalse(audit.Dead.Any(entry => entry.Name == nameof(StaleHeartbeatTestModule)));
        }

        [TestMethod]
        public void Audit_Detects_Dead_Modules()
        {
            var registry = ModuleRegistry.Instance;
            var module = new DeadRegisteredOnlyTestModule();

            registry.Discover(typeof(ModuleRegistryTests).Assembly);
            registry.Confirm(module);

            ModuleEntry tracked = registry.Get(module.ModuleName)!;
            DateTime deadNow = tracked.LastConfirmedUtc!.Value.AddMinutes(11);

            AuditResult audit = registry.Audit(new AuditOptions
            {
                UtcNow = deadNow,
                DeadAfter = TimeSpan.FromMinutes(10),
                StaleAfter = TimeSpan.FromMinutes(30),
                RefreshLiveDiagnostics = false,
            });

            Assert.IsTrue(audit.Dead.Any(entry => entry.Name == nameof(DeadRegisteredOnlyTestModule)));
            Assert.IsFalse(audit.Stale.Any(entry => entry.Name == nameof(DeadRegisteredOnlyTestModule)));
        }

        [TestMethod]
        public void GenerateReport_Lists_Silent_Stale_And_Dead_Findings()
        {
            var registry = ModuleRegistry.Instance;
            var silent = new SilentBrokenDiagnosticsTestModule();
            var stale = new StaleHeartbeatTestModule();
            var dead = new DeadRegisteredOnlyTestModule();

            registry.Discover(typeof(ModuleRegistryTests).Assembly);
            registry.Confirm(silent);
            registry.MarkHealthy(silent, "Initialize");
            registry.Confirm(stale);
            registry.MarkHealthy(stale, "Initialize");
            registry.Confirm(dead);

            ModuleEntry staleEntry = registry.Get(stale.ModuleName)!;
            DateTime reportNow = staleEntry.LastActivityUtc!.Value.AddMinutes(31);

            string report = registry.GenerateReport(new AuditOptions
            {
                UtcNow = reportNow,
                StaleAfter = TimeSpan.FromMinutes(30),
                DeadAfter = TimeSpan.FromMinutes(10),
                RefreshLiveDiagnostics = false,
            });

            StringAssert.Contains(report, "Silent     : 1");
            StringAssert.Contains(report, "Stale      :");
            StringAssert.Contains(report, "Dead       : 1");
            StringAssert.Contains(report, "Silent Broken Modules");
            StringAssert.Contains(report, "Stale Modules");
            StringAssert.Contains(report, "Dead Modules");
            StringAssert.Contains(report, "StaleHeartbeatTestModule");
            StringAssert.Contains(report, "DeadRegisteredOnlyTestModule");
        }

        private sealed class AliasNamedTestModule : MilitiaModuleBase
        {
            public override string ModuleName => "AliasNamedModule";
            public override int Priority => 25;
        }

        private sealed class DisabledAliasNamedTestModule : MilitiaModuleBase
        {
            public override string ModuleName => "DisabledAliasNamedModule";
            public override bool IsEnabled => false;
        }

        private sealed class RemovedAliasNamedTestModule : MilitiaModuleBase
        {
            public override string ModuleName => "RemovedAliasNamedModule";
        }

        private sealed class FailedAliasNamedTestModule : MilitiaModuleBase
        {
            public override string ModuleName => "FailedAliasNamedModule";
        }

        private sealed class DiscoveredOnlyTestModule : MilitiaModuleBase
        {
            public override string ModuleName => "DiscoveredOnlyTestModule";
        }

        private sealed class ClosureSubscriberModule : MilitiaModuleBase
        {
            public override string ModuleName => "ClosureSubscriberModule";

            public Action<TestGameEvent> CreateHandler()
            {
                int counter = 0;
                return evt =>
                {
                    counter++;
                    _ = $"{evt.GetDescription()}:{counter}:{ModuleName}";
                };
            }
        }

        private sealed class SilentBrokenDiagnosticsTestModule : MilitiaModuleBase
        {
            public override string ModuleName => "SilentBrokenDiagnosticsTestModule";
            public override string GetDiagnostics() => "Error: queue stalled without exception";
        }

        private sealed class StaleHeartbeatTestModule : MilitiaModuleBase
        {
            public override string ModuleName => "StaleHeartbeatTestModule";
            public override string GetDiagnostics() => "Heartbeat OK";
        }

        private sealed class DeadRegisteredOnlyTestModule : MilitiaModuleBase
        {
            public override string ModuleName => "DeadRegisteredOnlyTestModule";
            public override string GetDiagnostics() => "Registered only";
        }

        private sealed class ThrowingModuleNameTestModule : MilitiaModuleBase
        {
            public override string ModuleName => throw new NullReferenceException("boom");
            public override string GetDiagnostics() => "Diagnostics still reachable";
        }

        private sealed class TestGameEvent
        {
            public string GetDescription() => "test";
        }
    }
}
