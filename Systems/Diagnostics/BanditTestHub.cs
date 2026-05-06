using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Spawning;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Diagnostics
{
    public sealed class BanditRuntimeTestResult
    {
        public BanditRuntimeTestResult(string name, string description, bool passed, string summary, string details)
        {
            Name = name;
            Description = description;
            Passed = passed;
            Summary = summary;
            Details = details;
            RunAtUtc = DateTime.UtcNow;
        }

        public string Name { get; }
        public string Description { get; }
        public bool Passed { get; }
        public string Summary { get; }
        public string Details { get; }
        public DateTime RunAtUtc { get; }
    }

    internal sealed class BanditRuntimeCheck
    {
        public BanditRuntimeCheck(string name, string description, Func<BanditRuntimeTestResult> run)
        {
            Name = name;
            Description = description;
            Run = run;
        }

        public string Name { get; }
        public string Description { get; }
        public Func<BanditRuntimeTestResult> Run { get; }
    }

    public sealed class BanditTestHub
    {
        private static readonly Lazy<BanditTestHub> _instance = new(() => new BanditTestHub());
        private readonly Dictionary<string, BanditRuntimeCheck> _checks = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<BanditRuntimeTestResult> _lastResults = new();
        private readonly object _sync = new();
        private DateTime _lastRunUtc = DateTime.MinValue;


        public static int CurrentSeed { get; private set; } = 0;

        public static void ApplyDeterministicSeed(int seed)
        {
            CurrentSeed = seed;


            try { _ = TaleWorlds.Core.MBRandom.RandomFloat; } catch { }


            _deterministicRng = new System.Random(seed);
        }

        private static System.Random? _deterministicRng;


        public static float DeterministicFloat =>
            _deterministicRng != null ? (float)_deterministicRng.NextDouble() : TaleWorlds.Core.MBRandom.RandomFloat;


        private static readonly Dictionary<string, float> _thresholds = new(StringComparer.OrdinalIgnoreCase)
        {
            ["militia_count"] = 300f,
            ["fps_min"]       = 20f,
            ["memory_mb"]     = 1400f,
            ["drop_events"]   = 500f,
        };

        public static void SetThreshold(string metric, float value)
        {
            _thresholds[metric] = value;
        }

        public static bool CheckThreshold(string metric, float actual)
        {
            if (!_thresholds.TryGetValue(metric, out float limit)) return true;


            if (metric == "fps_min") return actual >= limit;
            return actual <= limit;
        }

        public static string GetThresholdReport()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kvp in _thresholds)
                sb.AppendLine($"  {kvp.Key,-20} = {kvp.Value}");
            return sb.ToString();
        }

        public static BanditTestHub Instance => _instance.Value;

        private BanditTestHub()
        {
            RegisterChecks();
        }

        public IReadOnlyCollection<string> CheckNames => _checks.Keys.OrderBy(x => x).ToArray();

        public void Reset()
        {
            lock (_sync)
            {
                _lastResults.Clear();
                _lastRunUtc = DateTime.MinValue;
            }
        }

        public IReadOnlyList<BanditRuntimeTestResult> Run(string? target)
        {
            string normalized = string.IsNullOrWhiteSpace(target) ? "all" : target?.Trim() ?? "all";
            List<BanditRuntimeCheck> checksToRun = ResolveChecks(normalized);

            var results = new List<BanditRuntimeTestResult>(checksToRun.Count);
            foreach (BanditRuntimeCheck check in checksToRun)
            {
                results.Add(RunCheck(check));
            }

            lock (_sync)
            {
                _lastResults.Clear();
                _lastResults.AddRange(results);
                _lastRunUtc = DateTime.UtcNow;
            }

            return results;
        }

        public string BuildCatalogReport()
        {
            var sb = new StringBuilder();
            _ = sb.AppendLine("=== BANDIT TEST HUB ===");
            _ = sb.AppendLine("Commands:");
            _ = sb.AppendLine("  bandit.test_list");
            _ = sb.AppendLine("  bandit.test_run <name|all>");
            _ = sb.AppendLine("  bandit.test_report");
            _ = sb.AppendLine("  bandit.test_reset");
            _ = sb.AppendLine();
            _ = sb.AppendLine("Available checks:");

            foreach (BanditRuntimeCheck check in _checks.Values.OrderBy(x => x.Name))
            {
                _ = sb.AppendLine($"  {check.Name,-28} {check.Description}");
            }

            return sb.ToString();
        }

        public string BuildLastReport()
        {
            List<BanditRuntimeTestResult> snapshot;
            DateTime lastRunUtc;
            lock (_sync)
            {
                snapshot = _lastResults.ToList();
                lastRunUtc = _lastRunUtc;
            }

            if (snapshot.Count == 0)
            {
                return "BanditTestHub: No results yet. Use `bandit.test_run all` or `bandit.test_run <name>`.";
            }

            int passed = snapshot.Count(x => x.Passed);
            int failed = snapshot.Count - passed;

            var sb = new StringBuilder();
            _ = sb.AppendLine("=== BANDIT TEST REPORT ===");
            _ = sb.AppendLine($"LastRunUtc: {lastRunUtc:yyyy-MM-dd HH:mm:ss}");
            _ = sb.AppendLine($"Summary   : PASS={passed} FAIL={failed} TOTAL={snapshot.Count}");
            _ = sb.AppendLine();

            foreach (BanditRuntimeTestResult result in snapshot.OrderBy(x => x.Name))
            {
                _ = sb.AppendLine($"[{(result.Passed ? "PASS" : "FAIL")}] {result.Name}");
                _ = sb.AppendLine($"  Desc   : {result.Description}");
                _ = sb.AppendLine($"  Summary: {result.Summary}");
                if (!string.IsNullOrWhiteSpace(result.Details))
                {
                    _ = sb.AppendLine($"  Details: {result.Details}");
                }
            }

            return sb.ToString();
        }

        public string BuildSummaryLine()
        {
            lock (_sync)
            {
                if (_lastResults.Count == 0)
                {
                    return $"BanditTestHub: no run yet ({_checks.Count} registered checks)";
                }

                int passed = _lastResults.Count(x => x.Passed);
                int failed = _lastResults.Count - passed;
                return $"BanditTestHub: PASS={passed} FAIL={failed} TOTAL={_lastResults.Count}";
            }
        }

        private void RegisterChecks()
        {
            Register("test_mode_state", "TestingMode and ShowTestMessages synchronization", CheckTestModeState);
            Register("module_registry_health", "Ghost, dead, stale and cold module audit", CheckModuleRegistryHealth);
            Register("spawn_pipeline_wiring", "ModuleManager and spawn system wiring check", CheckSpawnPipelineWiring);
            Register("hideout_cache_readiness", "Hideout cache fullness and active count", CheckHideoutCacheReadiness);
            Register("activation_delay_gate", "Activation delay gate consistency", CheckActivationDelayGate);
            Register("warlord_fallback_rule", "Two-phase warlord fallback rule", CheckWarlordFallbackRule);
            Register("verify_contract_bridge", "Current bandit.verify_contract bridge status", CheckVerifyContractBridge);
            Register("verify_warlord_economy_bridge", "Current bandit.verify_warlord_economy bridge status", CheckVerifyWarlordEconomyBridge);
            Register("verify_integration_bridge", "Current bandit.verify_integration bridge status", CheckVerifyIntegrationBridge);
        }

        private void Register(string name, string description, Func<BanditRuntimeTestResult> run)
        {
            _checks[name] = new BanditRuntimeCheck(name, description, run);
        }

        private List<BanditRuntimeCheck> ResolveChecks(string target)
        {
            if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return _checks.Values.OrderBy(x => x.Name).ToList();
            }

            if (_checks.TryGetValue(target, out BanditRuntimeCheck? single))
            {
                return new List<BanditRuntimeCheck> { single };
            }

            throw new ArgumentException(
                $"Unknown BanditTestHub check '{target}'. Use bandit.test_list to inspect available checks.",
                nameof(target));
        }

        private BanditRuntimeTestResult RunCheck(BanditRuntimeCheck check)
        {
            try
            {
                return check.Run();
            }
            catch (Exception ex)
            {
                return new BanditRuntimeTestResult(
                    check.Name,
                    check.Description,
                    passed: false,
                    summary: $"{check.Name} threw {ex.GetType().Name}",
                    details: ex.Message);
            }
        }

        private static BanditRuntimeTestResult CheckTestModeState()
        {
            if (Settings.Instance == null)
            {
                return Fail("test_mode_state", "TestingMode and ShowTestMessages synchronization", "Settings.Instance null", "Settings not loaded yet.");
            }

            bool aligned = !Settings.Instance.TestingMode || Settings.Instance.ShowTestMessages;
            string summary = $"TestingMode={Settings.Instance.TestingMode}, ShowTestMessages={Settings.Instance.ShowTestMessages}";

            return aligned
                ? Pass("test_mode_state", "TestingMode and ShowTestMessages synchronization", summary)
                : Fail("test_mode_state", "TestingMode and ShowTestMessages synchronization", summary, "ShowTestMessages must be enabled when TestingMode is enabled.");
        }

        private static BanditRuntimeTestResult CheckSpawnPipelineWiring()
        {
            bool hasCampaign = Campaign.Current != null;
            bool hasModuleManager = ModuleManager.Instance != null;
            bool hasSpawner = ModuleManager.Instance?.GetModule<MilitiaSpawningSystem>() != null;
            bool bootstrapComplete = ModuleManager.Instance?.IsSessionBootstrapComplete == true;

            string summary = $"Campaign={hasCampaign}, ModuleManager={hasModuleManager}, Spawner={hasSpawner}, Bootstrap={bootstrapComplete}";
            bool passed = hasCampaign && hasModuleManager && hasSpawner;

            return passed
                ? Pass("spawn_pipeline_wiring", "ModuleManager and spawn system wiring check", summary)
                : Fail("spawn_pipeline_wiring", "ModuleManager and spawn system wiring check", summary, "Campaign, ModuleManager and MilitiaSpawningSystem must be ready at the same time.");
        }

        private static BanditRuntimeTestResult CheckModuleRegistryHealth()
        {
            ModuleRegistryHealthSnapshot snapshot = ModuleRegistryHealthAnalyzer.Capture();
            string details = snapshot.BuildDetails();

            return !snapshot.HasProblems
                ? Pass("module_registry_health", "Ghost, dead, stale and cold module audit", snapshot.Summary, string.IsNullOrWhiteSpace(details) ? "No registry health issues detected." : details)
                : Fail("module_registry_health", "Ghost, dead, stale and cold module audit", snapshot.Summary, details);
        }

        private static BanditRuntimeTestResult CheckHideoutCacheReadiness()
        {
            int total = ModuleManager.Instance?.HideoutCache?.Count ?? -1;
            int active = ModuleManager.Instance?.HideoutCache?.Count(x => x != null && x.IsHideout && x.IsActive) ?? -1;
            string summary = $"HideoutCache total={total}, active={active}";

            return total > 0 && active >= 0
                ? Pass("hideout_cache_readiness", "Hideout cache fullness and active count", summary)
                : Fail("hideout_cache_readiness", "Hideout cache fullness and active count", summary, "Hideout cache is empty or inaccessible.");
        }

        private static BanditRuntimeTestResult CheckActivationDelayGate()
        {
            bool switchClosed = ModActivationManager.IsGameplayActivationSwitchClosed();
            bool delayed = ModActivationManager.IsGameplayActivationDelayed();
            float elapsedDays = ModActivationManager.GetActivationDelayElapsedDays();
            bool initialized = ModActivationManager.IsGameFullyInitialized();

            bool consistent = !(switchClosed && delayed);
            string summary = $"SwitchClosed={switchClosed}, Delayed={delayed}, ElapsedDays={elapsedDays:F2}, Initialized={initialized}";

            return consistent
                ? Pass("activation_delay_gate", "Activation delay gate consistency", summary)
                : Fail("activation_delay_gate", "Activation delay gate consistency", summary, "System still returns delayed while gameplay activation switch is closed.");
        }

        private static BanditRuntimeTestResult CheckWarlordFallbackRule()
        {
            bool earlySeed = WarlordProgressionRules.ShouldRunWarlordFallback(0, false, 60f, 60);
            bool earlyBlockedSingle = !WarlordProgressionRules.ShouldRunWarlordFallback(1, false, 80f, 60);
            bool lateEscalation = WarlordProgressionRules.ShouldRunWarlordFallback(2, false, 150f, 150);
            bool lateNoZeroFallback = !WarlordProgressionRules.ShouldRunWarlordFallback(0, false, 200f, 150);
            bool limitGuard = !WarlordProgressionRules.ShouldRunWarlordFallback(2, true, 150f, 150);

            bool passed = earlySeed && earlyBlockedSingle && lateEscalation && lateNoZeroFallback && limitGuard;
            string summary = $"earlySeed={earlySeed}, singleBlocked={earlyBlockedSingle}, lateEscalation={lateEscalation}, zeroBlockedLate={lateNoZeroFallback}, limitGuard={limitGuard}";

            return passed
                ? Pass("warlord_fallback_rule", "Two-phase warlord fallback rule", summary)
                : Fail("warlord_fallback_rule", "Two-phase warlord fallback rule", summary, "Fallback helper does not produce the expected two-phase behavior.");
        }

        private static BanditRuntimeTestResult CheckVerifyContractBridge()
            => RunBridgeCheck(
                "verify_contract_bridge",
                "Current bandit.verify_contract bridge status",
                () => BanditMilitias.Debug.VerificationCommands.VerifyContract(new List<string>()),
                result => result.IndexOf("PASS", StringComparison.OrdinalIgnoreCase) >= 0);

        private static BanditRuntimeTestResult CheckVerifyWarlordEconomyBridge()
            => RunBridgeCheck(
                "verify_warlord_economy_bridge",
                "Current bandit.verify_warlord_economy bridge status",
                () => BanditMilitias.Debug.VerificationCommands.VerifyWarlordEconomy(new List<string>()),
                result => result.IndexOf("Spawn: SUCCESS", StringComparison.OrdinalIgnoreCase) >= 0);

        private static BanditRuntimeTestResult CheckVerifyIntegrationBridge()
            => RunBridgeCheck(
                "verify_integration_bridge",
                "Current bandit.verify_integration bridge status",
                () => BanditMilitias.Debug.VerificationCommands.VerifyIntegration(new List<string>()),
                result =>
                    result.IndexOf("FAIL", StringComparison.OrdinalIgnoreCase) < 0 &&
                    result.IndexOf("DESYNCED", StringComparison.OrdinalIgnoreCase) < 0);

        private static BanditRuntimeTestResult RunBridgeCheck(string name, string description, Func<string> run, Func<string, bool> successPredicate)
        {
            string output = run() ?? string.Empty;
            string summary = FirstNonEmptyLine(output);

            return successPredicate(output)
                ? Pass(name, description, summary, output)
                : Fail(name, description, summary, output);
        }

        private static string FirstNonEmptyLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "(empty output)";
            }

            string? line = text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(x => x.Trim())
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

            return string.IsNullOrWhiteSpace(line) ? "(empty output)" : line;
        }

        private static BanditRuntimeTestResult Pass(string name, string description, string summary, string details = "")
            => new(name, description, passed: true, summary, details);

        private static BanditRuntimeTestResult Fail(string name, string description, string summary, string details)
            => new(name, description, passed: false, summary, details);
    }

    public static class BanditTestHubCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("test_list", "bandit")]
        public static string TestList(List<string> _)
        {
            return BanditTestHub.Instance.BuildCatalogReport();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("test_run", "bandit")]
        public static string TestRun(List<string> args)
        {
            string target = args != null && args.Count > 0 ? args[0] : "all";
            _ = BanditTestHub.Instance.Run(target);
            return BanditTestHub.Instance.BuildLastReport();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("test_report", "bandit")]
        public static string TestReport(List<string> _)
        {
            return BanditTestHub.Instance.BuildLastReport();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("test_reset", "bandit")]
        public static string TestReset(List<string> _)
        {
            BanditTestHub.Instance.Reset();
            return "BanditTestHub state reset. Use bandit.test_run all to collect fresh results.";
        }


        [CommandLineFunctionality.CommandLineArgumentFunction("test_seed", "bandit")]
        public static string TestSeed(List<string> args)
        {
            if (args == null || args.Count == 0)
            {
                return $"[BanditMilitias] Current test seed: {BanditTestHub.CurrentSeed}\n" +
                       $"Usage: bandit.test_seed [number]   (e.g. bandit.test_seed 42)";
            }

            if (!int.TryParse(args[0].Trim(), out int seed))
                return $"[BanditMilitias] Invalid seed: '{args[0]}'. Please enter an integer.";

            BanditTestHub.ApplyDeterministicSeed(seed);
            return $"[BanditMilitias] Deterministic seed applied: {seed}\n" +
                   $"Now you can get consistent results by using the same seed with bandit.test_run.";
        }


        [CommandLineFunctionality.CommandLineArgumentFunction("test_threshold", "bandit")]
        public static string TestThreshold(List<string> args)
        {
            if (args == null || args.Count < 2)
            {
                return "[BanditMilitias] Usage: bandit.test_threshold [metric] [value]\n" +
                       "Supported metrics:\n" +
                       "  militia_count [max]   → Test FAILS if militia count exceeds this value\n" +
                       "  fps_min [min]         → Test FAILS if FPS falls below this value\n" +
                       "  memory_mb [max]       → Test FAILS if RAM exceeds this value\n" +
                       "  drop_events [max]     → Test FAILS if EventBus drop count exceeds this value\n\n" +
                       "Current thresholds:\n" +
                       BanditTestHub.GetThresholdReport();
            }

            string metric = args[0].Trim().ToLowerInvariant();
            if (!float.TryParse(args[1].Trim(), out float value))
                return $"[BanditMilitias] Invalid value: '{args[1]}'.";

            BanditTestHub.SetThreshold(metric, value);
            return $"[BanditMilitias] Threshold set: {metric} = {value}";
        }
    }
}
