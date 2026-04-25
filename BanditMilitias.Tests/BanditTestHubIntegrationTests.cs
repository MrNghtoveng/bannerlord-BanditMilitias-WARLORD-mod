using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class BanditTestHubIntegrationTests
    {
        [TestMethod]
        public void ToggleTestModeCommand_Synchronizes_ShowTestMessages()
        {
            string debugSource = TestSourceHelper.ReadProjectFile("Debug/Debug.cs");

            StringAssert.Contains(debugSource, "Settings.Instance.ShowTestMessages = Settings.Instance.TestingMode;");
            StringAssert.Contains(debugSource, "ShowTestMessages={Settings.Instance.ShowTestMessages}");
        }

        [TestMethod]
        public void BanditTestHub_Registers_Runtime_Test_Commands()
        {
            string hubSource = TestSourceHelper.ReadProjectFile("Systems/Diagnostics/BanditTestHub.cs");

            StringAssert.Contains(hubSource, "CommandLineArgumentFunction(\"test_list\", \"bandit\")");
            StringAssert.Contains(hubSource, "CommandLineArgumentFunction(\"test_run\", \"bandit\")");
            StringAssert.Contains(hubSource, "CommandLineArgumentFunction(\"test_report\", \"bandit\")");
            StringAssert.Contains(hubSource, "CommandLineArgumentFunction(\"test_reset\", \"bandit\")");
        }

        [TestMethod]
        public void BanditTestHub_Catalog_Lists_Critical_Checks()
        {
            string hubSource = TestSourceHelper.ReadProjectFile("Systems/Diagnostics/BanditTestHub.cs");

            StringAssert.Contains(hubSource, "test_mode_state");
            StringAssert.Contains(hubSource, "module_registry_health");
            StringAssert.Contains(hubSource, "spawn_pipeline_wiring");
            StringAssert.Contains(hubSource, "hideout_cache_readiness");
            StringAssert.Contains(hubSource, "activation_delay_gate");
            StringAssert.Contains(hubSource, "warlord_fallback_rule");
            StringAssert.Contains(hubSource, "verify_contract_bridge");
            StringAssert.Contains(hubSource, "verify_warlord_economy_bridge");
            StringAssert.Contains(hubSource, "verify_integration_bridge");
            StringAssert.Contains(hubSource, "=== BANDIT TEST REPORT ===");
            StringAssert.Contains(hubSource, "ModuleRegistryHealthAnalyzer.Capture()");
        }

        [TestMethod]
        public void DiagnosticsHelp_And_FullReport_Mention_BanditTestHub()
        {
            string diagnosticsSource = TestSourceHelper.ReadProjectFile("Systems/Diagnostics/DiagnosticsSystem.cs");

            StringAssert.Contains(diagnosticsSource, "bandit.test_list");
            StringAssert.Contains(diagnosticsSource, "bandit.test_run");
            StringAssert.Contains(diagnosticsSource, "bandit.test_report");
            StringAssert.Contains(diagnosticsSource, "BanditTestHub.Instance.BuildSummaryLine()");
        }

        [TestMethod]
        public void MainSolution_Includes_Tests_Project()
        {
            string solutionSource = TestSourceHelper.ReadProjectFile("BanditMilitias.sln");
            StringAssert.Contains(solutionSource, "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"BanditMilitias.Tests\"");
            StringAssert.Contains(solutionSource, "BanditMilitias.Tests\\BanditMilitias.Tests.csproj");
        }

        [TestMethod]
        public void Readme_Mentions_Dotnet_Test_And_Runtime_TestHub()
        {
            string readme = TestSourceHelper.ReadProjectFile("README.md");

            StringAssert.Contains(readme, "dotnet test .\\BanditMilitias.Tests\\BanditMilitias.Tests.csproj");
            StringAssert.Contains(readme, "bandit.test_list");
            StringAssert.Contains(readme, "bandit.test_run all");
            StringAssert.Contains(readme, "bandit.test_report");
            StringAssert.Contains(readme, "cold module");
        }

        [TestMethod]
        public void WarlordFallbackRule_Implementation_Contains_TwoPhase_Gates()
        {
            string source = TestSourceHelper.ReadProjectFile("Intelligence/Strategic/WarlordSystem.cs");

            StringAssert.Contains(source, "fallbackDays <= 60 && activeWarlordCount == 0");
            StringAssert.Contains(source, "fallbackDays >= 150 && activeWarlordCount >= 2");
            StringAssert.Contains(source, "activeWarlordCount != 1");
            StringAssert.Contains(source, "return timeCheck && shouldUseWarlordFallbackWindow;");
        }
    }
}
