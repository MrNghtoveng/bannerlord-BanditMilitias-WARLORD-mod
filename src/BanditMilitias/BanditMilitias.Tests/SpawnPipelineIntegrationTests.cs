using TaleWorlds.CampaignSystem.Settlements;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace BanditMilitias.Tests
{

    [TestClass]
    public class SpawnPipelineIntegrationTests
    {
        private static string Read(string relativePath)
            => TestSourceHelper.ReadProjectFile(relativePath);

        [TestMethod]
        public void Stage1_SubModule_RegistersSpawningSystem()
        {
            string src = Read("SubModule.cs");
            StringAssert.Contains(src, "new Systems.Spawning.MilitiaSpawningSystem()",
                "SubModule must register MilitiaSpawningSystem with ModuleManager.");
        }

        [TestMethod]
        public void Stage1_SubModule_RegistersMilitiaBehavior()
        {
            string src = Read("SubModule.cs");
            StringAssert.Contains(src, "new Behaviors.MilitiaBehavior()",
                "SubModule must add MilitiaBehavior as a CampaignBehavior so daily ticks reach SpawningSystem.");
            StringAssert.Contains(src, "RegisterCampaignBehaviors(gameStarter)",
                "SubModule must always route behavior registration through the shared helper for Campaign and Sandbox starters.");
        }

        [TestMethod]
        public void Stage1_SubModule_CallsModuleManagerInitializeAll()
        {
            string src = Read("SubModule.cs");
            StringAssert.Contains(src, "Infrastructure.ModuleManager.Instance.InitializeAll()",
                "SubModule must call ModuleManager.InitializeAll() so all modules are started.");
        }

        [TestMethod]
        public void Stage1_SubModule_ClearsEventBusBeforeRegisteringModules()
        {
            string src = Read("SubModule.cs");
            int clearIndex = src.IndexOf("Core.Events.EventBus.Instance.Clear();", StringComparison.Ordinal);
            int registerIndex = src.IndexOf("RegisterModules();", StringComparison.Ordinal);

            Assert.IsTrue(clearIndex >= 0 && registerIndex >= 0 && clearIndex < registerIndex,
                "SubModule must clear EventBus before module registration so singleton resolution cannot leave stale subscriptions behind.");
        }

        [TestMethod]
        public void Stage1_SubModule_ValidatesSettingsBeforeInit()
        {
            string src = Read("SubModule.cs");
            StringAssert.Contains(src, "Settings.Instance?.ValidateAndClampSettings()",
                "SubModule must validate settings before initializing infrastructure.");
            StringAssert.Contains(src, "ConfigureTestMode()",
                "SubModule must configure test mode early in the init pipeline.");
        }

        [TestMethod]
        public void Stage2_MilitiaBehavior_RegistersDailyTickEvent()
        {
            string src = Read("Behaviors/MilitiaBehavior.cs");
            StringAssert.Contains(src, "CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick)",
                "MilitiaBehavior must subscribe to DailyTickEvent to drive the spawn system.");
        }

        [TestMethod]
        public void Stage2_MilitiaBehavior_OnDailyTick_CallsModuleManager()
        {
            string src = Read("Behaviors/MilitiaBehavior.cs");
            StringAssert.Contains(src, "Infrastructure.ModuleManager.Instance.OnDailyTick()",
                "MilitiaBehavior.OnDailyTick must forward the tick to ModuleManager so all modules tick.");
        }

        [TestMethod]
        public void Stage2_MilitiaBehavior_RegistersSessionLaunchedEvent()
        {
            string src = Read("Behaviors/MilitiaBehavior.cs");
            StringAssert.Contains(src, "CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched)",
                "MilitiaBehavior must listen for OnSessionLaunched to rebuild caches after world is loaded.");
        }

        [TestMethod]
        public void Stage2_MilitiaBehavior_OnSessionLaunched_RebuildsCache()
        {
            string src = Read("Behaviors/MilitiaBehavior.cs");
            StringAssert.Contains(src, "moduleManager.RebuildCaches()",
                "OnSessionLaunched must call RebuildCaches() to populate the hideout list.");
        }

        [TestMethod]
        public void Stage2_MilitiaBehavior_OnSessionLaunched_IsCrashSafe()
        {
            string src = Read("Behaviors/MilitiaBehavior.cs");
            StringAssert.Contains(src, "OnSessionLaunched failed:",
                "MilitiaBehavior.OnSessionLaunched must guard session bootstrap failures instead of crashing the load.");
            StringAssert.Contains(src, "Initial cache rebuild failed:",
                "MilitiaBehavior.OnSessionLaunched must isolate cache rebuild failures.");
            StringAssert.Contains(src, "Session bootstrap completion failed:",
                "MilitiaBehavior.OnSessionLaunched must isolate post-session bootstrap failures.");
        }

        [TestMethod]
        public void Stage2_MilitiaBehavior_CompletesSessionBootstrapThroughModuleManager()
        {
            string src = Read("Behaviors/MilitiaBehavior.cs");
            StringAssert.Contains(src, "moduleManager.CompleteSessionBootstrap()",
                "MilitiaBehavior must finalize post-session startup through ModuleManager so bootstrap ownership stays centralized.");
            Assert.IsTrue(src.IndexOf("BanditBrain.Instance.Initialize()", StringComparison.Ordinal) < 0,
                "MilitiaBehavior must not directly initialize BanditBrain during session launch.");
        }

        [TestMethod]
        public void Stage2_MilitiaBehavior_DefersSessionBootstrapUntilActivationDelayExpires()
        {
            string src = Read("Behaviors/MilitiaBehavior.cs");
            StringAssert.Contains(src, "_sessionBootstrapPending",
                "MilitiaBehavior must track delayed bootstrap work until the activation window opens.");
            StringAssert.Contains(src, "TryCompleteSessionBootstrapIfReady();",
                "MilitiaBehavior must keep checking delayed activation on campaign ticks after session launch.");
        }

        [TestMethod]
        public void Stage2_MilitiaDiplomacy_OnSessionLaunched_IsCrashSafe()
        {
            string src = Read("Behaviors/MilitiaDiplomacyCampaignBehavior.cs");
            StringAssert.Contains(src, "OnSessionLaunched failed:",
                "MilitiaDiplomacyCampaignBehavior.OnSessionLaunched must not crash the campaign load when dialog registration fails.");
            StringAssert.Contains(src, "UnregisterEvents();",
                "MilitiaDiplomacyCampaignBehavior must unregister old listeners before re-registering to avoid duplicate dialog/session hooks.");
            StringAssert.Contains(src, "Infrastructure.MbEventExtensions.RemoveListenerSafe",
                "MilitiaDiplomacyCampaignBehavior should remove only its own listeners instead of clearing whole event buses.");
        }

        [TestMethod]
        public void Stage2_StaticDataCache_OnSessionLaunched_IsCrashSafe()
        {
            string src = Read("Intelligence/AI/Components/DataCache.cs");
            StringAssert.Contains(src, "SafeRefreshCacheOnSessionLaunched",
                "StaticDataCache must route session launch refresh through a crash-safe wrapper.");
            StringAssert.Contains(src, "OnSessionLaunched cache refresh failed:",
                "StaticDataCache session refresh must log and degrade instead of crashing the load.");
        }

        [TestMethod]
        public void Stage2_MilitiaBehavior_UsesGameplayActivationSwitch()
        {
            string src = Read("Behaviors/MilitiaBehavior.cs");
            StringAssert.Contains(src, "CompatibilityLayer.IsGameplayActivationSwitchClosed()",
                "MilitiaBehavior must drive delayed startup through the shared gameplay activation switch.");
        }

        [TestMethod]
        public void Stage3_ModuleManager_OnDailyTick_CallsProcessModuleTicks()
        {
            string src = Read("Infrastructure/ModuleManager.cs");
            StringAssert.Contains(src, "ProcessModuleTicks(m => m.OnDailyTick(), \"Daily\")",
                "ModuleManager.OnDailyTick must call ProcessModuleTicks to dispatch to all modules.");
        }

        [TestMethod]
        public void Stage3_ModuleManager_ValidatesRegistryDaily()
        {
            string src = Read("Infrastructure/ModuleManager.cs");
            StringAssert.Contains(src, "ValidateRegistry()",
                "ModuleManager.OnDailyTick must call ValidateRegistry() to prune dead parties.");
        }

        [TestMethod]
        public void Stage3_ModuleManager_SkipsFailedModulesInTick()
        {
            string src = Read("Infrastructure/ModuleManager.cs");
            StringAssert.Contains(src, "if (!module.IsCritical && IsModuleFailed(moduleName)) continue;",
                "ProcessModuleTicks must skip failed non-critical modules to prevent crash loops.");
        }

        [TestMethod]
        public void Stage3_ModuleManager_UsesGameplayActivationSwitch()
        {
            string src = Read("Infrastructure/ModuleManager.cs");
            StringAssert.Contains(src, "CompatibilityLayer.IsGameplayActivationSwitchClosed()",
                "ModuleManager must use the shared gameplay activation switch before dispatching module ticks.");
        }

        [TestMethod]
        public void Stage3_ModuleManager_TracksSessionBootstrapLifecycle()
        {
            string src = Read("Infrastructure/ModuleManager.cs");
            StringAssert.Contains(src, "_campaignEventsRegistered",
                "ModuleManager must track whether campaign event wiring has already been applied.");
            StringAssert.Contains(src, "_sessionBootstrapComplete",
                "ModuleManager must track when session bootstrap is complete before ticking modules.");
            StringAssert.Contains(src, "public void CompleteSessionBootstrap()",
                "ModuleManager must expose a dedicated session bootstrap completion entry point.");
        }

        [TestMethod]
        public void Stage3_CompatibilityLayer_StartsActivationDelayOnMapState()
        {
            string src = Read("Infrastructure/CompatibilityLayer.cs");
            StringAssert.Contains(src, "TryStartActivationDelayClock()",
                "CompatibilityLayer must expose a shared activation-delay clock.");
            StringAssert.Contains(src, "ActiveState is TaleWorlds.CampaignSystem.GameState.MapState",
                "Activation-delay clock must start when the campaign map becomes active.");
        }

        [TestMethod]
        public void Stage3_CompatibilityLayer_ExposesGameplayActivationGate()
        {
            string src = Read("Infrastructure/CompatibilityLayer.cs");
            StringAssert.Contains(src, "public static bool IsGameplayActivationSwitchClosed()",
                "CompatibilityLayer must expose a central gameplay switch so all startup gating runs through one circuit.");
            StringAssert.Contains(src, "public static bool TryCloseGameplayActivationSwitch()",
                "CompatibilityLayer must own the moment when the activation switch closes and gameplay current starts.");
            StringAssert.Contains(src, "public static bool IsGameplayActivationDelayed()",
                "CompatibilityLayer must expose a shared gameplay gate so event-driven systems honor the same startup delay.");
            StringAssert.Contains(src, "return !IsGameplayActivationSwitchClosed();",
                "GameplayActivationDelayed must delegate to the central activation switch.");
        }

        [TestMethod]
        public void Stage4_SpawningSystem_DelegatesActivationCheckToModuleManager()
        {
            string src = Read("Systems/Spawning/MilitiaSpawningSystem.cs");
            Assert.IsFalse(src.Contains("if (!CompatibilityLayer.IsGameplayActivationSwitchClosed())"),
                "SpawningSystem should delegate activation gating to ModuleManager to avoid redundant checks.");
        }

        [TestMethod]
        public void Stage4_SpawningSystem_ChecksIsEnabled()
        {
            string src = Read("Systems/Spawning/MilitiaSpawningSystem.cs");
            StringAssert.Contains(src, "if (!IsEnabled) return;",
                "SpawningSystem.OnDailyTick must bail out early when disabled.");
        }

        [TestMethod]
        public void Stage4_SpawningSystem_ChecksMaxPartyCount()
        {
            string src = Read("Systems/Spawning/MilitiaSpawningSystem.cs");
            StringAssert.Contains(src, "int dynamicCap = CalculateDynamicMilitiaCap(currentCount, optimalCount, maxParties);",
                "SpawningSystem must derive a dynamic militia cap instead of locking to a flat optimal+buffer formula.");
            StringAssert.Contains(src, "if (currentCount >= dynamicCap)",
                "SpawningSystem must stop spawning when the current population reaches the dynamic cap.");
        }

        [TestMethod]
        public void Stage4_SpawningSystem_UsesHideoutCache()
        {
            string src = Read("Systems/Spawning/MilitiaSpawningSystem.cs");
            StringAssert.Contains(src, "ModuleManager.Instance.HideoutCache",
                "SpawningSystem must use the cached hideout list, not Settlement.All (performance).");
        }

        [TestMethod]
        public void Stage4_SpawningSystem_RebuildsCacheWhenEmpty()
        {
            string src = Read("Systems/Spawning/MilitiaSpawningSystem.cs");
            StringAssert.Contains(src, "ModuleManager.Instance.RebuildCaches()",
                "SpawningSystem must trigger a cache rebuild if hideouts are empty (init timing fix).");
        }

        [TestMethod]
        public void Stage5_SpawnMilitia_ChecksGlobalsBasicInfantry()
        {
            string src = Read("Systems/Spawning/MilitiaSpawningSystem.cs");
            StringAssert.Contains(src, "Globals.BasicInfantry.Count == 0",
                "SpawnMilitia must abort early if no bandit troop types are loaded.");
        }

        [TestMethod]
        public void Stage5_SpawnMilitia_ValidatesNaNGatePosition()
        {
            string src = Read("Systems/Spawning/MilitiaSpawningSystem.cs");
            StringAssert.Contains(src, "float.IsNaN(gatePos.X)",
                "SpawnMilitia must guard against NaN gate positions.");
        }

        [TestMethod]
        public void Stage5_SpawnMilitia_ValidatesNavMesh()
        {
            string src = Read("Systems/Spawning/MilitiaSpawningSystem.cs");
            StringAssert.Contains(src, "GetAccessiblePointNearPosition",
                "SpawnMilitia must probe the NavMesh before spawning.");
            StringAssert.Contains(src, "GetAccessiblePointNearPosition(gatePos, searchRadius)",
                "SpawnMilitia must use the repaired gatePos when scanning for a valid spawn position.");
        }

        [TestMethod]
        public void Stage5_SpawnMilitia_DoesNotBlacklistProblematicHideouts()
        {
            string src = Read("Systems/Spawning/MilitiaSpawningSystem.cs");
            Assert.IsFalse(src.Contains("BlacklistHideout("),
                "SpawnMilitia should not blacklist hideouts; failed hideouts must be retried on later ticks.");
        }

        [TestMethod]
        public void Stage5_SpawningSystem_DoesNotSkipInactiveHideoutsBeforeSpawnAttempt()
        {
            string src = Read("Systems/Spawning/MilitiaSpawningSystem.cs");
            Assert.IsFalse(src.Contains("if (!settlement.IsActive) continue;"),
                "OnDailyTick should not discard inactive hideouts before SpawnMilitia has a chance to reactivate them.");
        }

        [TestMethod]
        public void Stage5_SpawnMilitia_ValidatesPartyAfterCreation()
        {
            string src = Read("Systems/Spawning/MilitiaSpawningSystem.cs");
            StringAssert.Contains(src, "party.ActualClan == null || party.MapFaction == null",
                "SpawnMilitia must validate party after creation to catch race conditions.");
        }

        [TestMethod]
        public void Stage5_SpawnMilitia_UnlocksAIAfterPositionSet()
        {
            string src = Read("Systems/Spawning/MilitiaSpawningSystem.cs");
            StringAssert.Contains(src, "party.Ai.SetDoNotMakeNewDecisions(false)",
                "SpawnMilitia must unlock AI *after* InitializeMobilePartyAtPosition.");
        }

        [TestMethod]
        public void Stage6_MilitiaBehavior_OnPartyCreated_RegistersInRegistry()
        {
            string src = Read("Behaviors/MilitiaBehavior.cs");
            StringAssert.Contains(src, "ModuleManager.Instance.RegisterMilitia(party)",
                "OnPartyCreated must register the party in the militia registry.");
        }

        [TestMethod]
        public void Stage6_MilitiaBehavior_OnPartyDestroyed_UnregistersFromRegistry()
        {
            string src = Read("Behaviors/MilitiaBehavior.cs");
            StringAssert.Contains(src, "ModuleManager.Instance.UnregisterMilitia(party)",
                "OnPartyDestroyed must remove the party from the militia registry.");
        }

        [TestMethod]
        public void Stage6_CleanupSystem_OnPartyDestroyed_KillsCaptainHero()
        {
            string src = Read("Systems/Cleanup/PartyCleanupSystem.cs");
            StringAssert.Contains(src, "KillCharacterAction.ApplyByRemove",
                "When a militia party dies, its captain hero must be killed to prevent Hero.All leak.");
        }

        [TestMethod]
        public void Globals_Initialize_HasFallbackInitialization()
        {
            string src = Read("Core/Config/Constants.cs");
            StringAssert.Contains(src, "TryFallbackInitialization()",
                "Globals.Initialize must attempt a fallback initialization when core data is not ready.");
            StringAssert.Contains(src, "StringId.IndexOf(\"looter\"",
                "Globals.Initialize must attempt looter-based fallback when no infantry is found.");
        }

        [TestMethod]
        public void Globals_Initialize_AtomicSwap()
        {
            string src = Read("Core/Config/Constants.cs");
            StringAssert.Contains(src, "BasicInfantry = infantry;",
                "Globals must atomically swap the list to prevent readers seeing a partial list.");
        }

        [TestMethod]
        public void Globals_Initialize_CanForceReinit()
        {
            string src = Read("Core/Config/Constants.cs");
            StringAssert.Contains(src, "public static void Initialize(bool force = false)",
                "Globals.Initialize must accept a force parameter for OnSessionLaunched.");
        }
    }
}
