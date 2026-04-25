using System.Collections.Generic;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class SpawnCleanupIntegrationWiringTests
    {
        [TestMethod]
        public void SpawningSystem_UsesDynamicMilitiaCap()
        {
            string content = TestSourceHelper.ReadProjectFile("Systems/Spawning/MilitiaSpawningSystem.cs");

            StringAssert.Contains(content, "CalculateOptimalMilitiaCount()");
            StringAssert.Contains(content, "CalculateDynamicMilitiaCap(currentCount, optimalCount, maxParties)");
            StringAssert.Contains(content, "currentCount >= dynamicCap",
                "DoSpawns must check the dynamic per-iteration cap.");
        }

        [TestMethod]
        public void CleanupSystem_DoesNotRepairMissingLeaderByDefault()
        {
            string content = TestSourceHelper.ReadProjectFile("Systems/Cleanup/PartyCleanupSystem.cs");

            StringAssert.Contains(content, "party.LeaderHero != null && !party.LeaderHero.IsAlive");
            Assert.IsFalse(content.Contains("party.LeaderHero == null || !party.LeaderHero.IsAlive"));
        }

        [TestMethod]
        public void ModuleManager_SyncData_ContinuesAfterSingleModuleFailure()
        {
            string content = TestSourceHelper.ReadProjectFile("Infrastructure/ModuleManager.cs");

            StringAssert.Contains(content, "foreach (var module in _modules)");
            Assert.IsFalse(content.Contains("SyncData aborted after"));
        }

        [TestMethod]
        public void ModuleManager_RejectsDuplicateModuleContracts()
        {
            string content = TestSourceHelper.ReadProjectFile("Infrastructure/ModuleManager.cs");

            StringAssert.Contains(content, "_modulesByType.ContainsKey(moduleType)");
            StringAssert.Contains(content, "_moduleTypesByName.TryGetValue(moduleName");
        }

        [TestMethod]
        public void ModuleManager_RefreshHideoutCache_RebuildsSpatialIndex()
        {
            string content = TestSourceHelper.ReadProjectFile("Infrastructure/ModuleManager.cs");

            StringAssert.Contains(content, "_spatialIndex.Clear();");
            StringAssert.Contains(content, "IndexSettlement(settlement);");
        }

        [TestMethod]
        public void CompatibilityLayer_UsesLazyReflectionCaches()
        {
            string content = TestSourceHelper.ReadProjectFile("Infrastructure/CompatibilityLayer.cs");

            StringAssert.Contains(content, "_getPartyPositionDelegate = new Lazy<Func<MobileParty, Vec2>?>");
            StringAssert.Contains(content, "_giveGoldMethod = new Lazy<MethodInfo?>");
        }

        [TestMethod]
        public void ModuleManager_ExposesFailedModuleDiagnosticsCommand()
        {
            string content = TestSourceHelper.ReadProjectFile("Infrastructure/ModuleManager.cs");

            StringAssert.Contains(content, "private readonly Dictionary<string, ModuleFailureRecord> _failureRecords");
            StringAssert.Contains(content, "RecordModuleFailure(moduleName, $\"{tickType}Tick\", ex, notifyUser: true);");
            StringAssert.Contains(content, "public string GetFailedModulesReport(int maxEntries = 25)");
            StringAssert.Contains(content, "CommandLineArgumentFunction(\"failed_modules\", \"militia\")");
        }

        [TestMethod]
        public void Settings_ProvidesValidationDiagnostics()
        {
            string content = TestSourceHelper.ReadProjectFile("Settings.cs");

            StringAssert.Contains(content, "public int ValidateAndClampSettingsWithDiagnostics(out string report)");
            StringAssert.Contains(content, "Settings auto-corrected");
            StringAssert.Contains(content, "if (WarlordFallbackDays < FamousBanditFallbackDays)");
        }

        [TestMethod]
        public void SurrenderPatch_ProvidesCompatibilityDiagnostics()
        {
            string content = TestSourceHelper.ReadProjectFile("Patches/SurrenderCrashPatch.cs");

            StringAssert.Contains(content, "internal static bool ReflectionAccessAvailable => _reflectionAvailable;");
            StringAssert.Contains(content, "internal static string GetDiagnostics()");
            StringAssert.Contains(content, "SurrenderFix: API=");
            StringAssert.Contains(content, "HasAgentCrashGuardCaptivityPatch() => false;");
            StringAssert.Contains(content, "HasAgentCrashGuardDestroyPartyPatch() => false;");
            StringAssert.Contains(content, "Standalone BanditMilitias debugging mode");
        }
    }
}
