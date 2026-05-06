using System;
using BanditMilitias.Core.Components;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Infrastructure;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TaleWorlds.CampaignSystem.Party;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using BanditMilitias.Systems.AI;
using BanditMilitias.Intelligence.Neural;

namespace BanditMilitias.Systems.Diagnostics
{

    public static class DiagnosticsSystem
    {

        private const double CRITICAL_FRAME_TIME_MS = 3.0;

        private static readonly ConcurrentDictionary<string, long> _startTimestamps = new();
        private static readonly ConcurrentDictionary<string, double> _averageTimes = new();
        private static readonly ConcurrentDictionary<string, long> _callCounts = new();

        private static readonly ConcurrentDictionary<string, float> _customMetrics = new();

        public static bool IsHighLoad { get; private set; } = false;
        private static int _loadCheckThrottle = 0;
        private static float _auditTimer = 0f;
        private const float AUDIT_INTERVAL_SEC = 1800f;


        private static bool _initialAuditDone = false;
        private static readonly List<string> EmptyArgs = new List<string>();

        public static void Update(float dt)
        {
            if (Settings.Instance?.TestingMode != true || Campaign.Current == null)
            {
                _auditTimer = 0f;
                _initialAuditDone = false;
                return;
            }


            if (!_initialAuditDone)
            {
                _initialAuditDone = true;
                Infrastructure.FileLogger.Log("[AUTO-AUDIT] Starting initial session audit automatically.");
                CommandAudit(EmptyArgs);
                _auditTimer = 0f;

                return;
            }

            _auditTimer += dt;
            if (_auditTimer >= AUDIT_INTERVAL_SEC)
            {
                _auditTimer = 0f;
                Infrastructure.FileLogger.Log("[AUTO-AUDIT] Triggering periodic audit in TestingMode.");
                CommandAudit(EmptyArgs);
            }
        }

        public static void OnSessionEnd()
        {
            _startTimestamps.Clear();
            _customMetrics.Clear();
            _initialAuditDone = false;
            _auditTimer = 0f;


        }

        [Conditional("DEBUG"), Conditional("RELEASE")]
        public static void StartScope(string scopeName)
        {
            _startTimestamps[scopeName] = Stopwatch.GetTimestamp();
        }

        [Conditional("DEBUG"), Conditional("RELEASE")]
        public static void EndScope(string scopeName)
        {
            if (_startTimestamps.TryRemove(scopeName, out long startTicks))
            {
                long endTicks = Stopwatch.GetTimestamp();
                double elapsedMs = (endTicks - startTicks) * 1000.0 / Stopwatch.Frequency;

                _averageTimes.AddOrUpdate(scopeName, elapsedMs, (_, oldAvg) => (oldAvg * 0.95) + (elapsedMs * 0.05));
                _callCounts.AddOrUpdate(scopeName, 1L, (_, oldCount) => oldCount + 1);


                if (System.Threading.Interlocked.Increment(ref _loadCheckThrottle) % 10 == 0)
                {
                    CheckSystemLoad();
                }
            }
        }

        private static void CheckSystemLoad()
        {


            double hourly = _averageTimes.TryGetValue("Militia.HourlyTick", out var h) ? h : 0;
            double daily  = _averageTimes.TryGetValue("Militia.DailyTick",  out var d) ? d : 0;
            double total  = hourly + daily;

            IsHighLoad = total > CRITICAL_FRAME_TIME_MS;
        }

        public static string GenerateReport()
        {
            var sb = new StringBuilder();
            _ = sb.AppendLine("=== DIAGNOSTICS REPORT ===");
            _ = sb.AppendLine($"Load Status: {(IsHighLoad ? "HIGH" : "NORMAL")}");

            foreach (var kvp in _averageTimes)
            {
                _ = sb.AppendLine($"{kvp.Key}: {kvp.Value:F2}ms (avg)");
            }

            if (_customMetrics.Count > 0)
            {
                _ = sb.AppendLine("--- Metrics ---");
                foreach (var kvp in _customMetrics.OrderBy(k => k.Key))
                {
                    _ = sb.AppendLine($"{kvp.Key}: {kvp.Value:F3}");
                }
            }

            return sb.ToString();
        }

        public static void SetMetric(string key, float value)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            _customMetrics[key] = value;
        }

        public static void IncrementMetric(string key, float delta = 1f)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            _customMetrics.AddOrUpdate(key, delta, (_, oldVal) => oldVal + delta);
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("diag_report", "militia")]
        public static string CommandDiagReport(List<string> args)
        {
            string report = GenerateReport();
            SaveReportToFile(report, "BanditMilitias_Diag.txt");
            return report + "\n(Report also saved to Warlord_Logs/BanditMilitias/Diagnostics/BanditMilitias_Diag.txt)";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("audit", "militia")]
        public static string CommandAudit(List<string> args)
        {
            var sb = new StringBuilder();
            sb.AppendLine("==================================================================");
            sb.AppendLine("   BANDIT MILITIAS: FULL SYSTEM & AI AUDIT REPORT");
            sb.AppendLine($"   Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("==================================================================");


            sb.AppendLine("\n[SECTION 1: CORE INFRASTRUCTURE]");
            sb.AppendLine(SubModule.GetDiagnostics());


            sb.AppendLine("\n[SECTION 2: MODULE REGISTRY & HEALTH]");
            if (ModuleManager.Instance != null)
            {
                sb.AppendLine(ModuleManager.DebugSystemStatus(EmptyArgs));
                string failed = ModuleManager.Instance.GetFailedModulesReport();
                if (!failed.Contains("No failed modules"))
                {
                    sb.AppendLine("FAILED MODULES DETECTED:");
                    sb.AppendLine(failed);
                }
            }


            sb.AppendLine("\n[SECTION 3: SIMULATION & POPULATION]");
            sb.AppendLine(CommandFullSimReport(EmptyArgs));


            sb.AppendLine("\n[SECTION 4: PERFORMANCE METRICS]");
            sb.AppendLine(GenerateReport());


            sb.AppendLine("\n[SECTION 5: AI & NEURAL INTELLIGENCE]");
            var doctrineSys = ModuleManager.Instance?.GetModule<AdaptiveAIDoctrineSystem>();
            if (doctrineSys != null)
            {
                sb.AppendLine("--- Tactical Doctrine ---");
                sb.AppendLine(doctrineSys.GetDiagnostics());
            }

            var neuralAdvisor = Intelligence.Neural.NeuralAdvisor.Instance;
            if (neuralAdvisor != null)
            {
                sb.AppendLine("--- Neural AI (Brain) ---");
                sb.AppendLine(neuralAdvisor.GetDiagnostics());
            }


            sb.AppendLine("\n[SECTION 6: STABILITY & EXCEPTIONS]");
            sb.AppendLine(Infrastructure.ExceptionMonitor.GetReport(50));

            sb.AppendLine("\n==================================================================");
            sb.AppendLine("   END OF AUDIT REPORT");
            sb.AppendLine("==================================================================");

            string finalReport = sb.ToString();
            string filename = $"Audit_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            SaveReportToFile(finalReport, filename);

            return finalReport + $"\n\n>>> AUDIT COMPLETE. Saved to: Warlord_Logs/BanditMilitias/Diagnostics/{filename}\n" +
                                 $">>> AI Logs: Warlord_Logs/BanditMilitias/AI/AIDecisions.log\n" +
                                 $">>> Neural Exports: Warlord_Logs/BanditMilitias/AI/exports/";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("help", "militia")]
        public static string CommandHelp(List<string> args)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== BANDIT MILITIAS CONSOLE COMMANDS ===");
            sb.AppendLine("You can use the following commands as 'militia.[command]':");
            sb.AppendLine("  help              - Shows this help menu.");
            sb.AppendLine("  help_ux           - UX commands guide (recommended for beginners).");
            sb.AppendLine("  list_parties      - Lists all active militias with their IDs and positions.");
            sb.AppendLine("  list_parties [x]  - Lists only militias containing 'x'.");
            sb.AppendLine("  reset_safe        - Safe reset with confirmation mechanism.");
            sb.AppendLine("  reset_safe confirm- Confirmed reset (all militia data will be deleted!).");
            sb.AppendLine("  dev_export_path   - Shows the current export directory.");
            sb.AppendLine("  dev_export_path [path] - Redirects export output to a custom directory.");
            sb.AppendLine("  module_status     - Lists the health status and errors of modules.");
            sb.AppendLine("  full_sim_test     - Provides a detailed status report of all systems.");
            sb.AppendLine("  full_sim_test once- Runs a one-time integration test.");
            sb.AppendLine("  diag_report       - Generates a performance and metrics report.");
            sb.AppendLine("  audit             - Collects ALL systems (Infrastructure, AI, Neural Network, Errors) into a single report and saves it to a file.");
            sb.AppendLine("  watchdog_status   - Shows the status of the system watchdog.");

            sb.AppendLine("  runtime_diag      - Live runtime diagnostic report.");
            sb.AppendLine("  dev_export        - Take a snapshot and export to DevDataCollector.");
            sb.AppendLine("  dev_status        - DevDataCollector session summary.");
            sb.AppendLine("  bandit.test_list  - Lists the runtime test hub check catalog.");
            sb.AppendLine("  bandit.test_run   - Runs the runtime test hub checks.");
            sb.AppendLine("  bandit.test_report- Shows the latest runtime test report.");
            sb.AppendLine("\nOther prefixes (Legacy commands):");
            sb.AppendLine("  bandit_militias.spawn_swarm - Starts a large bandit swarm.");
            sb.AppendLine("  bandit_militias.debug_hideout - Shows hideout data.");
            sb.AppendLine("========================================");
            sb.AppendLine("NOTE: This mod is single-player only. It does not work in multiplayer.");
            return sb.ToString();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("module_status", "militia")]
        public static string CommandModuleStatus(List<string> args)
        {
            if (ModuleManager.Instance == null) return "ModuleManager could not be initialized.";
            return ModuleManager.Instance.GetFailedModulesReport();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("full_sim_report", "militia")]
        public static string CommandFullSimReport(List<string> args)
        {
            var sb = new StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine("   BANDIT MILITIAS: FULL SIMULATION REPORT");
            sb.AppendLine($"   Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("========================================");


            sb.AppendLine("--- Deployment Readiness ---");
            float worldAge = ModActivationManager.GetActivationDelayElapsedDays();
            int requiredDays = Settings.Instance?.ActivationDelay ?? 2;
            bool isDelayed = ModActivationManager.IsGameplayActivationDelayed();
            int totalParties = Campaign.Current?.MobileParties?.Count ?? 0;
            int globalLimit = Settings.Instance?.GlobalPerformancePartyLimit ?? 3000;
            float partyFillPct = globalLimit > 0 ? (totalParties / (float)globalLimit) * 100f : 0f;

            string tierAccess = worldAge switch
            {
                < 10f  => "Tier 1-2 Only [Early Game — first ~3 weeks]",
                < 100f => "Tier 1-3        [Early-Mid — under ~1.2 years]",
                < 300f => "Tier 1-4+       [Mid Game — 1-3.5 years]",
                _      => "Tier 1-6 ELITE   [Late Game — 3.5+ years]"
            };

            sb.AppendLine($"  World Age        : {worldAge:F1} days (Required: {requiredDays} days)");
            sb.AppendLine($"  Activation       : {(isDelayed ? "PENDING (Passive)" : "ACTIVE (Live)")}");
            sb.AppendLine($"  Game Readiness   : {(ModActivationManager.IsGameFullyInitialized() ? "READY" : "WAITING")}");
            sb.AppendLine($"  Global Parties   : {totalParties} / {globalLimit} ({partyFillPct:F1}%) [{(totalParties > globalLimit ? "SUSPENDED" : "OK")}]");

            bool isPopCritical = (ModuleManager.Instance?.GetMilitiaCount() ?? 0) < 10;
            string spawnStatus = (totalParties > globalLimit)
                ? (isPopCritical ? "CRITICAL_RECOVERY (Limit exceeded but population low)" : "BLOCKED (Limit exceeded)")
                : "HEALTHY";
            sb.AppendLine($"  Spawn Status     : {spawnStatus}");
            sb.AppendLine($"  Troop Levels     : {tierAccess}");
            if (isDelayed)
            {
                float daysLeft = requiredDays - worldAge;
                sb.AppendLine($"  Time to Start    : {Math.Max(0f, daysLeft):F1} days");
            }
            sb.AppendLine("");

            sb.AppendLine("--- World Party Health ---");
            try
            {
                int zombieParties = 0;
                int headlessParties = 0;
                int militiaParties = 0;
                int vanillaBanditParties = 0;

                var allParties = Campaign.Current?.MobileParties;
                if (allParties != null)
                {
                    foreach (var party in allParties)
                    {
                        if (party == null || !party.IsActive) continue;

                        bool isMilitia = party.PartyComponent is BanditMilitias.Components.MilitiaPartyComponent;
                        if (isMilitia) militiaParties++;

                        string sid = party.StringId ?? string.Empty;
                        if (!isMilitia && sid.IndexOf("bandit", StringComparison.OrdinalIgnoreCase) >= 0)
                            vanillaBanditParties++;

                        if (party.MemberRoster == null || party.MemberRoster.TotalManCount <= 0)
                            zombieParties++;

                        if (party.ActualClan == null && party.LeaderHero == null)
                            headlessParties++;
                    }
                }

                sb.AppendLine($"  Zombie Parties   : {zombieParties}");
                sb.AppendLine($"  Headless Parties : {headlessParties}");
                sb.AppendLine($"  Militia Parties  : {militiaParties}");
                sb.AppendLine($"  Vanilla Bandits  : {vanillaBanditParties}");

                float zombieRatio = totalParties > 0 ? zombieParties / (float)totalParties : 0f;
                if (zombieRatio > 0.08f || headlessParties > 20)
                {
                    sb.AppendLine("  Health Alert     : HIGH (cleanup pressure recommended)");
                }
                else if (zombieRatio > 0.03f || headlessParties > 8)
                {
                    sb.AppendLine("  Health Alert     : MEDIUM");
                }
                else
                {
                    sb.AppendLine("  Health Alert     : LOW");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  [ERROR] {ex.Message}");
            }
            sb.AppendLine("");


            sb.AppendLine("--- Tier Distribution (Active Militias) ---");
            try
            {
                var tierCounts = new int[7];
                int totalSoldiers = 0;
                var militias = CompatibilityLayer.GetSafeMobileParties()
                    .Where(p => p.PartyComponent is BanditMilitias.Components.MilitiaPartyComponent)
                    .ToList() ?? new List<TaleWorlds.CampaignSystem.Party.MobileParty>();

                foreach (var mp in militias)
                    foreach (var elem in mp.MemberRoster.GetTroopRoster())
                    {
                        if (elem.Character == null || elem.Character.IsHero) continue;
                        int t = (int)MathF.Clamp(elem.Character.Tier, 0, 6);
                        tierCounts[t] += elem.Number;
                        totalSoldiers += elem.Number;
                    }

                if (totalSoldiers > 0)
                {
                    for (int t = 1; t <= 6; t++)
                    {
                        if (tierCounts[t] == 0) continue;
                        float pct = tierCounts[t] / (float)totalSoldiers * 100f;
                        string bar = new string('#', Math.Max(1, (int)(pct / 4f)));
                        sb.AppendLine($"  Tier {t}: {tierCounts[t],5} ({pct,5:F1}%) {bar}");
                    }
                    sb.AppendLine($"  Total : {totalSoldiers} soldiers in {militias.Count} parties");
                }
                else
                {
                    sb.AppendLine("  No active militia soldiers.");
                }
            }
            catch (Exception ex) { sb.AppendLine($"  [ERROR] {ex.Message}"); }
            sb.AppendLine("");


            sb.AppendLine(GenerateReport());


            sb.AppendLine(SystemWatchdog.Instance.GetStatusReport());


            if (ModuleManager.Instance != null)
            {
                sb.AppendLine("--- Population ---");
                sb.AppendLine($"Active Militias: {ModuleManager.Instance.GetMilitiaCount()}");
                sb.AppendLine($"Failed Modules: {ModuleManager.Instance.GetFailedModuleSummary()}");
            }


            var ws = BanditMilitias.Intelligence.Strategic.WarlordSystem.Instance;
            if (ws != null)
            {
                sb.AppendLine("--- Strategic Layer ---");
                var allWarlords = ws.GetAllWarlords();
                int aliveWarlords = allWarlords.Count(w => w.IsAlive);
                int tier3plus = allWarlords.Count(w => w.IsAlive && (int)WarlordCareerSystem.Instance.GetTier(w.StringId) >= 3);
                sb.AppendLine($"  Active Warlords : {aliveWarlords}");
                sb.AppendLine($"  Tier 3+ Warlords: {tier3plus}");
            }

            sb.AppendLine("--- Bandit Test Hub ---");
            sb.AppendLine(BanditTestHub.Instance.BuildSummaryLine());
            sb.AppendLine("  Commands         : bandit.test_list | bandit.test_run all | bandit.test_report");


            sb.AppendLine("");
            sb.AppendLine("--- New Systems Status ---");


            try
            {
                var seasonal = BanditMilitias.Systems.Seasonal.SeasonalEffectsSystem.Instance;
                sb.AppendLine($"  [Seasonal] {seasonal.GetSeasonDescription()}");
                sb.AppendLine($"    RaidMultiplier={seasonal.RaidLootMultiplier:F2}  SpeedMultiplier={seasonal.SpeedMultiplier:F2}  WinterAttrition={seasonal.WinterAttritionRisk:P0}");
            }
            catch { sb.AppendLine("  [Seasonal] Not initialized."); }


            try
            {
                var morale = BanditMilitias.Systems.Combat.MilitiaMoraleSystem.Instance;
                sb.AppendLine($"  [Morale] {morale.GetDiagnostics().Replace("\n", "\n    ")}");
            }
            catch { sb.AppendLine("  [Morale] Not initialized."); }


            try
            {
                var tax = BanditMilitias.Systems.Economy.CaravanTaxSystem.Instance;
                sb.AppendLine($"  [CaravanTax] {tax.GetDiagnostics().Replace("\n", "\n    ")}");
            }
            catch { sb.AppendLine("  [CaravanTax] Not initialized."); }


            try
            {
                var succession = BanditMilitias.Systems.Progression.WarlordSuccessionSystem.Instance;
                sb.AppendLine($"  [Succession] {succession.GetDiagnostics().Replace("\n", "\n    ")}");
            }
            catch { sb.AppendLine("  [Succession] Not initialized."); }


            try
            {
                var workshop = BanditMilitias.Systems.Workshop.WarlordWorkshopSystem.Instance;
                sb.AppendLine($"  [Workshop] {workshop.GetDiagnostics()}");
            }
            catch { sb.AppendLine("  [Workshop] Not initialized."); }

            string fullReport = sb.ToString();
            SaveReportToFile(fullReport, "BanditMilitias_FullSim.txt");

            return fullReport + "\n\n[SUCCESS] Full simulation state exported to Warlord_Logs/BanditMilitias/Diagnostics/BanditMilitias_FullSim.txt";
        }

        private static void SaveReportToFile(string content, string fileName)
        {
            try
            {
                string logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord", "Warlord_Logs", "BanditMilitias", "Diagnostics");

                if (!System.IO.Directory.Exists(logDir))
                    System.IO.Directory.CreateDirectory(logDir);

                string filePath = System.IO.Path.Combine(logDir, fileName);
                System.IO.File.WriteAllText(filePath, content);
            }
            catch (Exception ex)
            {
                TaleWorlds.Library.Debug.Print($"[BanditMilitias] Failed to save report {fileName}: {ex.Message}");
            }
        }
    }


    public class SystemWatchdog
    {
        private static SystemWatchdog? _instance;
        public static SystemWatchdog Instance => _instance ??= new SystemWatchdog();

        private const float TIMEOUT_HOURS = 26f;
        private const float NEURAL_TIMEOUT_HOURS = 12f;

        private struct MonitoredSystem
        {
            public string Name;
            public CampaignTime LastHeartbeat;
            public Action? RestartAction;
            public int RestartCount;


            public bool IsActivated;
        }

        private readonly Dictionary<string, MonitoredSystem> _monitoredSystems = new();

        private SystemWatchdog() { }


        public void RegisterComponent(string name, Action? restartAction)
        {


            _monitoredSystems[name] = new MonitoredSystem
            {
                Name = name,
                LastHeartbeat = CampaignTime.Zero,

                RestartAction = restartAction,
                RestartCount = 0,
                IsActivated = false

            };

            TaleWorlds.Library.Debug.Print($"[Watchdog] Registered: {name} (heartbeat pending)");
        }

        public void ReportHeartbeat(string name)
        {
            if (Campaign.Current == null) return;


            if (_monitoredSystems.TryGetValue(name, out var sys))
            {
                sys.LastHeartbeat = CampaignTime.Now;

                sys.IsActivated = true;
                _monitoredSystems[name] = sys;
            }
        }

        public void CheckSystems()
        {
            if (Campaign.Current == null) return;

            foreach (var key in _monitoredSystems.Keys)
            {
                if (!_monitoredSystems.TryGetValue(key, out var sys)) continue;


                if (!sys.IsActivated)
                {
                    sys.LastHeartbeat = CampaignTime.Now;
                    sys.IsActivated = true;
                    _monitoredSystems[key] = sys;
                    continue;
                }

                double hoursSinceBeat = CampaignTime.Now.ToHours - sys.LastHeartbeat.ToHours;
                double timeoutHours = string.Equals(sys.Name, "NeuralActivity",
                    StringComparison.OrdinalIgnoreCase)
                    ? NEURAL_TIMEOUT_HOURS
                    : TIMEOUT_HOURS;

                if (hoursSinceBeat > timeoutHours)
                    HandleSystemFailure(sys);
            }
        }

        private void HandleSystemFailure(MonitoredSystem sys)
        {
            string alert = $"[Watchdog] CRITICAL FAILURE DETECTED: {sys.Name} stopped responding!";
            InformationManager.DisplayMessage(new InformationMessage(alert, Colors.Red));
            TaleWorlds.Library.Debug.Print(alert);

            try
            {
                if (sys.RestartAction == null)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[Watchdog] {sys.Name} is silent, but has no manual override. Monitor closely.",
                        Colors.Yellow));
                    return;
                }

                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Watchdog] Attempting Emergency Restart of {sys.Name}...", Colors.Yellow));
                sys.RestartAction.Invoke();

                sys.RestartCount++;
                sys.LastHeartbeat = CampaignTime.Now;
                sys.IsActivated = true;
                _monitoredSystems[sys.Name] = sys;

                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Watchdog] {sys.Name} restarted successfully. (Total Resets: {sys.RestartCount})",
                    Colors.Green));
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Watchdog] RESTART FAILED! Mod may be unstable. ({ex.Message})", Colors.Red));
            }
        }


        public void Clear()
        {
            _monitoredSystems.Clear();
            TaleWorlds.Library.Debug.Print("[Watchdog] All monitored systems cleared.");
        }

        public string GetStatusReport()
        {
            if (Campaign.Current == null)
                return "[Watchdog] Campaign not active.";

            var sb = new System.Text.StringBuilder();
            _ = sb.AppendLine("--- FUSE BOX STATUS ---");
            foreach (var kvp in _monitoredSystems)
            {
                if (!kvp.Value.IsActivated)
                {
                    _ = sb.AppendLine($"[PENDING] {kvp.Key}: awaiting first heartbeat");
                    continue;
                }
                double hours = CampaignTime.Now.ToHours - kvp.Value.LastHeartbeat.ToHours;
                double timeoutHours = string.Equals(kvp.Key, "NeuralActivity",
                    StringComparison.OrdinalIgnoreCase)
                    ? NEURAL_TIMEOUT_HOURS : TIMEOUT_HOURS;
                string status = hours < timeoutHours ? "OK" : "DEAD";
                _ = sb.AppendLine($"[{status}] {kvp.Key}: Last Beat {hours:F1}h ago (Resets: {kvp.Value.RestartCount})");
            }
            return sb.ToString();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("watchdog_status", "militia")]
        public static string CommandWatchdogStatus(List<string> args)
        {
            return Instance.GetStatusReport();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("watchdog_check", "militia")]
        public static string CommandWatchdogCheck(List<string> args)
        {
            Instance.CheckSystems();
            return "Watchdog check executed.";
        }
    }
}
