using BanditMilitias.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class ModuleManagerRetryTests
    {
        [TestMethod]
        public void ModuleManager_RetryLogic_UsesFailureCountField()
        {
            string src = TestSourceHelper.ReadProjectFile("Infrastructure/ModuleManager.cs");
            StringAssert.Contains(src, "record.FailureCount",
                "Retry logic must consult FailureCount to implement exponential backoff.");
        }

        [TestMethod]
        public void ModuleManager_RetryLogic_HasDayCountTracking()
        {
            string src = TestSourceHelper.ReadProjectFile("Infrastructure/ModuleManager.cs");
            StringAssert.Contains(src, "_dayCount",
                "ModuleManager must track day count for interval-based retry.");
        }

        [TestMethod]
        public void ModuleManager_RetryLogic_HasWeeklyInterval()
        {
            string src = TestSourceHelper.ReadProjectFile("Infrastructure/ModuleManager.cs");
            StringAssert.Contains(src, "_dayCount % 7",
                "Retry logic must include a 7-day interval tier.");
        }

        [TestMethod]
        public void ModuleManager_RetryLogic_HasMonthlyInterval()
        {
            string src = TestSourceHelper.ReadProjectFile("Infrastructure/ModuleManager.cs");
            StringAssert.Contains(src, "_dayCount % 30",
                "Retry logic must include a 30-day interval for persistently failing modules.");
        }

        [TestMethod]
        public void ModuleManager_RetryLogic_DoesNotClearAllFailedModulesAtOnce()
        {
            string src = TestSourceHelper.ReadProjectFile("Infrastructure/ModuleManager.cs");
            int methodStart = src.IndexOf("private int ResetFailedModulesForRetry()");
            Assert.IsTrue(methodStart >= 0, "ResetFailedModulesForRetry method must exist.");

            int nextMethod = src.IndexOf("private bool IsModuleFailed", methodStart);
            Assert.IsTrue(nextMethod > methodStart, "Could not isolate ResetFailedModulesForRetry body.");

            string methodBody = src.Substring(methodStart, nextMethod - methodStart);
            Assert.IsFalse(methodBody.Contains("_failedModules.Clear()"),
                "Retry logic must not clear all failed modules in a single sweep.");
        }

        [TestMethod]
        public void SpawningSystem_NullHideoutGuard_IsFirstCheck()
        {
            string src = TestSourceHelper.ReadProjectFile("Systems/Spawning/MilitiaSpawningSystem.cs");

            int methodStart = src.IndexOf("public MobileParty? SpawnMilitia(Settlement hideout, bool force = false)");
            Assert.IsTrue(methodStart >= 0, "SpawnMilitia(force) method must exist.");

            int braceOpen = src.IndexOf('{', methodStart);
            string bodyStart = src.Substring(braceOpen + 1, 400);

            Assert.IsTrue(bodyStart.Contains("if (hideout == null)"),
                "The very first check in SpawnMilitia must guard against null hideout.");

            Assert.IsFalse(src.Contains("CRITICAL: Hideout null! Spawn iptal."),
                "The old duplicate null-check comment should have been removed.");
        }

        [TestMethod]
        public void MilitiaBehavior_OnSessionLaunched_ForceInitGlobals()
        {
            string src = TestSourceHelper.ReadProjectFile("Behaviors/MilitiaBehavior.cs");

            StringAssert.Contains(src, "Core.Config.Globals.Initialize(force: true);",
                "OnSessionLaunched must always call Globals.Initialize(force: true).");

            Assert.IsFalse(src.Contains("if (Core.Config.Globals.BasicInfantry.Count == 0)"),
                "The conditional guard before Globals.Initialize must have been removed.");
        }

        [TestMethod]
        public void MilitiaBehavior_OnPartyCreated_ChecksIsActiveBeforeRegister()
        {
            string src = TestSourceHelper.ReadProjectFile("Behaviors/MilitiaBehavior.cs");
            StringAssert.Contains(src, "party.IsActive",
                "OnPartyCreated must verify party.IsActive before registering to prevent race condition.");
        }

        [TestMethod]
        public void MilitiaBehavior_OnPartyCreated_ChecksPartyNotNull()
        {
            string src = TestSourceHelper.ReadProjectFile("Behaviors/MilitiaBehavior.cs");
            StringAssert.Contains(src, "party.Party != null",
                "OnPartyCreated must check party.Party != null before registering.");
        }

        [TestMethod]
        public void CompatibilityLayer_CreatePartySafe_AILockedDuringInit()
        {
            string src = TestSourceHelper.ReadProjectFile("Infrastructure/CompatibilityLayer.cs");

            StringAssert.Contains(src, "SetDoNotMakeNewDecisions(true)",
                "CreatePartySafe Init callback must lock AI (true) to prevent decisions before position is set.");

            int initStart = src.IndexOf("void Init(MobileParty p)");
            Assert.IsTrue(initStart >= 0, "CreatePartySafe Init callback must exist.");

            int initEnd = src.IndexOf("MobileParty? party = CreateParty", initStart);
            Assert.IsTrue(initEnd > initStart, "Could not isolate CreatePartySafe Init body.");

            string initBody = src.Substring(initStart, initEnd - initStart);
            Assert.IsFalse(initBody.Contains("SetDoNotMakeNewDecisions(false)"),
                "The old SetDoNotMakeNewDecisions(false) in Init must have been replaced.");
        }

        [TestMethod]
        public void CleanupSystem_OnPartyDestroyed_KillsCaptainHero()
        {
            string src = TestSourceHelper.ReadProjectFile("Systems/Cleanup/PartyCleanupSystem.cs");
            StringAssert.Contains(src, "KillCharacterAction.ApplyByRemove",
                "OnPartyDestroyedCleanup must kill the captain hero to prevent Hero.All memory leak.");
        }

        [TestMethod]
        public void CleanupSystem_HeroKill_ChecksOccupationAndClan()
        {
            string src = TestSourceHelper.ReadProjectFile("Systems/Cleanup/PartyCleanupSystem.cs");
            StringAssert.Contains(src, "Occupation.Bandit",
                "Hero kill must guard with Occupation.Bandit check to avoid killing non-mod heroes.");
            StringAssert.Contains(src, "IsBanditFaction",
                "Hero kill must guard with IsBanditFaction check.");
        }

        [TestMethod]
        public void SpawningSystem_DeadSpawnChanceConstant_IsRemoved()
        {
            string src = TestSourceHelper.ReadProjectFile("Systems/Spawning/MilitiaSpawningSystem.cs");
            Assert.IsFalse(src.Contains("Settings.Instance.SpawnChance"),
                "SpawningSystem should no longer read spawn chance from settings.");
            Assert.IsTrue(src.Contains("BaseDailySpawnChanceMin"),
                "SpawningSystem should define an internal base daily chance band.");
        }

        [TestMethod]
        public void SubModule_RegisterModules_UsesFactoryWrappedRegisterSafe()
        {
            string src = TestSourceHelper.ReadProjectFile("SubModule.cs");

            StringAssert.Contains(src,
                "void RegisterSafe(Func<BanditMilitias.Core.Components.IMilitiaModule?> moduleFactory, string moduleLabel, bool critical = false)",
                "RegisterModules must wrap singleton resolution in a factory so getter exceptions do not crash game start.");

            StringAssert.Contains(src,
                "RegisterSafe(() => Systems.AI.AdaptiveAIDoctrineSystem.Instance, nameof(Systems.AI.AdaptiveAIDoctrineSystem));",
                "AdaptiveAIDoctrineSystem registration must be factory-wrapped to catch early singleton resolution failures.");
        }

        [TestMethod]
        public void SubModule_RegisterModules_LogsModuleResolutionFailures()
        {
            string src = TestSourceHelper.ReadProjectFile("SubModule.cs");

            StringAssert.Contains(src, "Module resolution failed for",
                "RegisterModules must log module resolution failures explicitly so startup crashes can be diagnosed.");
        }
    }
}
