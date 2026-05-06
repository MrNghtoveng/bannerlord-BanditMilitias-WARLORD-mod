using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BanditMilitias.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Bounty;
using BanditMilitias.Systems.Fear;
using BanditMilitias.Systems.WarlordLegitimacy;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Core.Components;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.AI;
using BanditMilitias.Systems.Spawning;
using BanditMilitias.Systems.Diagnostics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace BanditMilitias.Debug
{


    public static class DebugLogger
    {
        private static readonly object _logLock = new object();
        private static readonly List<LogEntry> _recentLogs = new List<LogEntry>();
        private const int MAX_RECENT_LOGS = 100;

        public enum LogLevel { Debug, Info, Warning, Error }

        public struct LogEntry
        {
            public DateTime Timestamp;
            public string Category;
            public string Message;
            public LogLevel Level;
            public Dictionary<string, object>? Context;
        }

        public static bool IsLevelEnabled(LogLevel level)
        {
            if (level >= LogLevel.Warning)
            {
                return true;
            }

            bool verbose = Settings.Instance?.DevMode == true || Settings.Instance?.TestingMode == true;
            if (verbose)
            {
                return true;
            }

            return level == LogLevel.Info && Settings.Instance?.EnableFileLogging == true;
        }

        public static void Log(string category, string message, LogLevel level = LogLevel.Info, Dictionary<string, object>? context = null)
        {
            if (string.IsNullOrWhiteSpace(message) || !IsLevelEnabled(level))
            {
                return;
            }

            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Category = category,
                Message = message,
                Level = level,
                Context = context
            };

            lock (_logLock)
            {
                _recentLogs.Add(entry);
                if (_recentLogs.Count > MAX_RECENT_LOGS) _recentLogs.RemoveAt(0);
            }

            bool mirrorToFile = Settings.Instance?.EnableFileLogging == true || level >= LogLevel.Warning;
            if (mirrorToFile)
            {
                string prefix = level switch
                {
                    LogLevel.Error => "[ERROR]",
                    LogLevel.Warning => "[WARNING]",
                    LogLevel.Debug => "[DEBUG]",
                    _ => "[INFO]"
                };
                FileLogger.Log($"{prefix} [{category}] {message}");
            }

            if (Settings.Instance?.DevMode == true)
            {
                TaleWorlds.Library.Debug.Print($"[BM:{category}] {message}");
            }
        }

        /// <summary>Convenience overload: logs to the "General" category at Info level.</summary>
        public static void Log(string message) => Log("General", message, LogLevel.Info);

        public static void Info(string category, string message) => Log(category, message, LogLevel.Info);
        public static void Warning(string category, string message) => Log(category, message, LogLevel.Warning);
        public static void Error(string category, string message) => Log(category, message, LogLevel.Error);
        public static void Warn(string category, string message) => Log(category, message, LogLevel.Warning);
        public static void Debug(string category, string message) => Log(category, message, LogLevel.Debug);

        public static void TestLog(string message) => TestLog(message, Colors.Cyan);
        public static void TestLog(string message, Color color)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            try
            {
                Infrastructure.FileLogger.Log($"[TEST] {message}");
                if (ShouldShowMessages())
                    Infrastructure.UiNotifier.TryShow(message, color, "DebugLogger");
            }
            catch { }
        }

        private static bool ShouldShowMessages()
            => Settings.Instance?.ShowTestMessages == true || Settings.Instance?.TestingMode == true;

        public static void LogInitialization(string component, bool success, string? details = null)
        {
            Log("Init", $"{component} {(success ? "initialized" : "failed")}",
                success ? LogLevel.Info : LogLevel.Error,
                new Dictionary<string, object> { ["details"] = details ?? "none" });
        }


        public static void LogPerformance(string operation, long elapsedMs, Dictionary<string, object>? context = null)
        {
            context ??= new Dictionary<string, object>();
            context["elapsed_ms"] = elapsedMs;

            var level = elapsedMs > 100 ? LogLevel.Warning : LogLevel.Debug;
            Log("Performance", operation, level, context);
        }


        public static IEnumerable<LogEntry> GetRecentLogs(int count = 20)
        {
            lock (_logLock)
            {
                if (count <= 0) return new List<LogEntry>();
                int skip = Math.Max(0, _recentLogs.Count - count);
                return _recentLogs.Skip(skip).ToList();
            }
        }

        private static float GetElapsedDays()
        {
            try
            {
                return Infrastructure.ModActivationManager.GetActivationDelayElapsedDays();
            }
            catch
            {
                return 0f;
            }
        }
    }


    public static class VerificationCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("verify_contract", "bandit")]
        public static string VerifyContract(List<string> args)
        {
            if (Campaign.Current == null) return "Campaign not loaded.";

            var militia = MobileParty.All.FirstOrDefault(p => p.PartyComponent is MilitiaPartyComponent);
            if (militia == null) return "No active militia party found.";

            var component = (MilitiaPartyComponent)militia.PartyComponent;

            var cmd = new StrategicCommand
            {
                Type = CommandType.Defend,
                TargetLocation = BanditMilitias.Infrastructure.CompatibilityLayer.GetPartyPosition(militia),
                Reason = "Verification Test"
            };


            if (Settings.Instance?.DevMode != true)
                return "Contract verification requires DevMode=true (developer-only command).";

            component.OrderTimestamp = CampaignTime.Now;
            component.CurrentOrder = cmd;


            bool isContractActive = CustomMilitiaAI.IsCommandActive(component);

            CustomMilitiaAI.UpdateTacticalDecision(militia);

            return $"Contract Verification:\n" +
                   $"- Militia: {militia.Name}\n" +
                   $"- Order: {cmd.Type}\n" +
                   $"- Active: {isContractActive}\n" +
                   $"- Wounded: {CustomMilitiaAI.IsPartyWounded(militia)}\n" +
                   "Result: " + (isContractActive ? "PASS (Order dictates behavior)" : "FAIL (Order ignored or expired)");
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("simulate_learning", "bandit")]
        public static string SimulateLearning(List<string> args)
        {
            if (BanditBrain.Instance == null) return "BanditBrain not initialized.";

            var militia = MobileParty.All.FirstOrDefault(p => p.PartyComponent is MilitiaPartyComponent);
            if (militia == null)
            {
                var spawner = ModuleManager.Instance.GetModule<MilitiaSpawningSystem>();
                var hideout = ModuleManager.Instance.HideoutCache.FirstOrDefault(s => s.IsHideout && s.IsActive);
                if (spawner != null && hideout != null)
                {
                    militia = spawner.SpawnMilitia(hideout, force: true);
                }
            }

            if (militia == null)
            {
                return "No militia available to simulate learning.";
            }

            for (int i = 0; i < 5; i++)
            {
                var evt = new BanditMilitias.Core.Events.CommandCompletionEvent
                {
                    Command = new StrategicCommand { Type = CommandType.Hunt },
                    Status = CommandCompletionStatus.Failure,
                    Party = militia,
                    CompletionTime = CampaignTime.Now
                };

                BanditMilitias.Core.Events.EventBus.Instance.Publish(evt);
            }

            return "Simulated 5 failed 'Hunt' commands. Check log for confidence drop.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("force_hourly_tick", "bandit")]
        public static string ForceHourlyTick(List<string> args)
        {
            if (Campaign.Current == null) return "Campaign not loaded.";

            BanditMilitias.Behaviors.MilitiaBehavior behavior = Campaign.Current.GetCampaignBehavior<BanditMilitias.Behaviors.MilitiaBehavior>();
            if (behavior == null) return "MilitiaBehavior not found.";

            var method = behavior.GetType().GetMethod("OnHourlyTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method == null) return "OnHourlyTick method not found.";
            _ = method.Invoke(behavior, null);

            return "Forced OnHourlyTick execution.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("verify_warlord_economy", "bandit")]
        public static string VerifyWarlordEconomy(List<string> args)
        {
            if (Campaign.Current == null) return "Campaign not loaded.";

            var hideout = Settlement.All.FirstOrDefault(s => s.IsHideout && s.IsActive);
            if (hideout == null) return "No active hideout found.";

            var warlord = WarlordSystem.Instance.GetWarlordForHideout(hideout);
            if (warlord == null) return $"No warlord found for {hideout.Name}.";

            float initialGold = warlord.Gold;
            float testGold = 1000f;
            warlord.Gold = testGold;

            var spawningSystem = ModuleManager.Instance.GetModule<MilitiaSpawningSystem>();
            if (spawningSystem == null)
            {
                warlord.Gold = initialGold;
                return "Spawning system not registered.";
            }

            var party = spawningSystem.SpawnMilitia(hideout, force: false);

            string result = $"Warlord: {warlord.Name}\n";
            result += $"Initial Gold (Set): {testGold}\n";

            if (party != null)
            {
                result += $"Spawn: SUCCESS (Party: {party.Name})\n";
                result += $"Final Gold: {warlord.Gold}\n";
                result += $"Cost Paid: {testGold - warlord.Gold}\n";

                BanditMilitias.Infrastructure.CompatibilityLayer.DestroyParty(party);
                result += "Test Party Destroyed.\n";
            }
            else
            {
                result += $"Spawn: FAILED (Check logs/debug)\n";
                result += $"Final Gold: {warlord.Gold}\n";
            }

            warlord.Gold = initialGold;

            return result;
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("verify_integration", "bandit")]
        public static string VerifyIntegration(List<string> args)
        {
            if (BanditBrain.Instance == null) return "BanditBrain not initialized.";

            var sb = new System.Text.StringBuilder();
            _ = sb.AppendLine("Integration Status:");

            var trackerThreat = BanditMilitias.Systems.Tracking.PlayerTracker.Instance.GetThreatLevel();

            var brainProfileField = typeof(BanditBrain).GetField("_playerProfile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var brainProfile = brainProfileField?.GetValue(BanditBrain.Instance);

            if (brainProfile != null)
            {
                var threatProp = brainProfile.GetType().GetMethod("GetCurrentThreat");
                float brainThreat = (float)(threatProp?.Invoke(brainProfile, null) ?? -1f);

                _ = sb.AppendLine($"[PlayerTracker -> Brain]");
                _ = sb.AppendLine($"  Tracker Threat: {trackerThreat:F2}");
                _ = sb.AppendLine($"  Brain Perception: {brainThreat:F2}");
                _ = sb.AppendLine($"  Status: {(MathF.Abs(trackerThreat - brainThreat) < 0.1f ? "SYNCED" : "DESYNCED")}");
            }
            else
            {
                _ = sb.AppendLine($"[PlayerTracker -> Brain] FAIL: Could not access Brain profile.");
            }

            _ = sb.AppendLine("[Warlord -> Spawning]");
            _ = sb.AppendLine("  Use 'bandit.verify_warlord_economy' to test.");

            return sb.ToString();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("runtime_diag", "militia")]
        public static string RuntimeDiagnostics(List<string> args)
        {
            var sb = new System.Text.StringBuilder();
            _ = sb.AppendLine("=== RUNTIME DIAGNOSTICS ===");
            _ = sb.AppendLine(ModuleManager.Instance.GetDiagnostics());
            _ = sb.AppendLine();
            _ = sb.AppendLine(DiagnosticsSystem.GenerateReport());
            _ = sb.AppendLine();
            _ = sb.AppendLine(SystemWatchdog.Instance.GetStatusReport());
            _ = sb.AppendLine();
            _ = sb.AppendLine(ExceptionMonitor.GetReport(10));
            return sb.ToString();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("log_spawn_diag", "militia")]
        public static string LogSpawnDiagnostics(List<string> args)
        {
            var sb = new System.Text.StringBuilder();

            bool hasCampaign = Campaign.Current != null;
            var spawner = ModuleManager.Instance.GetModule<MilitiaSpawningSystem>();
            int registryCount = ModuleManager.Instance.GetMilitiaCount();
            var registryMilitias = ModuleManager.Instance.ActiveMilitias.Where(p => p != null).ToList();
            int worldMilitiaCount = MobileParty.All.Count(p => p != null &&
                                                               p.PartyComponent is MilitiaPartyComponent &&
                                                               p.IsActive);
            int activeHideouts = ModuleManager.Instance.HideoutCache.Count(h => h != null && h.IsHideout && h.IsActive);
            int totalHideouts = ModuleManager.Instance.HideoutCache.Count;

            _ = sb.AppendLine("=== SPAWN DIAGNOSTICS ===");
            _ = sb.AppendLine($"CampaignLoaded: {hasCampaign}");
            _ = sb.AppendLine($"CampaignDay: {(hasCampaign ? CampaignTime.Now.ToDays.ToString("F2") : "n/a")}");
            _ = sb.AppendLine($"Settings.MilitiaSpawn: {Settings.Instance?.MilitiaSpawn}");
            _ = sb.AppendLine($"Spawner.DailyChanceBand: {MilitiaSpawningSystem.BaseDailySpawnChanceMin:P0}-{MilitiaSpawningSystem.BaseDailySpawnChanceMax:P0}");
            _ = sb.AppendLine($"Settings.ActivationDelay: {Settings.Instance?.ActivationDelay}");
            _ = sb.AppendLine($"Settings.MaxTotalMilitias: {Settings.Instance?.MaxTotalMilitias}");
            _ = sb.AppendLine($"Hideouts: active={activeHideouts}, totalCache={totalHideouts}");
            _ = sb.AppendLine($"ModuleRegistryMilitias: {registryCount}");
            _ = sb.AppendLine($"WorldScanMilitias(active): {worldMilitiaCount}");
            _ = sb.AppendLine($"Captivity: {BanditMilitias.Patches.SurrenderFix.SurrenderCrashPatch.GetCaptivityOverlayStatus()}");

            if (spawner == null)
            {
                _ = sb.AppendLine("SpawnerModule: MISSING");
            }
            else
            {
                _ = sb.AppendLine($"SpawnerModule: Ready (IsEnabled={spawner.IsEnabled})");
            }

            var sampleHideout = ModuleManager.Instance.HideoutCache.FirstOrDefault(h => h != null && h.IsHideout);
            if (sampleHideout == null)
            {
                _ = sb.AppendLine("SampleHideout: NONE");
            }
            else if (spawner == null)
            {
                _ = sb.AppendLine($"SampleHideout: {sampleHideout.Name} (spawner missing)");
            }
            else
            {
                bool canSpawn = spawner.CanSpawn(sampleHideout);
                _ = sb.AppendLine($"SampleHideout: {sampleHideout.Name} | CanSpawn={canSpawn}");
            }

            _ = sb.AppendLine("RegistryParties(top 10):");
            foreach (var party in registryMilitias.Take(10))
            {
                string home = (party.PartyComponent as MilitiaPartyComponent)?.GetHomeSettlement()?.Name?.ToString() ?? "n/a";
                _ = sb.AppendLine($"- {party.Name} | Active={party.IsActive} | Home={home}");
            }

            string report = sb.ToString();
            try
            {
                FileLogger.LogSection("SpawnDiagnostics");
                FileLogger.Log(report);
                FileLogger.Log($"[SpawnDiagnostics] Command='militia.log_spawn_diag' | LogPath={FileLogger.GetLogPath()}");
            }
            catch
            {

            }

            return report + $"\nLogged to: {FileLogger.GetLogPath()}";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("verify_optimization", "militia")]
        public static string VerifyOptimization(List<string> args)
        {
            var sb = new System.Text.StringBuilder();
            _ = sb.AppendLine("=== AI OPTIMIZATION REPORT ===");


            var cache = BanditMilitias.Intelligence.AI.Components.StaticDataCache.Instance;
            _ = sb.AppendLine($"[StaticDataCache] Hideouts: {cache.AllHideouts.Count}, Villages: {cache.AllVillages.Count}, Towns: {cache.AllTowns.Count}");


            var militias = ModuleManager.Instance.ActiveMilitias;
            int total = militias.Count;
            int[] slots = new int[3];
            foreach (var p in militias)
            {
                if (p == null) continue;
                int hash = Math.Abs(p.StringId.GetHashCode());
                slots[hash % 3]++;
            }
            int currentHourSlot = (int)CampaignTime.Now.ToHours % 3;
            _ = sb.AppendLine($"[Staggered Ticks] Total: {total} | Slots Distribution: [{slots[0]}, {slots[1]}, {slots[2]}]");
            _ = sb.AppendLine($"  Current active slot: {currentHourSlot} (Running AI for {slots[currentHourSlot]} parties)");


            var swarm = ModuleManager.Instance.GetModule<BanditMilitias.Intelligence.Swarm.SwarmCoordinator>();
            if (swarm != null)
            {
                var diag = swarm.GetDiagnostics();
                _ = sb.AppendLine($"[SwarmCoordinator] {diag}");
            }

            return sb.ToString();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("spawn_pipeline_check", "militia")]
        public static string SpawnPipelineCheck(List<string> args)
        {
            var sb = new System.Text.StringBuilder();
            _ = sb.AppendLine("══════════════════════════════════════════");
            _ = sb.AppendLine("  SPAWN PIPELINE — GATE TEST");
            _ = sb.AppendLine("══════════════════════════════════════════");

            int passCount = 0;
            int failCount = 0;

            void Check(string name, bool condition, string failDetail = "")
            {
                if (condition)
                {
                    _ = sb.AppendLine($"  ✅ PASS: {name}");
                    passCount++;
                }
                else
                {
                    string detail = string.IsNullOrEmpty(failDetail) ? "" : $" ({failDetail})";
                    _ = sb.AppendLine($"  ❌ FAIL: {name}{detail}");
                    failCount++;
                }
            }

            bool hasCampaign = Campaign.Current != null;
            Check("Campaign.Current", hasCampaign, "Campaign not loaded");

            bool hasSettings = Settings.Instance != null;
            Check("Settings.Instance", hasSettings, "MCM/Settings not initialized");

            bool spawnEnabled = Settings.Instance?.MilitiaSpawn ?? true;
            Check("Settings.MilitiaSpawn", spawnEnabled, "Disabled in MCM");

            Check("Spawner.DailyChanceBand",
                MilitiaSpawningSystem.BaseDailySpawnChanceMin > 0f
                && MilitiaSpawningSystem.BaseDailySpawnChanceMax > MilitiaSpawningSystem.BaseDailySpawnChanceMin,
                $"Values: {MilitiaSpawningSystem.BaseDailySpawnChanceMin:P0}-{MilitiaSpawningSystem.BaseDailySpawnChanceMax:P0}");

            int infantryCount = BanditMilitias.Core.Config.Globals.BasicInfantry.Count;
            Check("Globals.BasicInfantry", infantryCount > 0, $"Count: {infantryCount}");

            var lootersClan = ClanCache.GetLootersClan();
            var fallbackClan = ClanCache.GetFallbackBanditClan();
            Check("ClanCache.LootersClan", lootersClan != null, "Looters clan not found");
            Check("ClanCache.FallbackClan", fallbackClan != null, "Fallback bandit clan not found");

            int hideoutCount = ModuleManager.Instance.HideoutCache.Count;
            int activeHideouts = ModuleManager.Instance.HideoutCache.Count(h => h != null && h.IsHideout && h.IsActive);
            Check("HideoutCache", hideoutCount > 0, $"Total: {hideoutCount}");
            Check("ActiveHideouts", activeHideouts > 0, $"Active: {activeHideouts}");

            int currentMilitias = ModuleManager.Instance.GetMilitiaCount();
            int maxMilitias = Settings.Instance?.MaxTotalMilitias ?? 60;
            Check("MaxTotalMilitias limit", currentMilitias < maxMilitias,
                $"Current: {currentMilitias}/{maxMilitias}");

            if (hasCampaign)
            {
                float elapsedDays = ModActivationManager.GetActivationDelayElapsedDays();
                int delay = Settings.Instance?.ActivationDelay ?? 1;
                Check("ActivationDelay", ModActivationManager.HasActivationDelayElapsed(delay),
                    $"Elapsed days: {elapsedDays:F1}, Required: {delay}");
            }
            else
            {
                Check("ActivationDelay", false, "No campaign, check skipped");
            }

            var spawner = ModuleManager.Instance.GetModule<MilitiaSpawningSystem>();
            Check("SpawningSystem registered", spawner != null, "Not in ModuleManager");
            if (spawner != null)
            {
                Check("SpawningSystem.IsEnabled", spawner.IsEnabled, "Module disabled");
            }

            _ = sb.AppendLine("──────────────────────────────────────────");
            _ = sb.AppendLine($"  RESULT: {passCount} PASS / {failCount} FAIL");
            if (failCount == 0)
                _ = sb.AppendLine("  → All gates open — spawning should work!");
            else
                _ = sb.AppendLine($"  → {failCount} blockers preventing spawn!");
            _ = sb.AppendLine("══════════════════════════════════════════");

            string result = sb.ToString();
            try { FileLogger.Log($"[PipelineCheck]\n{result}"); } catch { }
            return result;
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("toggle_test_mode", "militia")]
        public static string ToggleTestMode(List<string> args)
        {
            if (Settings.Instance == null) return "Settings not loaded.";
            Settings.Instance.TestingMode = !Settings.Instance.TestingMode;
            Settings.Instance.ShowTestMessages = Settings.Instance.TestingMode;
            return $"TestingMode toggled. TestingMode={Settings.Instance.TestingMode}, ShowTestMessages={Settings.Instance.ShowTestMessages}";
        }
    }

}
