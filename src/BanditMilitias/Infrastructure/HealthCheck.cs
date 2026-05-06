using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace BanditMilitias.Infrastructure
{
    /// <summary>
    /// Comprehensive health check system for mod diagnostics
    /// </summary>
    public static class HealthCheck
    {
        public enum Severity { Info, Warning, Critical }

        public class HealthIssue
        {
            public string Component { get; set; } = "";
            public string Description { get; set; } = "";
            public Severity Severity { get; set; }
            public bool AutoFixed { get; set; }
        }

        public class HealthReport
        {
            public List<HealthIssue> Issues { get; } = new();
            public DateTime CheckTime { get; set; } = DateTime.Now;
            public bool HasCriticalIssues => Issues.Any(i => i.Severity == Severity.Critical);
            public bool HasWarnings => Issues.Any(i => i.Severity == Severity.Warning);
            public int IssueCount => Issues.Count;

            public void AddIssue(string component, string description, Severity severity, bool autoFixed = false)
            {
                Issues.Add(new HealthIssue
                {
                    Component = component,
                    Description = description,
                    Severity = severity,
                    AutoFixed = autoFixed
                });
            }

            public override string ToString()
            {
                var lines = new List<string>
                {
                    $"=== Health Report ({CheckTime:HH:mm:ss}) ===",
                    $"Total Issues: {IssueCount} (Critical: {Issues.Count(i => i.Severity == Severity.Critical)}, Warning: {Issues.Count(i => i.Severity == Severity.Warning)})"
                };

                foreach (var issue in Issues.OrderByDescending(i => i.Severity))
                {
                    var icon = issue.Severity switch
                    {
                        Severity.Critical => "🔴",
                        Severity.Warning => "🟡",
                        _ => "🟢"
                    };
                    var fixedText = issue.AutoFixed ? " [AUTO-FIXED]" : "";
                    lines.Add($"{icon} [{issue.Component}] {issue.Description}{fixedText}");
                }

                return string.Join("\n", lines);
            }
        }

        /// <summary>
        /// Run full diagnostics and return report
        /// </summary>
        public static HealthReport RunDiagnostics(bool autoFix = true)
        {
            var report = new HealthReport();

            // 1. Globals Check
            CheckGlobals(report, autoFix);

            // 2. ClanCache Check
            CheckClanCache(report, autoFix);

            // 3. Settings Validation
            CheckSettings(report, autoFix);

            // 4. Dependencies Check
            CheckDependencies(report);

            // 5. Module Manager Check
            CheckModuleManager(report);

            // 6. Module Registry Check
            CheckModuleRegistry(report);

            // 7. Game State Check
            CheckGameState(report);

            // 8. Memory Check
            CheckMemory(report);

            return report;
        }

        private static void CheckGlobals(HealthReport report, bool autoFix)
        {
            try
            {
                if (Core.Config.Globals.BasicInfantry.Count == 0)
                {
                    if (autoFix)
                    {
                        Core.Config.Globals.Initialize(force: true);

                        if (Core.Config.Globals.BasicInfantry.Count > 0)
                        {
                            report.AddIssue("Globals", "BasicInfantry boştu, initialize edildi",
                                Severity.Warning, true);
                        }
                        else
                        {
                            report.AddIssue("Globals", "BasicInfantry boş ve initialize başarısız!",
                                Severity.Critical);
                        }
                    }
                    else
                    {
                        report.AddIssue("Globals", "BasicInfantry boş", Severity.Critical);
                    }
                }

                if (!Core.Config.Globals.IsInitialized)
                {
                    report.AddIssue("Globals", "Globals tam olarak initialize edilmemiş", Severity.Warning);
                }
            }
            catch (Exception ex)
            {
                report.AddIssue("Globals", $"Exception: {ex.Message}", Severity.Critical);
            }
        }

        private static void CheckClanCache(HealthReport report, bool autoFix)
        {
            try
            {
                if (!ClanCache.IsInitialized)
                {
                    if (autoFix)
                    {
                        ClanCache.Initialize();
                        report.AddIssue("ClanCache", "Başlatılmamıştı, initialize edildi",
                            Severity.Warning, true);
                    }
                    else
                    {
                        report.AddIssue("ClanCache", "Başlatılmamış", Severity.Critical);
                    }
                }

                var lootersClan = ClanCache.GetLootersClan();
                var fallbackClan = ClanCache.GetFallbackBanditClan();

                if (lootersClan == null && fallbackClan == null)
                {
                    report.AddIssue("ClanCache", "Hiç bandit klanı bulunamadı!", Severity.Critical);
                }
            }
            catch (Exception ex)
            {
                report.AddIssue("ClanCache", $"Exception: {ex.Message}", Severity.Critical);
            }
        }

        private static void CheckSettings(HealthReport report, bool autoFix)
        {
            try
            {
                if (Settings.Instance == null)
                {
                    report.AddIssue("Settings", "Settings.Instance null!", Severity.Critical);
                    return;
                }

                // Validate settings
                if (autoFix)
                {
                    var beforeCount = report.IssueCount;
                    Settings.Instance.ValidateAndClampSettings();

                    // Check if any values were changed
                    if (report.IssueCount > beforeCount)
                    {
                        report.AddIssue("Settings", "Geçersiz değerler otomatik düzeltildi",
                            Severity.Warning, true);
                    }
                }

                // Check specific values
                if (Settings.Instance.MaxTotalMilitias < 1)
                {
                    report.AddIssue("Settings", "MaxTotalMilitias geçersiz", Severity.Critical);
                }

                if (Settings.Instance.ActivationDelay < 0)
                {
                    report.AddIssue("Settings", "ActivationDelay negatif", Severity.Warning);
                }
            }
            catch (Exception ex)
            {
                report.AddIssue("Settings", $"Exception: {ex.Message}", Severity.Critical);
            }
        }

        private static void CheckDependencies(HealthReport report)
        {
            // Check MCM
            bool mcmLoaded = AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name?.Contains("MCM") == true);

            if (!mcmLoaded)
            {
                report.AddIssue("Dependencies", "MCM (Mod Configuration Menu) yüklenmemiş",
                    Severity.Warning);
            }

            // Check TaleWorlds assemblies
            var requiredAssemblies = new[] { "TaleWorlds.CampaignSystem", "TaleWorlds.Core", "TaleWorlds.Library" };
            foreach (var asm in requiredAssemblies)
            {
                bool loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .Any(a => a.GetName().Name == asm);
                if (!loaded)
                {
                    report.AddIssue("Dependencies", $"{asm} yüklenmemiş!", Severity.Critical);
                }
            }
        }

        private static void CheckModuleManager(HealthReport report)
        {
            try
            {
                var mm = ModuleManager.Instance;
                if (mm == null)
                {
                    report.AddIssue("ModuleManager", "Instance null!", Severity.Critical);
                    return;
                }

                var militiaCount = mm.GetMilitiaCount();
                if (militiaCount < 0)
                {
                    report.AddIssue("ModuleManager", "GetMilitiaCount() geçersiz değer döndürdü",
                        Severity.Warning);
                }

                // Check for too many militias
                if (Settings.Instance != null && militiaCount > Settings.Instance.MaxTotalMilitias * 2)
                {
                    report.AddIssue("ModuleManager", $"Militia sayısı çok yüksek: {militiaCount}",
                        Severity.Warning);
                }
            }
            catch (Exception ex)
            {
                report.AddIssue("ModuleManager", $"Exception: {ex.Message}", Severity.Critical);
            }
        }

        private static void CheckModuleRegistry(HealthReport report)
        {
            try
            {
                var audit = Core.Registry.ModuleRegistry.Instance.Audit();
                AddRegistryIssues(report, "Ghost modules", audit.Unregistered);
                AddRegistryIssues(report, "Failed modules", audit.Failed);
                AddRegistryIssues(report, "Silent broken modules", audit.SilentBroken);
                AddRegistryIssues(report, "Stale modules", audit.Stale);
                AddRegistryIssues(report, "Dead modules", audit.Dead);
                AddRegistryIssues(report, "Event leak modules", audit.EventLeaks);
            }
            catch (Exception ex)
            {
                report.AddIssue("ModuleRegistry", $"Exception: {ex.Message}", Severity.Warning);
            }
        }

        private static void CheckGameState(HealthReport report)
        {
            try
            {
                if (Campaign.Current == null)
                {
                    report.AddIssue("GameState", "Campaign.Current null", Severity.Info);
                    return;
                }

                if (Hero.MainHero == null)
                {
                    report.AddIssue("GameState", "Hero.MainHero null", Severity.Warning);
                }

                if (MobileParty.MainParty == null)
                {
                    report.AddIssue("GameState", "MobileParty.MainParty null", Severity.Warning);
                }

                // Check campaign time
                var elapsedDays = GetElapsedDays();
                if (elapsedDays < 0)
                {
                    report.AddIssue("GameState", "Campaign zamanı geçersiz", Severity.Warning);
                }
            }
            catch (Exception ex)
            {
                report.AddIssue("GameState", $"Exception: {ex.Message}", Severity.Warning);
            }
        }

        private static void CheckMemory(HealthReport report)
        {
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                var memMB = proc.WorkingSet64 / (1024 * 1024);

                if (memMB > 4096) // 4GB
                {
                    report.AddIssue("Memory", $"Yüksek bellek kullanımı: {memMB} MB", Severity.Warning);
                }

                // Check object pool stats
                var poolStats = TroopRosterPool.GetDiagnostics();
                if (TroopRosterPool.Created > 10000)
                {
                    report.AddIssue("Memory", $"Çok fazla TroopRoster oluşturuldu: {poolStats}",
                        Severity.Warning);
                }
            }
            catch (Exception ex)
            {
                report.AddIssue("Memory", $"Exception: {ex.Message}", Severity.Info);
            }
        }

        /// <summary>
        /// Display health report in-game
        /// </summary>
        public static void DisplayReport(HealthReport report)
        {
            if (report.HasCriticalIssues)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BanditMilitias] KRİTİK: Mod düzgün başlatılamadı! Detaylar için logları kontrol edin.",
                    Colors.Red));
            }
            else if (report.HasWarnings)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[BanditMilitias] Uyarı: {report.IssueCount} sorun tespit edildi, bazıları otomatik düzeltildi.",
                    Colors.Yellow));
            }
            else if (Settings.Instance?.TestingMode == true)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BanditMilitias] Tüm sistemler normal çalışıyor ✓",
                    Colors.Green));
            }
        }

        private static float GetElapsedDays()
        {
            try
            {
                var startTime = CompatibilityLayer.GetCampaignStartTime();
                if (startTime == CampaignTime.Zero) return 0f;
                return (float)(CampaignTime.Now - startTime).ToDays;
            }
            catch
            {
                return -1f;
            }
        }

        private static void AddRegistryIssues(HealthReport report, string label, List<Core.Registry.ModuleEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return;
            }

            bool hasCritical = entries.Any(e => e.IsCritical);
            string preview = string.Join(", ", entries.Take(3).Select(e => e.DisplayName));
            if (entries.Count > 3)
            {
                preview += ", ...";
            }

            report.AddIssue(
                "ModuleRegistry",
                $"{label}: {entries.Count} ({preview})",
                hasCritical ? Severity.Critical : Severity.Warning);
        }
    }
}
