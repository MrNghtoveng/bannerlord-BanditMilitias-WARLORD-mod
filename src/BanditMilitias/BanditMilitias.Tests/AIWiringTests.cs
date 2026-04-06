using TaleWorlds.CampaignSystem.Party;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class AIWiringTests
    {
        [TestMethod]
        public void CustomMilitiaAI_UsesComponentDecisionPipeline()
        {
            string content = TestSourceHelper.ReadProjectFile("Intelligence/AI/CustomMilitiaAI.cs");

            StringAssert.Contains(content, "_ = TryExecuteComponentDecision(party, component);");
            StringAssert.Contains(content, "TryApplyLearningHint(party, component)");
            StringAssert.Contains(content, "MilitiaActionExecutor.ExecutePatrol");
            StringAssert.Contains(content, "MilitiaDecider.DecisionResult decision = decider.GetBestDecision");
        }

        [TestMethod]
        public void SmartCache_IncludesGlobalPartyScan()
        {
            string content = TestSourceHelper.ReadProjectFile("Intelligence/AI/Components/DataCache.cs");

            StringAssert.Contains(content, "foreach (MobileParty p in Campaign.Current.MobileParties)");
            StringAssert.Contains(content, "var seen = new HashSet<MobileParty>(results);");
        }

        [TestMethod]
        public void SubModule_RegistersCriticalAISystems()
        {
            string content = TestSourceHelper.ReadProjectFile("SubModule.cs");

            StringAssert.Contains(content, "RegisterSafe(() => new Systems.Scheduling.AISchedulerSystem(), nameof(Systems.Scheduling.AISchedulerSystem), critical: true);");
            StringAssert.Contains(content, "RegisterSafe(() => Intelligence.Swarm.SwarmCoordinator.Instance, nameof(Intelligence.Swarm.SwarmCoordinator), critical: true);");
            StringAssert.Contains(content, "RegisterSafe(() => Intelligence.Strategic.BanditBrain.Instance, nameof(Intelligence.Strategic.BanditBrain), critical: true);");
            StringAssert.Contains(content, "RegisterSafe(() => Intelligence.ML.AILearningSystem.Instance, nameof(Intelligence.ML.AILearningSystem));");
        }

        [TestMethod]
        public void WarlordAndSpawningSystems_WiredThroughSpawnEvents()
        {
            string warlord = TestSourceHelper.ReadProjectFile("Intelligence/Strategic/WarlordSystem.cs");
            StringAssert.Contains(warlord, "EventBus.Instance.Subscribe<BanditMilitias.Core.Events.MilitiaSpawnedEvent>");
            StringAssert.Contains(warlord, "EventBus.Instance.Unsubscribe<BanditMilitias.Core.Events.MilitiaSpawnedEvent>");

            string spawning = TestSourceHelper.ReadProjectFile("Systems/Spawning/MilitiaSpawningSystem.cs");
            StringAssert.Contains(spawning, "EventBus.Instance.Get<BanditMilitias.Core.Events.MilitiaSpawnedEvent>");
            StringAssert.Contains(spawning, "EventBus.Instance.Publish(spawnEvt)");
        }

        [TestMethod]
        public void BanditBrain_ActivatesDuringCampaignBootstrapNotInitialize()
        {
            string brain = TestSourceHelper.ReadProjectFile("Intelligence/Strategic/BanditBrain.cs");

            StringAssert.Contains(brain, "public override void RegisterCampaignEvents()",
                "BanditBrain must activate through RegisterCampaignEvents so campaign session timing owns AI startup.");
            StringAssert.Contains(brain, "_currentState = BrainState.Dormant;",
                "BanditBrain.Initialize must leave the brain dormant until campaign bootstrap completes.");
            StringAssert.Contains(brain, "_currentState = BrainState.Active;",
                "BanditBrain must still transition to active state once campaign bootstrap completes.");
        }

        [TestMethod]
        public void MilitiaBanner_RenderPath_DoesNotTouchStrategicSystems()
        {
            string component = TestSourceHelper.ReadProjectFile("Components/MilitiaPartyComponent.cs");

            StringAssert.Contains(component, "SetBannerPrestigeLevel(",
                "MilitiaPartyComponent must read banner state from component-owned data.");
            Assert.IsTrue(component.IndexOf("WarlordSystem.Instance.GetWarlord", StringComparison.Ordinal) < 0,
                "Map banner rendering must not query WarlordSystem.");
            Assert.IsTrue(component.IndexOf("WarlordLegitimacySystem.Instance.GetLevel", StringComparison.Ordinal) < 0,
                "Map banner rendering must not query WarlordLegitimacySystem.");
        }

        [TestMethod]
        public void WarlordSystems_SynchronizeBannerPrestige_OutsideRenderPath()
        {
            string warlord = TestSourceHelper.ReadProjectFile("Intelligence/Strategic/WarlordSystem.cs");
            string progression = TestSourceHelper.ReadProjectFile("Systems/Progression/WarlordProgression.cs");

            StringAssert.Contains(warlord, "SynchronizeMilitiaBannerState(",
                "WarlordSystem must push banner state into militia components during command ownership changes.");
            StringAssert.Contains(progression, "SyncAllMilitiaBannerPrestige()",
                "LegitimacySystem must rebuild banner prestige after campaign data is loaded.");
            StringAssert.Contains(progression, "ApplyMilitiaBannerPrestige(warlord, newLevel);",
                "Legitimacy transitions must update militia banner state outside render-time.");
        }
    }
}
