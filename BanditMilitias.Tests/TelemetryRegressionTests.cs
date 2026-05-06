using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class TelemetryRegressionTests
    {
        [TestMethod]
        public void Battle_Action_Attribution_Must_Use_Heuristic_Decider_And_SmartCache()
        {
            string decider = TestSourceHelper.ReadProjectFile("Intelligence", "AI", "Components", "MilitiaDecider.cs");
            string logger = TestSourceHelper.ReadProjectFile("Intelligence", "Logging", "AIDecisionLogger.cs");


            StringAssert.Contains(decider, "MilitiaSmartCache.Instance.CacheDecision(");


            StringAssert.Contains(decider, "AIDecisionLogger.LogTacticalDecision(");


            StringAssert.Contains(logger, "public static void LogTacticalDecision(");
            StringAssert.Contains(logger, "public static void LogWarlordResponse(");
        }

        [TestMethod]
        public void Heuristic_Decider_Must_Implement_Role_Based_Logic()
        {
            string decider = TestSourceHelper.ReadProjectFile("Intelligence", "AI", "Components", "MilitiaDecider.cs");


            StringAssert.Contains(decider, "component.Role == MilitiaPartyComponent.MilitiaRole.Guardian");
            StringAssert.Contains(decider, "source = \"GuardianLeash\"");


            StringAssert.Contains(decider, "component.Role == MilitiaPartyComponent.MilitiaRole.Raider");
            StringAssert.Contains(decider, "result.Decision = AIDecisionType.Raid");


            StringAssert.Contains(decider, "SwarmCoordinator.Instance.TryGetOrder(");
        }
    }
}
