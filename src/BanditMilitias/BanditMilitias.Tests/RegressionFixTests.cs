using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class RegressionFixTests
    {
        [TestMethod]
        public void SpatialGridSystem_Must_Register_Singleton_Instance()
        {
            string subModule = TestSourceHelper.ReadProjectFile("SubModule.cs");

            StringAssert.Contains(subModule, "RegisterSafe(() => Systems.Grid.SpatialGridSystem.Instance, nameof(Systems.Grid.SpatialGridSystem), critical: true);");
            Assert.IsTrue(subModule.IndexOf("RegisterSafe(() => new Systems.Grid.SpatialGridSystem(), nameof(Systems.Grid.SpatialGridSystem), critical: true);", StringComparison.Ordinal) < 0);
        }

        [TestMethod]
        public void Crisis_Save_Types_Must_Be_Defined()
        {
            string definer = TestSourceHelper.ReadProjectFile("Infrastructure", "BanditMilitiasSaveDefiner.cs");

            StringAssert.Contains(definer, "AddClassDefinition(typeof(CrisisEvent), 27);");
            StringAssert.Contains(definer, "ConstructContainerDefinition(typeof(List<CrisisEvent>));");
            StringAssert.Contains(definer, "AddEnumDefinition(typeof(CrisisType), 132);");
            StringAssert.Contains(definer, "AddEnumDefinition(typeof(CrisisPhase), 133);");
        }

        [TestMethod]
        public void Registry_Must_Track_Event_Leaks_And_Removals()
        {
            string eventBus = TestSourceHelper.ReadProjectFile("Core", "Events", "EventBus.cs");
            string moduleManager = TestSourceHelper.ReadProjectFile("Infrastructure", "ModuleManager.cs");

            StringAssert.Contains(eventBus, "ModuleRegistry.Instance.RecordEventSubscription");
            StringAssert.Contains(moduleManager, "ModuleRegistry.Instance.MarkRemoved(module);");
        }

        [TestMethod]
        public void ThreatLevelChangedEvent_Reset_Must_Clear_Transient_State()
        {
            string events = TestSourceHelper.ReadProjectFile("Core", "Events", "Events.cs");

            StringAssert.Contains(events, "ThreatDelta    = 0f;");
            StringAssert.Contains(events, "ChangeTime     = CampaignTime.Zero;");
        }

        [TestMethod]
        public void CrisisStartedEvent_Reset_Must_Clear_CrisisType()
        {
            string events = TestSourceHelper.ReadProjectFile("Core", "Events", "Events.cs");

            StringAssert.Contains(events, "CrisisType = default;");
        }

        [TestMethod]
        public void FearSystem_Betrayal_Publish_Must_Fill_Threat_Metadata()
        {
            string fearSystem = TestSourceHelper.ReadProjectFile("Systems", "Fear", "FearSystem.cs");

            StringAssert.Contains(fearSystem, "threatEvt.ThreatDelta = threatEvt.NewThreatLevel - threatEvt.OldThreatLevel;");
            StringAssert.Contains(fearSystem, "threatEvt.ChangeTime = CampaignTime.Now;");
        }

        [TestMethod]
        public void EventBus_PublishDeferred_Must_Reject_Pooled_Events()
        {
            string eventBus = TestSourceHelper.ReadProjectFile("Core", "Events", "EventBus.cs");

            StringAssert.Contains(eventBus, "if (gameEvent is IPoolableEvent)");
            StringAssert.Contains(eventBus, "throw new InvalidOperationException(message);");
        }

        [TestMethod]
        public void SubModule_Must_Expose_IsDeferredInitDone()
        {
            string src = TestSourceHelper.ReadProjectFile("SubModule.cs");
            StringAssert.Contains(src, "public static bool IsDeferredInitDone =>");
        }

        [TestMethod]
        public void SubModule_Must_Expose_SetStateDormant_SetStateActive()
        {
            string src = TestSourceHelper.ReadProjectFile("SubModule.cs");
            StringAssert.Contains(src, "public static void SetStateDormant()");
            StringAssert.Contains(src, "public static void SetStateActive()");
        }

        [TestMethod]
        public void MilitiaBehavior_Must_Not_Use_Reflection_For_State_Transitions()
        {
            string src = TestSourceHelper.ReadProjectFile("Behaviors", "MilitiaBehavior.cs");
            Assert.IsTrue(src.IndexOf("new object[] { 3 }", StringComparison.Ordinal) < 0);
            Assert.IsTrue(src.IndexOf("new object[] { 4 }", StringComparison.Ordinal) < 0);
            Assert.IsTrue(src.IndexOf("\"TransitionToState\"", StringComparison.Ordinal) < 0);
        }

        [TestMethod]
        public void MilitiaBehavior_Must_Run_DeferredInit_In_Bootstrap()
        {
            string src = TestSourceHelper.ReadProjectFile("Behaviors", "MilitiaBehavior.cs");
            StringAssert.Contains(src, "SubModule.IsDeferredInitDone");
            StringAssert.Contains(src, "SubModule.RunDeferredSystemInit()");
            StringAssert.Contains(src, "SubModule.SetStateActive()");
        }

        [TestMethod]
        public void SpawningSystem_Must_Not_Have_Dead_SpawnedCount()
        {
            string src = TestSourceHelper.ReadProjectFile("Systems", "Spawning", "MilitiaSpawningSystem.cs");
            Assert.IsTrue(src.IndexOf("spawnedCount", StringComparison.Ordinal) < 0);
        }

        [TestMethod]
        public void SpawningSystem_Must_Use_ActivationDelayElapsedDays()
        {
            string src = TestSourceHelper.ReadProjectFile("Systems", "Spawning", "MilitiaSpawningSystem.cs");
            StringAssert.Contains(src, "GetActivationDelayElapsedDays()");
        }

        [TestMethod]
        public void CompatibilityLayer_ActivationDelay_Must_Use_InGame_Anchor_Time()
        {
            string src = TestSourceHelper.ReadProjectFile("Infrastructure", "CompatibilityLayer.cs");
            StringAssert.Contains(src, "ResolveActivationDelayAnchor()");
            StringAssert.Contains(src, "(CampaignTime.Now - anchor).ToDays");
            Assert.IsTrue(src.IndexOf("return (float)CampaignTime.Now.ToDays;", StringComparison.Ordinal) < 0);
        }

        [TestMethod]
        public void WarlordCombatSystem_Must_Only_Regen_Militia_Parties()
        {
            string src = TestSourceHelper.ReadProjectFile("Systems", "Combat", "Combat.cs");
            StringAssert.Contains(src, "party?.PartyComponent is not MilitiaPartyComponent");
            StringAssert.Contains(src, "TryResolveMobileParty(affectedAgent)");
        }

        [TestMethod]
        public void BanditBrain_Must_Publish_Command_Snapshots()
        {
            string src = TestSourceHelper.ReadProjectFile("Intelligence", "Strategic", "BanditBrain.cs");
            StringAssert.Contains(src, "evt.Command = CloneCommand(command);");
            StringAssert.Contains(src, "private static StrategicCommand CloneCommand");
            Assert.IsTrue(src.IndexOf("_commandPool.Return(command);", StringComparison.Ordinal) < 0);
        }

        [TestMethod]
        public void MilitiaEquipmentManager_Must_Not_Use_ThreadStatic_Global_Doctrine()
        {
            string src = TestSourceHelper.ReadProjectFile("Systems", "AI", "MilitiaEquipmentManager.cs");
            Assert.IsTrue(src.IndexOf("[ThreadStatic]", StringComparison.Ordinal) < 0);
            StringAssert.Contains(src, "_missionDoctrineByPartyId");
            StringAssert.Contains(src, "TryGetMissionEquipmentPolicy");
        }

        [TestMethod]
        public void AdaptiveDoctrineSystem_Must_Clear_Mission_Policies_After_Battle()
        {
            string src = TestSourceHelper.ReadProjectFile("Systems", "AI", "AdaptiveAIDoctrineSystem.cs");
            StringAssert.Contains(src, "MilitiaEquipmentManager.Instance.ClearMissionEquipmentPolicy");
            StringAssert.Contains(src, "MilitiaEquipmentManager.Instance.ResetMissionEquipmentPolicies();");
        }

        [TestMethod]
        public void MilitiaBehavior_LazyInit_Failure_Must_Trigger_Recovery_Path()
        {
            string src = TestSourceHelper.ReadProjectFile("Behaviors", "MilitiaBehavior.cs");
            StringAssert.Contains(src, "HandleLazyInitializationFailure(");
            StringAssert.Contains(src, "Infrastructure.HealthCheck.RunDiagnostics(autoFix: true);");
            StringAssert.Contains(src, "_sessionBootstrapPending = true;");
        }

        [TestMethod]
        public void MilitiaPartyComponent_Sleep_Must_Clamp_And_Expose_Overdue_State()
        {
            string src = TestSourceHelper.ReadProjectFile("Components", "MilitiaPartyComponent.cs");
            StringAssert.Contains(src, "if (clampedHours > 24f) clampedHours = 24f;");
            StringAssert.Contains(src, "public float GetSleepRemainingHours()");
            StringAssert.Contains(src, "public float GetSleepOverdueHours()");
        }

        [TestMethod]
        public void AiPatrolPatch_Must_Force_Overdue_Parties_To_Think()
        {
            string src = TestSourceHelper.ReadProjectFile("Patches", "AiPatrollingBehaviorPatch.cs");
            StringAssert.Contains(src, "component.GetSleepOverdueHours() >= 6f");
            StringAssert.Contains(src, "component.NextThinkTime == CampaignTime.Zero");
        }

        [TestMethod]
        public void AIScheduler_Zombie_Rescue_Must_Not_ReSleep_Overdue_Parties()
        {
            string src = TestSourceHelper.ReadProjectFile("Systems", "Scheduling", "AISchedulerSystem.cs");
            StringAssert.Contains(src, "comp.IsPriorityAIUpdate = true;");
            StringAssert.Contains(src, "EnqueueDecision(party, urgent: true);");
            Assert.IsTrue(src.IndexOf("comp.SleepFor(0.1f + TaleWorlds.Core.MBRandom.RandomFloat * 2.9f);", StringComparison.Ordinal) < 0);
        }

        [TestMethod]
        public void AiPatrolPatch_Must_Target_LandBandit_Behavior_Too()
        {
            string src = TestSourceHelper.ReadProjectFile("Patches", "AiPatrollingBehaviorPatch.cs");
            StringAssert.Contains(src, "AiLandBanditPatrollingBehavior");
            StringAssert.Contains(src, "[HarmonyTargetMethods]");
        }

        [TestMethod]
        public void BanditAiPatch_Must_Support_Modern_Target_Methods()
        {
            string src = TestSourceHelper.ReadProjectFile("Patches", "BanditAiPatch.cs");
            StringAssert.Contains(src, "AiHourlyTick");
            StringAssert.Contains(src, "HourlyTick");
            StringAssert.Contains(src, "[HarmonyArgument(0)] MobileParty banditParty");
        }

        [TestMethod]
        public void CleanupSystem_Must_Drain_Queue_Even_Without_Aggressive_Mode()
        {
            string src = TestSourceHelper.ReadProjectFile("Systems", "Cleanup", "PartyCleanupSystem.cs");
            StringAssert.Contains(src, "ExecuteInternalDestruction();");
            Assert.IsTrue(src.IndexOf("if (Settings.Instance?.EnableAggressiveCleanup != true) return;", StringComparison.Ordinal) < 0);
        }

        [TestMethod]
        public void CleanupSystem_GlobalThinning_Must_Prioritize_Zombie_And_Headless_Parties()
        {
            string src = TestSourceHelper.ReadProjectFile("Systems", "Cleanup", "PartyCleanupSystem.cs");
            StringAssert.Contains(src, "bool isZombie = party.MemberRoster == null || party.MemberRoster.TotalManCount <= 0;");
            StringAssert.Contains(src, "bool isHeadless = party.ActualClan == null && party.LeaderHero == null;");
            StringAssert.Contains(src, "GlobalThinning_Zombie");
        }

        [TestMethod]
        public void BanditBrain_Must_Persist_Threat_Map_Snapshot()
        {
            string src = TestSourceHelper.ReadProjectFile("Intelligence", "Strategic", "BanditBrain.cs");
            StringAssert.Contains(src, "_ = dataStore.SyncData(\"_threatMapSnapshot\", ref threatSnapshot);");
            StringAssert.Contains(src, "_threatMap.Clear();");
            StringAssert.Contains(src, "_threatMap[settlement] = item;");
        }

        [TestMethod]
        public void SaveDefiner_Must_Define_ThreatAssessment_For_SaveV4()
        {
            string src = TestSourceHelper.ReadProjectFile("Infrastructure", "BanditMilitiasSaveDefiner.cs");
            StringAssert.Contains(src, "public const int SAVE_VERSION = 4;");
            StringAssert.Contains(src, "AddClassDefinition(typeof(ThreatAssessment), 19);");
            StringAssert.Contains(src, "ConstructContainerDefinition(typeof(List<ThreatAssessment>));");
        }

        [TestMethod]
        public void SubModule_Must_Have_Live_EmergencyStop_Path()
        {
            string src = TestSourceHelper.ReadProjectFile("SubModule.cs");
            StringAssert.Contains(src, "private static void EnterEmergencyStop");
            StringAssert.Contains(src, "TransitionToState(ModState.EmergencyStop);");
            StringAssert.Contains(src, "MAX_TICK_ERRORS_FOR_EMERGENCY");
        }

        [TestMethod]
        public void SubModule_DeferredInit_Repeated_Failures_Must_Trigger_EmergencyStop()
        {
            string src = TestSourceHelper.ReadProjectFile("SubModule.cs");
            StringAssert.Contains(src, "_deferredInitFailureCount++");
            StringAssert.Contains(src, "MAX_DEFERRED_INIT_FAILURES");
            StringAssert.Contains(src, "Deferred system init failed");
        }

        [TestMethod]
        public void Diagnostics_FullSim_Report_Must_Include_World_Party_Health()
        {
            string src = TestSourceHelper.ReadProjectFile("Systems", "Diagnostics", "DiagnosticsSystem.cs");
            StringAssert.Contains(src, "--- World Party Health ---");
            StringAssert.Contains(src, "Zombie Parties");
            StringAssert.Contains(src, "Headless Parties");
            StringAssert.Contains(src, "Health Alert");
        }

        [TestMethod]
        public void MilitiaDecider_Must_Gate_Swarm_Override_With_Context()
        {
            string src = TestSourceHelper.ReadProjectFile("Intelligence", "AI", "Components", "MilitiaDecider.cs");
            StringAssert.Contains(src, "ShouldApplySwarmOverride(");
            StringAssert.Contains(src, "source = \"SwarmBypass\";");
            StringAssert.Contains(src, "if (order.Tactic == SwarmTactic.Retreat)");
        }

        [TestMethod]
        public void MilitiaDecider_Weak_Fallback_Must_Try_Merge_Or_Recruit_Before_Flee()
        {
            string src = TestSourceHelper.ReadProjectFile("Intelligence", "AI", "Components", "MilitiaDecider.cs");
            StringAssert.Contains(src, "SwarmCoalescence:PanicMerge");
            StringAssert.Contains(src, "EmergencyRecruit");
            StringAssert.Contains(src, "TryGetMergeDecisionForWeakParty");
            StringAssert.Contains(src, "IncubationMode:RetreatToSafety");
        }

        [TestMethod]
        public void SwarmCoordinator_Must_Derive_Order_Priority_From_Cohesion()
        {
            string src = TestSourceHelper.ReadProjectFile("Intelligence", "Swarm", "SwarmCoordinator.cs");
            StringAssert.Contains(src, "ComputeOrderPriority");
            StringAssert.Contains(src, "group.CohesionScore < 0.35f");
            StringAssert.Contains(src, "IsOffensiveTactic");
        }

        [TestMethod]
        public void PatrolDetection_Must_Not_Keep_Dead_Manual_Patrol_Cache()
        {
            string src = TestSourceHelper.ReadProjectFile("Intelligence", "AI", "PatrolDetection.cs");
            Assert.IsTrue(src.IndexOf("_cachedPatrols", StringComparison.Ordinal) < 0);
            StringAssert.Contains(src, "private static List<MobileParty> NearbyBuffer");
            StringAssert.Contains(src, "SpatialGridSystem authoritative source; no manual patrol cache to refresh.");
        }

        [TestMethod]
        public void MilitiaBehavior_Save_Version_Must_Follow_SaveDefiner()
        {
            string src = TestSourceHelper.ReadProjectFile("Behaviors", "MilitiaBehavior.cs");
            StringAssert.Contains(src, "private const int CurrentSaveVersion = Infrastructure.BanditMilitiasSaveDefiner.SAVE_VERSION;");
            Assert.IsTrue(src.IndexOf("private const int CurrentSaveVersion = 1;", StringComparison.Ordinal) < 0);
        }

        [TestMethod]
        public void MilitiaIntelLayer_Must_Keep_A_Single_Active_Instance()
        {
            string src = TestSourceHelper.ReadProjectFile("GUI", "GauntletUI", "MilitiaIntelLayer.cs");
            StringAssert.Contains(src, "private static MilitiaIntelLayer? _activeInstance;");
            StringAssert.Contains(src, "_activeInstance.Close();");
            StringAssert.Contains(src, "_activeInstance = this;");
        }

        [TestMethod]
        public void DiagnosticsSystem_Must_Not_Register_Duplicate_FailedModules_Or_FullSim_Commands()
        {
            string src = TestSourceHelper.ReadProjectFile("Systems", "Diagnostics", "DiagnosticsSystem.cs");

            Assert.IsTrue(src.IndexOf("CommandLineArgumentFunction(\"failed_modules\", \"militia\")", StringComparison.Ordinal) < 0);
            Assert.IsTrue(src.IndexOf("CommandLineArgumentFunction(\"full_sim_test\", \"militia\")", StringComparison.Ordinal) < 0);
            StringAssert.Contains(src, "CommandLineArgumentFunction(\"full_sim_report\", \"militia\")");
        }

        [TestMethod]
        public void DevCollector_Must_Not_Write_Redundant_FullSimLatest_Text_Report()
        {
            string src = TestSourceHelper.ReadProjectFile("Systems", "Dev", "DevDataCollector.cs");

            Assert.IsTrue(src.IndexOf("full_sim_latest.txt", StringComparison.Ordinal) < 0);
            Assert.IsTrue(src.IndexOf("_fullSimLatestFile", StringComparison.Ordinal) < 0);
            StringAssert.Contains(src, "_fullSimRunLog");
            StringAssert.Contains(src, "session_summary.txt");
        }

        [TestMethod]
        public void HTNPlanner_Must_Honor_Primitive_Task_Preconditions()
        {
            string src = TestSourceHelper.ReadProjectFile("Intelligence", "Tactical", "HTNCore.cs");

            StringAssert.Contains(src, "candidate.CheckPreconditions(state)");
            StringAssert.Contains(src, "continue;");
        }

        [TestMethod]
        public void Tactical_Doctrines_Must_Use_Previously_Dead_Primitives()
        {
            string src = TestSourceHelper.ReadProjectFile("Intelligence", "Tactical", "TacticalDoctrines.cs");

            StringAssert.Contains(src, "state.GetBool(\"IsTuranDoctrine\")");
            StringAssert.Contains(src, "new TuranManeuverTask()");
            StringAssert.Contains(src, "new DeepShieldWallTask()");
        }

        [TestMethod]
        public void Tactical_Mission_Behavior_Must_Prime_World_State_Before_Planning()
        {
            string src = TestSourceHelper.ReadProjectFile("Intelligence", "Tactical", "WarlordTacticalMissionBehavior.cs");

            StringAssert.Contains(src, "_worldState.SetFloat(\"ClosestEnemyDistance\", 9999f);");
            StringAssert.Contains(src, "_worldState.SetBool(\"IsMeleeHeavy\", IsMeleeHeavyTeam(warlordTeam));");
            StringAssert.Contains(src, "_worldState.SetBool(\"IsMeleeHeavy\", meleeUnits >= rangedUnits);");
        }
    }
}
