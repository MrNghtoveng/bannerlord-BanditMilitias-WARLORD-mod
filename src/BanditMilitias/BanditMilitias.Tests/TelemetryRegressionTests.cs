using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class TelemetryRegressionTests
    {
        [TestMethod]
        public void Battle_Action_Attribution_Must_Use_SmartCache_And_Tracked_Action()
        {
            string decider = TestSourceHelper.ReadProjectFile("Intelligence", "AI", "Components", "MilitiaDecider.cs");
            string mlSystem = TestSourceHelper.ReadProjectFile("Intelligence", "ML", "AILearningSystem.cs");

            StringAssert.Contains(decider, "MilitiaSmartCache.Instance.CacheDecision(");
            StringAssert.Contains(mlSystem, "private static AIAction ResolveTrackedAction(MobileParty party)");
            StringAssert.Contains(mlSystem, "MilitiaSmartCache.Instance.TryGetDecision(party, 0f, out var cached)");
            StringAssert.Contains(mlSystem, "ResolveTrackedAction(militia)");
            StringAssert.Contains(mlSystem, "snapshot.ActionTaken");
        }

        [TestMethod]
        public void Battle_Telemetry_Must_Use_PreBattle_Snapshots_And_Shared_Reward()
        {
            string safeTelemetry = TestSourceHelper.ReadProjectFile("Infrastructure", "SafeTelemetry.cs");
            string mlSystem = TestSourceHelper.ReadProjectFile("Intelligence", "ML", "AILearningSystem.cs");
            string devCollector = TestSourceHelper.ReadProjectFile("Systems", "Dev", "DevDataCollector.cs");

            StringAssert.Contains(safeTelemetry, "double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _)");
            StringAssert.Contains(mlSystem, "bool hadEnemy");
            StringAssert.Contains(devCollector, "CampaignEvents.MapEventStarted.AddNonSerializedListener(this, OnMapEventStarted);");
            StringAssert.Contains(devCollector, "_battleSnapshots.TryGetValue(militia.StringId, out var snapshot)");
            StringAssert.Contains(devCollector, "AILearningSystem.CalculateTelemetryReward(");
            StringAssert.Contains(devCollector, "_battleSnapshots.Remove(militia.StringId);");
        }
    }
}
