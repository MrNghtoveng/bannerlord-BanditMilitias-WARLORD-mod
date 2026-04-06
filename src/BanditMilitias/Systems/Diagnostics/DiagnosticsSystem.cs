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

namespace BanditMilitias.Systems.Diagnostics
{

    public static class DiagnosticsSystem
    {

        private const double CRITICAL_FRAME_TIME_MS = 3.0;

        private static readonly Dictionary<string, Stopwatch> _activeScopes = new();
        private static readonly Dictionary<string, double> _averageTimes = new();
        private static readonly Dictionary<string, long> _callCounts = new();

        private static readonly Dictionary<string, float> _customMetrics = new();

        public static bool IsHighLoad { get; private set; } = false;

        [Conditional("DEBUG"), Conditional("RELEASE")]
        public static void StartScope(string scopeName)
        {
            var sw = new Stopwatch();
            _activeScopes[scopeName] = sw;
            sw.Start();
        }

        [Conditional("DEBUG"), Conditional("RELEASE")]
        public static void EndScope(string scopeName)
        {
            if (_activeScopes.TryGetValue(scopeName, out var sw))
            {
                _ = _activeScopes.Remove(scopeName);
                sw.Stop();
                double elapsedMs = sw.Elapsed.TotalMilliseconds;

                if (_averageTimes.TryGetValue(scopeName, out double oldAvg))
                    _averageTimes[scopeName] = (oldAvg * 0.95) + (elapsedMs * 0.05);
                else
                    _averageTimes[scopeName] = elapsedMs;

                if (_callCounts.TryGetValue(scopeName, out long oldCount))
                    _callCounts[scopeName] = oldCount + 1;
                else
                    _callCounts[scopeName] = 1;

                CheckSystemLoad();
            }
        }

        private static void CheckSystemLoad()
        {
            // FIX: Önceden "AI" scope'una bakılıyordu — bu scope hiç kaydedilmiyordu
            // (MilitiaBehavior "Militia.HourlyTick" ve "Militia.DailyTick" kullanıyor),
            // dolayısıyla IsHighLoad her zaman false kalıyordu ve yük kısıtlaması hiç
            // devreye girmiyordu. Doğru scope isimleri ile kontrol ediyoruz.
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
            if (_customMetrics.TryGetValue(key, out float oldVal))
                _customMetrics[key] = oldVal + delta;
            else
                _customMetrics[key] = delta;
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("diag_report", "militia")]
        public static string CommandDiagReport(List<string> args)
        {
            string report = GenerateReport();
            SaveReportToFile(report, "BanditMilitias_Diag.txt");
            return report + "\n(Report also saved to Logs/BanditMilitias_Diag.txt)";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("help", "militia")]
        public static string CommandHelp(List<string> args)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== BANDİT MİLİTİAS KONSOL KOMUTLARI ===");
            sb.AppendLine("Aşağıdaki komutları 'militia.[komut]' şeklinde kullanabilirsiniz:");
            sb.AppendLine("  help              - Bu yardım menüsünü gösterir.");
            sb.AppendLine("  module_status     - Modüllerin sağlık durumunu ve hataları listeler.");
            sb.AppendLine("  full_sim_test     - Tüm sistemlerin detaylı durum raporunu verir.");
            sb.AppendLine("  diag_report       - Performans ve metrik raporu oluşturur.");
            sb.AppendLine("  watchdog_status   - Sistem nöbetçisi (watchdog) durumunu gösterir.");
            sb.AppendLine("  bandit.test_list  - Runtime test hub check kataloğunu listeler.");
            sb.AppendLine("  bandit.test_run   - Runtime test hub check'lerini çalıştırır.");
            sb.AppendLine("  bandit.test_report- Son runtime test raporunu gösterir.");
            sb.AppendLine("  spawn             - Rastgele bir milis ordusu doğurur.");
            sb.AppendLine("  reset             - Tüm milis verilerini sıfırlar.");
            sb.AppendLine("\nDiğer ön ekler (Eski komutlar):");
            sb.AppendLine("  bandit_militias.spawn_swarm - Büyük bir haydut akını başlatır.");
            sb.AppendLine("  bandit_militias.debug_hideout - Sığınak verilerini gösterir.");
            sb.AppendLine("========================================");
            return sb.ToString();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("module_status", "militia")]
        public static string CommandModuleStatus(List<string> args)
        {
            if (ModuleManager.Instance == null) return "ModuleManager başlatılamadı.";
            return ModuleManager.Instance.GetFailedModulesReport();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("full_sim_report", "militia")]
        public static string CommandFullSimReport(List<string> args)
        {
            var sb = new StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine("   BANDIT MILITIAS: FULL SIM REPORT");
            sb.AppendLine($"   Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("========================================");

            // 1. Deployment Readiness (Absolute Time & Limits)
            sb.AppendLine("--- Deployment Readiness ---");
            float worldAge = CompatibilityLayer.GetActivationDelayElapsedDays();
            int requiredDays = Settings.Instance?.ActivationDelay ?? 2;
            bool isDelayed = CompatibilityLayer.IsGameplayActivationDelayed();
            int totalParties = Campaign.Current?.MobileParties?.Count ?? 0;
            int globalLimit = Settings.Instance?.GlobalPerformancePartyLimit ?? 3000;
            float partyFillPct = globalLimit > 0 ? (totalParties / (float)globalLimit) * 100f : 0f;

            string tierAccess = worldAge switch
            {
                < 10f  => "Tier 1-2 only  [Early Game  — ilk ~3 hafta]",
                < 100f => "Tier 1-3       [Mid-Early  — ~1.2 yıl altı]",
                < 300f => "Tier 1-4+      [Mid Game   — 1-3.5 yıl, Tier 5-6 kademeli]",
                _      => "Tier 1-6 ELITE [Late Game  — 3.5+ yıl (300+ gün, ~%15 elit)]"
            };

            sb.AppendLine($"  World Age        : {worldAge:F1} days  (Required: {requiredDays}d)");
            sb.AppendLine($"  Activation Switch: {(isDelayed ? "DELAYED (Passive)" : "ENERGIZED (Active)")}");
            sb.AppendLine($"  Game Init        : {(CompatibilityLayer.IsGameFullyInitialized() ? "READY" : "WAITING")}");
            sb.AppendLine($"  Global Parties   : {totalParties} / {globalLimit} ({partyFillPct:F1}%) [{(totalParties > globalLimit ? "BLOCKED" : "OK")}]");
            sb.AppendLine($"  Troop Tier Access: {tierAccess}");
            if (isDelayed)
            {
                float daysLeft = requiredDays - worldAge;
                sb.AppendLine($"  Spawn Starts In  : {Math.Max(0f, daysLeft):F1} days");
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

            // 1b. Tier Dağılımı — aktif milisyalardaki asker kalitesi
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

            // 2. Diagnostics (Performance)
            sb.AppendLine(GenerateReport());

            // 2. System Watchdog (Health)
            sb.AppendLine(SystemWatchdog.Instance.GetStatusReport());

            // 3. Module Status
            if (ModuleManager.Instance != null)
            {
                sb.AppendLine("--- Population ---");
                sb.AppendLine($"Active Militias: {ModuleManager.Instance.GetMilitiaCount()}");
                sb.AppendLine($"Failed Modules: {ModuleManager.Instance.GetFailedModuleSummary()}");
            }

            // 4. ML / QiRL Status
            var ml = ModuleManager.Instance?.GetModule<BanditMilitias.Intelligence.ML.AILearningSystem>();
            if (ml != null)
            {
                sb.AppendLine("--- Machine Learning (QiRL) ---");
                sb.AppendLine(ml.GetDiagnostics());
            }

            // 5. Warlord Strategic Status
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

            // 6. Yeni Sistemler Durumu
            sb.AppendLine("");
            sb.AppendLine("--- New Systems Status ---");

            // Seasonal
            try
            {
                var seasonal = BanditMilitias.Systems.Seasonal.SeasonalEffectsSystem.Instance;
                sb.AppendLine($"  [Seasonal] {seasonal.GetSeasonDescription()}");
                sb.AppendLine($"    RaidMultiplier={seasonal.RaidLootMultiplier:F2}  SpeedMultiplier={seasonal.SpeedMultiplier:F2}  WinterAttrition={seasonal.WinterAttritionRisk:P0}");
            }
            catch { sb.AppendLine("  [Seasonal] Not initialized."); }

            // Morale
            try
            {
                var morale = BanditMilitias.Systems.Combat.MilitiaMoraleSystem.Instance;
                sb.AppendLine($"  [Morale] {morale.GetDiagnostics().Replace("\n", "\n    ")}");
            }
            catch { sb.AppendLine("  [Morale] Not initialized."); }

            // CaravanTax
            try
            {
                var tax = BanditMilitias.Systems.Economy.CaravanTaxSystem.Instance;
                sb.AppendLine($"  [CaravanTax] {tax.GetDiagnostics().Replace("\n", "\n    ")}");
            }
            catch { sb.AppendLine("  [CaravanTax] Not initialized."); }

            // Succession
            try
            {
                var succession = BanditMilitias.Systems.Progression.WarlordSuccessionSystem.Instance;
                sb.AppendLine($"  [Succession] {succession.GetDiagnostics().Replace("\n", "\n    ")}");
            }
            catch { sb.AppendLine("  [Succession] Not initialized."); }

            // Workshop
            try
            {
                var workshop = BanditMilitias.Systems.Workshop.WarlordWorkshopSystem.Instance;
                sb.AppendLine($"  [Workshop] {workshop.GetDiagnostics()}");
            }
            catch { sb.AppendLine("  [Workshop] Not initialized."); }

            string fullReport = sb.ToString();
            SaveReportToFile(fullReport, "BanditMilitias_FullSim.txt");
            
            return fullReport + "\n\n[SUCCESS] Full simulation state exported to Logs/BanditMilitias_FullSim.txt";
        }

        private static void SaveReportToFile(string content, string fileName)
        {
            try
            {
                string logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), 
                    "Mount and Blade II Bannerlord", "Logs");
                
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


    // ── SystemWatchdog ─────────────────────────────────────────
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
            // FIX #1: Kampanya hazır mı bayrağı - CampaignTime.Now'u erken çağırmamak için
            public bool IsActivated;
        }

        private readonly Dictionary<string, MonitoredSystem> _monitoredSystems = new();

        private SystemWatchdog() { }

        /// <summary>
        /// Bileşeni kaydeder. CampaignTime.Now ÇAĞIRILMAZ — kampanya henüz hazır olmayabilir.
        /// Saat damgası ilk ReportHeartbeat() veya CheckSystems() çağrısında set edilir.
        /// </summary>
        public void RegisterComponent(string name, Action? restartAction)
        {
            // FIX #1: CampaignTime.Now yerine CampaignTime.Zero kullan.
            // OnGameStart / InitializeAll sırasında CampaignTime.Now TaleWorlds native
            // zamanlayıcısına erişir ve kampanya tam hazır değilken AccessViolationException
            // ya da NullReferenceException fırlatır. LastHeartbeat'i sıfır bırakıp
            // ilk CheckSystems() çağrısında güvenli şekilde set ediyoruz.
            _monitoredSystems[name] = new MonitoredSystem
            {
                Name = name,
                LastHeartbeat = CampaignTime.Zero,   // ← güvenli: native çağrı yok
                RestartAction = restartAction,
                RestartCount = 0,
                IsActivated = false                   // ← ilk heartbeat gelince true olacak
            };

            TaleWorlds.Library.Debug.Print($"[Watchdog] Registered: {name} (heartbeat pending)");
        }

        public void ReportHeartbeat(string name)
        {
            if (Campaign.Current == null) return; // kampanya yoksa dokunma

            if (_monitoredSystems.TryGetValue(name, out var sys))
            {
                sys.LastHeartbeat = CampaignTime.Now; // artık kampanya hazır
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

                // FIX #1: Henüz heartbeat almamış bileşenleri ilk geçişte aktive et,
                // timeout sayacını şimdiden başlat — erken alarm verme
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

        // FIX #3: OnGameEnd'de çağrılmalı — bir sonraki oturumda eski CampaignTime
        // değerleri kalmasın, sahte timeout alarmı tetiklenmesin
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
