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

            // Verify Heuristic Decider uses SmartCache for performance
            StringAssert.Contains(decider, "MilitiaSmartCache.Instance.CacheDecision(");
            
            // Verify tactical decisions are logged for telemetry analysis
            StringAssert.Contains(decider, "AIDecisionLogger.LogTacticalDecision(");
            
            // Verify logger has structured logging methods
            StringAssert.Contains(logger, "public static void LogTacticalDecision(");
            StringAssert.Contains(logger, "public static void LogWarlordResponse(");
        }

        [TestMethod]
        public void Heuristic_Decider_Must_Implement_Role_Based_Logic()
        {
            string decider = TestSourceHelper.ReadProjectFile("Intelligence", "AI", "Components", "MilitiaDecider.cs");

            // Verify Guardian leash logic
            StringAssert.Contains(decider, "component.Role == MilitiaPartyComponent.MilitiaRole.Guardian");
            StringAssert.Contains(decider, "source = \"GuardianLeash\"");

            // Verify Raider raid logic
            StringAssert.Contains(decider, "component.Role == MilitiaPartyComponent.MilitiaRole.Raider");
            StringAssert.Contains(decider, "result.Decision = AIDecisionType.Raid");

            // Verify Swarm override integration
            StringAssert.Contains(decider, "SwarmCoordinator.Instance.TryGetOrder(");
        }
    }
}
