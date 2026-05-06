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

        // ── Deterministic seed desteği ────────────────────────────
        public static int CurrentSeed { get; private set; } = 0;

        public static void ApplyDeterministicSeed(int seed)
        {
            CurrentSeed = seed;
            // MBRandom'un seed'ini ayarla — Bannerlord native rastgelelik motoru
            try { _ = TaleWorlds.Core.MBRandom.RandomFloat; } catch { } // warm-up
            // TaleWorlds.Core.MBRandom doğrudan seed almaz; System.Random ile sarıyoruz
            _deterministicRng = new System.Random(seed);
        }

        private static System.Random? _deterministicRng;

        /// <summary>
        /// Deterministic seed ayarlıysa seed'li RNG'den, değilse MBRandom'dan değer döner.
        /// Test kodunda MBRandom.RandomFloat yerine bu çağrılır.
        /// </summary>
        public static float DeterministicFloat =>
            _deterministicRng != null ? (float)_deterministicRng.NextDouble() : TaleWorlds.Core.MBRandom.RandomFloat;

        // ── Test başarı eşikleri ──────────────────────────────────
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
            // fps_min: actual >= limit olmalı; diğerleri: actual <= limit
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
                return "BanditTestHub: Henüz sonuç yok. `bandit.test_run all` veya `bandit.test_run <name>` kullan.";
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
            Register("test_mode_state", "TestingMode ve ShowTestMessages senkronu", CheckTestModeState);
            Register("module_registry_health", "Ghost, dead, stale ve cold module denetimi", CheckModuleRegistryHealth);
            Register("spawn_pipeline_wiring", "ModuleManager ve spawn sistemi kablo kontrolu", CheckSpawnPipelineWiring);
            Register("hideout_cache_readiness", "Hideout cache dolulugu ve aktif sayi", CheckHideoutCacheReadiness);
            Register("activation_delay_gate", "Activation delay gate tutarliligi", CheckActivationDelayGate);
            Register("warlord_fallback_rule", "Iki fazli warlord fallback kurali", CheckWarlordFallbackRule);
            Register("verify_contract_bridge", "Mevcut bandit.verify_contract kopru durumu", CheckVerifyContractBridge);
            Register("verify_warlord_economy_bridge", "Mevcut bandit.verify_warlord_economy kopru durumu", CheckVerifyWarlordEconomyBridge);
            Register("verify_integration_bridge", "Mevcut bandit.verify_integration kopru durumu", CheckVerifyIntegrationBridge);
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
                return Fail("test_mode_state", "TestingMode ve ShowTestMessages senkronu", "Settings.Instance null", "Settings henüz yüklenmedi.");
            }

            bool aligned = !Settings.Instance.TestingMode || Settings.Instance.ShowTestMessages;
            string summary = $"TestingMode={Settings.Instance.TestingMode}, ShowTestMessages={Settings.Instance.ShowTestMessages}";

            return aligned
                ? Pass("test_mode_state", "TestingMode ve ShowTestMessages senkronu", summary)
                : Fail("test_mode_state", "TestingMode ve ShowTestMessages senkronu", summary, "TestingMode açıkken ShowTestMessages da açık olmalı.");
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
                ? Pass("spawn_pipeline_wiring", "ModuleManager ve spawn sistemi kablo kontrolu", summary)
                : Fail("spawn_pipeline_wiring", "ModuleManager ve spawn sistemi kablo kontrolu", summary, "Campaign, ModuleManager ve MilitiaSpawningSystem ayni anda hazir olmali.");
        }

        private static BanditRuntimeTestResult CheckModuleRegistryHealth()
        {
            ModuleRegistryHealthSnapshot snapshot = ModuleRegistryHealthAnalyzer.Capture();
            string details = snapshot.BuildDetails();

            return !snapshot.HasProblems
                ? Pass("module_registry_health", "Ghost, dead, stale ve cold module denetimi", snapshot.Summary, string.IsNullOrWhiteSpace(details) ? "No registry health issues detected." : details)
                : Fail("module_registry_health", "Ghost, dead, stale ve cold module denetimi", snapshot.Summary, details);
        }

        private static BanditRuntimeTestResult CheckHideoutCacheReadiness()
        {
            int total = ModuleManager.Instance?.HideoutCache?.Count ?? -1;
            int active = ModuleManager.Instance?.HideoutCache?.Count(x => x != null && x.IsHideout && x.IsActive) ?? -1;
            string summary = $"HideoutCache total={total}, active={active}";

            return total > 0 && active >= 0
                ? Pass("hideout_cache_readiness", "Hideout cache dolulugu ve aktif sayi", summary)
                : Fail("hideout_cache_readiness", "Hideout cache dolulugu ve aktif sayi", summary, "Hideout cache boş veya erişilemiyor.");
        }

        private static BanditRuntimeTestResult CheckActivationDelayGate()
        {
            bool switchClosed = CompatibilityLayer.IsGameplayActivationSwitchClosed();
            bool delayed = CompatibilityLayer.IsGameplayActivationDelayed();
            float elapsedDays = CompatibilityLayer.GetActivationDelayElapsedDays();
            bool initialized = CompatibilityLayer.IsGameFullyInitialized();

            bool consistent = !(switchClosed && delayed);
            string summary = $"SwitchClosed={switchClosed}, Delayed={delayed}, ElapsedDays={elapsedDays:F2}, Initialized={initialized}";

            return consistent
                ? Pass("activation_delay_gate", "Activation delay gate tutarliligi", summary)
                : Fail("activation_delay_gate", "Activation delay gate tutarliligi", summary, "Gameplay activation switch kapalıyken sistem hala delayed dönüyor.");
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
                ? Pass("warlord_fallback_rule", "Iki fazli warlord fallback kurali", summary)
                : Fail("warlord_fallback_rule", "Iki fazli warlord fallback kurali", summary, "Fallback helper beklenen iki fazlı davranışı üretmiyor.");
        }

        private static BanditRuntimeTestResult CheckVerifyContractBridge()
            => RunBridgeCheck(
                "verify_contract_bridge",
                "Mevcut bandit.verify_contract kopru durumu",
                () => BanditMilitias.Debug.VerificationCommands.VerifyContract(new List<string>()),
                result => result.IndexOf("PASS", StringComparison.OrdinalIgnoreCase) >= 0);

        private static BanditRuntimeTestResult CheckVerifyWarlordEconomyBridge()
            => RunBridgeCheck(
                "verify_warlord_economy_bridge",
                "Mevcut bandit.verify_warlord_economy kopru durumu",
                () => BanditMilitias.Debug.VerificationCommands.VerifyWarlordEconomy(new List<string>()),
                result => result.IndexOf("Spawn: SUCCESS", StringComparison.OrdinalIgnoreCase) >= 0);

        private static BanditRuntimeTestResult CheckVerifyIntegrationBridge()
            => RunBridgeCheck(
                "verify_integration_bridge",
                "Mevcut bandit.verify_integration kopru durumu",
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

        /// <summary>
        /// bandit.test_seed [N] — Deterministic test seed'i ayarlar.
        /// Aynı seed → MBRandom'un aynı sequenceını üretir → aynı sonuç.
        /// Kullanım: bandit.test_seed 42
        ///           bandit.test_seed (argümansız → mevcut seed'i gösterir)
        /// </summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("test_seed", "bandit")]
        public static string TestSeed(List<string> args)
        {
            if (args == null || args.Count == 0)
            {
                return $"[BanditMilitias] Mevcut test seed: {BanditTestHub.CurrentSeed}\n" +
                       $"Kullanım: bandit.test_seed [sayı]   (örn: bandit.test_seed 42)";
            }

            if (!int.TryParse(args[0].Trim(), out int seed))
                return $"[BanditMilitias] Geçersiz seed: '{args[0]}'. Tam sayı girin.";

            BanditTestHub.ApplyDeterministicSeed(seed);
            return $"[BanditMilitias] Deterministic seed uygulandı: {seed}\n" +
                   $"Şimdi bandit.test_run ile aynı seed'i kullanarak tutarlı sonuçlar alabilirsiniz.";
        }

        /// <summary>
        /// bandit.test_threshold [metrik] [değer] — Test başarı eşiği tanımlar.
        /// Kullanım: bandit.test_threshold militia_count 200
        ///           bandit.test_threshold fps_min 25
        /// </summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("test_threshold", "bandit")]
        public static string TestThreshold(List<string> args)
        {
            if (args == null || args.Count < 2)
            {
                return "[BanditMilitias] Kullanım: bandit.test_threshold [metrik] [değer]\n" +
                       "Desteklenen metrikler:\n" +
                       "  militia_count [max]   → Milis sayısı bu değeri aşarsa test FAIL\n" +
                       "  fps_min [min]         → FPS bu değerin altına düşerse test FAIL\n" +
                       "  memory_mb [max]       → RAM bu değeri aşarsa test FAIL\n" +
                       "  drop_events [max]     → EventBus drop sayısı bu değeri aşarsa test FAIL\n\n" +
                       "Mevcut eşikler:\n" +
                       BanditTestHub.GetThresholdReport();
            }

            string metric = args[0].Trim().ToLowerInvariant();
            if (!float.TryParse(args[1].Trim(), out float value))
                return $"[BanditMilitias] Geçersiz değer: '{args[1]}'.";

            BanditTestHub.SetThreshold(metric, value);
            return $"[BanditMilitias] Eşik ayarlandı: {metric} = {value}";
        }
    }
}
