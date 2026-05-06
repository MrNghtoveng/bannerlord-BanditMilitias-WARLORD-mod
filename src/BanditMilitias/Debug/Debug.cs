using BanditMilitias.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.AI;
using BanditMilitias.Intelligence.AI.Components;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Diagnostics;
using BanditMilitias.Systems.Spawning;
using BanditMilitias.Systems.Tracking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;

namespace BanditMilitias.Debug
{
    // ── DebugPanel ─────────────────────────────────────────

    public class DebugPanel
    {

        private Action<MilitiaKilledEvent>? _onKilled;
        private Action<HideoutClearedEvent>? _onCleared;
        private Action<MilitiaMergeEvent>? _onMerge;
        private Action<MilitiaRaidEvent>? _onRaid;
        private Action<AIDecisionEvent>? _onAIDecision;
        private static DebugPanel? _instance;
        public static DebugPanel Instance => _instance ??= new DebugPanel();

        public static void Reset()
        {
            if (_instance != null)
            {
                try { _instance.Dispose(); } catch { }
            }
            _instance = null;
        }

        private bool _isVisible = false;
        private readonly List<string> _eventLog = new(100);
        private readonly Dictionary<string, Action<string[]>> _commands = new();

        private const int MAX_LOG_ENTRIES = 50;

        private DebugPanel()
        {
            RegisterCommands();

            _onKilled = e => LogEvent($"Kill: {e.GetDescription()}");
            _onCleared = e => LogEvent($"Cleared: {e.GetDescription()}");
            _onMerge = e => LogEvent($"Merge: {e.GetDescription()}");
            _onRaid = e => LogEvent($"Raid: {e.GetDescription()}");
            _onAIDecision = e => LogEvent($"AI: {e.GetDescription()}");
            EventBus.Instance.Subscribe<MilitiaKilledEvent>(_onKilled);
            EventBus.Instance.Subscribe<HideoutClearedEvent>(_onCleared);
            EventBus.Instance.Subscribe<MilitiaMergeEvent>(_onMerge);
            EventBus.Instance.Subscribe<MilitiaRaidEvent>(_onRaid);
            EventBus.Instance.Subscribe<AIDecisionEvent>(_onAIDecision);
        }

        public static void PrintError(string message)
        {
            EventBus.Instance.Publish<AIDecisionEvent>(new AIDecisionEvent { DecisionType = "ERROR", Reason = message, Party = null! });
        }

        public static void PrintWarning(string message)
        {
            EventBus.Instance.Publish<AIDecisionEvent>(new AIDecisionEvent { DecisionType = "WARNING", Reason = message, Party = null! });
        }

        private string GetDetailedStats()
        {
            var sb = new System.Text.StringBuilder();
            _ = sb.AppendLine("=== DETAILED STATISTICS ===");

            _ = sb.AppendLine(ModuleManager.Instance.GetDiagnostics());

            return sb.ToString();
        }

        private float _lastUpdate = 0f;

        private static bool IsDiagnosticsOverlayAllowed()
        {
            return Settings.Instance?.TestingMode == true
                || Settings.Instance?.ShowTestMessages == true
                || Settings.Instance?.DevMode == true;
        }

        public void Update()
        {

            if (Campaign.Current == null || Game.Current == null || Game.Current.GameStateManager == null) return;

            if (Game.Current.GameStateManager.ActiveState is not TaleWorlds.CampaignSystem.GameState.MapState) return;

            if (!IsDiagnosticsOverlayAllowed())
            {
                _isVisible = false;
                return;
            }

            if (Input.IsKeyDown(InputKey.LeftShift) &&
                Input.IsKeyDown(InputKey.LeftAlt) &&
                Input.IsKeyPressed(InputKey.L))
            {
                Toggle();
            }

            if (Input.IsKeyDown(InputKey.LeftShift) &&
                Input.IsKeyDown(InputKey.LeftAlt) &&
                Input.IsKeyPressed(InputKey.J))
            {
                Toggle();
            }

            if (Input.IsKeyDown(InputKey.LeftShift) &&
                Input.IsKeyDown(InputKey.LeftAlt) &&
                Input.IsKeyPressed(InputKey.M))
            {
                if (Settings.Instance != null)
                {
                    Settings.Instance.TestingMode = !Settings.Instance.TestingMode;
                    Settings.Instance.ShowTestMessages = Settings.Instance.TestingMode;

                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[SHORTCUT] Test Mode: {(Settings.Instance.TestingMode ? "ENABLED (Fast Spawn + Logs)" : "DISABLED")}",
                        Settings.Instance.TestingMode ? Colors.Green : Colors.Yellow));

                    if (Settings.Instance.TestingMode)
                    {
                        var count = BanditMilitias.Infrastructure.ModuleManager.Instance.GetMilitiaCount();
                        InformationManager.DisplayMessage(new InformationMessage($"Current Militias: {count}", Colors.Cyan));
                    }
                }
            }

            {

                float currentTime = (float)(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds;
                if (currentTime - _lastUpdate > 1.0f)
                {
                    try
                    {

                        if (_isVisible)
                        {
                            DisplayPanel();
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Warning("DebugPanel", $"Panel update disabled after error: {ex.Message}");
                        _isVisible = false;
                    }
                    _lastUpdate = currentTime;
                }
            }
        }

        public void Toggle()
        {
            if (!IsDiagnosticsOverlayAllowed())
            {
                _isVisible = false;
                InformationManager.DisplayMessage(
                    new InformationMessage(
                        "Bandit Debug Panel requires Test Mode, Dev Mode, or Show Debug Messages.",
                        Colors.Yellow));
                return;
            }

            _isVisible = !_isVisible;

            InformationManager.DisplayMessage(
                new InformationMessage(
                    _isVisible ? "Bandit Debug Panel: ON" : "Bandit Debug Panel: OFF",
                    _isVisible ? Colors.Green : Colors.Red));
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("toggle_bandit_debug", "campaign")]
        public static string ToggleDebugCommand(List<string> _)
        {
            Instance.Toggle();
            return "Debug panel toggled.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("toggle_test_mode", "bandit")]
        public static string ToggleTestModeCommand(List<string> _)
        {
            if (Settings.Instance == null) return "Settings not loaded.";
            Settings.Instance.TestingMode = !Settings.Instance.TestingMode;
            Settings.Instance.ShowTestMessages = Settings.Instance.TestingMode;

            return $"Testing Mode is now {(Settings.Instance.TestingMode ? "ENABLED (Hourly Spawns)" : "DISABLED")} | ShowTestMessages={Settings.Instance.ShowTestMessages}.";
        }

        private void DisplayPanel()
        {
            var stats = GetCurrentStats();
            var captivityStatus = BanditMilitias.Patches.SurrenderFix.SurrenderCrashPatch.GetCaptivityOverlayStatus();
            bool spawnEnabled = Settings.Instance?.MilitiaSpawn == true;
            int totalHideouts = ModuleManager.Instance.HideoutCache.Count;
            int activeHideouts = ModuleManager.Instance.HideoutCache.Count(h => h != null && h.IsActive);

            // FIX: Bootstrap ve activation delay durumunu göster
            bool bootstrapDone = ModuleManager.Instance.IsSessionBootstrapComplete;
            string bootStatus = bootstrapDone ? "OK" : "PENDING";

            string delayStatus = "";
            try
            {
                float elapsed = Infrastructure.CompatibilityLayer.GetActivationDelayElapsedDays();
                int required = Settings.Instance?.ActivationDelay ?? 2;
                bool switchClosed = Infrastructure.CompatibilityLayer.IsGameplayActivationSwitchClosed();
                delayStatus = switchClosed ? "ACTIVE" : $"{elapsed:F1}d/{required}d";
            }
            catch { delayStatus = "?"; }

            var message = $"[BANDIT DEBUG] Boot:{bootStatus} | Delay:{delayStatus} | Militia:{stats.TotalMilitias} | Active:{stats.ActiveParties} | Spawn:{(spawnEnabled ? "ON" : "OFF")} | Hideout(A/T):{activeHideouts}/{totalHideouts} | {captivityStatus}";

            InformationManager.DisplayMessage(new InformationMessage(message, Colors.Cyan));
        }

        private void RegisterCommands()
        {
            _commands["sys_diagnosis"] = DiagnosisCommand;
            _commands["sys_clear"] = ClearCommand;
            _commands["sys_log"] = LogCommand;
            _commands["sys_threat"] = ThreatCommand;
            _commands["sys_ambush"] = AmbushCommand;
            _commands["sys_kill_scan"] = KillCommand;
            _commands["sys_spawn"] = SpawnCommand;
            _commands["sys_force_spawn"] = ForceSpawnCommand;
            _commands["sys_stats"] = StatsCommand;
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("dlt.frce.sys.16", "bandit")]
        public static string CryptoForceSpawn(List<string> args)
        {
            Instance.ForceSpawnCommand(args?.ToArray() ?? Array.Empty<string>());
            return "System 16 Executed.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("force_spawn", "bandit")]
        public static string ForceSpawnAlias(List<string> args)
        {
            Instance.ForceSpawnCommand(args?.ToArray() ?? Array.Empty<string>());
            return "Force spawn executed.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("exec", "bandit")]
        public static string ExecuteDebugCommand(List<string> args)
        {
            if (args == null || args.Count == 0)
            {
                return "Usage: bandit.exec <sys_command> [args...]";
            }

            Instance.ExecuteCommand(string.Join(" ", args));
            return "Debug command executed.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("debug_log", "bandit")]
        public static string GetDebugLogCommand(List<string> args)
        {
            return Instance.GetRecentLog();
        }

        public void ExecuteCommand(string input)
        {
            if (string.IsNullOrEmpty(input)) return;

            var parts = input.Split(' ');
            string command = parts[0].ToLower();
            string[] args = parts.Skip(1).ToArray();

            if (_commands.TryGetValue(command, out var action))
            {
                try
                {
                    action(args);
                    LogEvent($"Command executed: {command}");
                }
                catch (Exception ex)
                {
                    LogEvent($"Command failed: {ex.Message}");
                    InformationManager.DisplayMessage(
                        new InformationMessage($"[Debug] Error: {ex.Message}", Colors.Red));
                }
            }
            else
            {
                LogEvent($"Unknown command: {command}");
                InformationManager.DisplayMessage(
                    new InformationMessage($"[Debug] Unknown command: {command}", Colors.Red));
            }
        }

        private void SpawnCommand(string[] args)
        {
            int count = args.Length > 0 ? int.Parse(args[0]) : 1;

            if (Hero.MainHero == null) return;

            Vec2 playerPos = Vec2.Invalid;
            if (Hero.MainHero.PartyBelongedTo != null)
            {
                playerPos = CompatibilityLayer.GetPartyPosition(Hero.MainHero.PartyBelongedTo);
            }
            else if (Hero.MainHero.CurrentSettlement != null)
            {

                var p = Hero.MainHero.CurrentSettlement.GatePosition;
                playerPos = new Vec2(p.X, p.Y);
            }

            if (!playerPos.IsValid)
            {
                InformationManager.DisplayMessage(new InformationMessage("[Debug] Player not on map (Teleport skipped).", Colors.Yellow));

            }

            var nearestHideout = FindNearestHideout();
            if (nearestHideout == null)
            {
                throw new Exception("No hideout found");
            }

            var spawner = ModuleManager.Instance.GetModule<MilitiaSpawningSystem>();
            if (spawner == null)
            {
                throw new Exception("Spawning system not found");
            }

            int successCount = 0;
            for (int i = 0; i < count; i++)
            {
                var militia = spawner.SpawnMilitia(nearestHideout);
                if (militia != null)
                {
                    militia.IsVisible = true;
                    if (militia.Party != null) militia.Party.SetVisualAsDirty();
                    successCount++;
                    LogEvent($"Spawned militia {militia.Name} #{i + 1}");
                }
                else
                {
                    LogEvent($"Failed to spawn militia #{i + 1} (Cooldown or Limit)");
                }
            }

            InformationManager.DisplayMessage(
                new InformationMessage($"[Debug] Spawned {successCount}/{count} militias at {nearestHideout.Name}", Colors.Green));
        }

        private void ForceSpawnCommand(string[] args)
        {
            if (Hero.MainHero == null) return;

            Vec2 playerPos = Vec2.Invalid;
            if (Hero.MainHero.PartyBelongedTo != null)
            {
                playerPos = CompatibilityLayer.GetPartyPosition(Hero.MainHero.PartyBelongedTo);
            }
            else if (Hero.MainHero.CurrentSettlement != null)
            {
                var p = Hero.MainHero.CurrentSettlement.GatePosition;
                playerPos = new Vec2(p.X, p.Y);
            }

            if (!playerPos.IsValid)
            {
                InformationManager.DisplayMessage(new InformationMessage("[Debug] Player position not found (Teleport skipped).", Colors.Yellow));
            }

            Settlement? targetHideout;

            if (args.Length > 0)
            {
                string search = string.Join(" ", args).ToLower();
                targetHideout = TaleWorlds.CampaignSystem.Campaign.Current.Settlements
                    .FirstOrDefault(s => s.IsHideout && s.Name.ToString().ToLower().Contains(search));

                if (targetHideout == null)
                {
                    InformationManager.DisplayMessage(new InformationMessage($"[Debug] Hideout '{search}' not found.", Colors.Red));
                    return;
                }
            }
            else
            {
                targetHideout = FindNearestHideout()!;
            }

            if (targetHideout == null)
            {
                InformationManager.DisplayMessage(new InformationMessage("[Debug] No hideout found nearby.", Colors.Red));
                return;
            }

            var spawner = ModuleManager.Instance.GetModule<MilitiaSpawningSystem>();
            if (spawner == null)
            {
                InformationManager.DisplayMessage(new InformationMessage("[Debug] Spawning system missing!", Colors.Red));
                return;
            }

            var party = spawner.SpawnMilitia(targetHideout, true);

            if (party != null)
            {
                party.IsVisible = true;
                if (party.Party != null) party.Party.SetVisualAsDirty();

                InformationManager.DisplayMessage(new InformationMessage(
                    $"? [FORCE] Spawned {party.Name} at {targetHideout.Name}", Colors.Green));
            }
            else
            {
                InformationManager.DisplayMessage(new InformationMessage($"[FORCE] Spawn FAILED at {targetHideout.Name}. Check logs.", Colors.Red));
            }
        }

        private void KillCommand(string[] args)
        {
            float radius = args.Length > 0 ? float.Parse(args[0]) : 50f;

            if (Hero.MainHero?.PartyBelongedTo == null) return;

            var playerPos = CompatibilityLayer.GetPartyPosition(Hero.MainHero.PartyBelongedTo);
            int killed = 0;

            var militias = ModuleManager.Instance.ActiveMilitias.ToList();

            foreach (var militia in militias)
            {
                var militiaPos = CompatibilityLayer.GetPartyPosition(militia);
                float dist = playerPos.Distance(militiaPos);

                if (dist <= radius)
                {
                    CompatibilityLayer.DestroyParty(militia);
                    killed++;
                }
            }

            InformationManager.DisplayMessage(
                new InformationMessage($"[Debug] Killed {killed} militias in {radius} radius", Colors.Red));
        }

        private void StatsCommand(string[] _)
        {
            var stats = GetDetailedStats();

            InformationManager.DisplayMessage(new InformationMessage(stats, Colors.Cyan));
        }

        private void ClearCommand(string[] _)
        {
            _eventLog.Clear();
            InformationManager.DisplayMessage(new InformationMessage("[Debug] Log cleared", Colors.Green));
        }

        private void LogCommand(string[] _)
        {
            string log = GetRecentLog();
            if (string.IsNullOrWhiteSpace(log))
            {
                InformationManager.DisplayMessage(new InformationMessage("[Debug] Event log is empty.", Colors.Gray));
                return;
            }

            InformationManager.DisplayMessage(new InformationMessage(log, Colors.Cyan));
        }

        private void DiagnosisCommand(string[] _)
        {
            string report = BanditMilitias.Systems.Diagnostics.DiagnosticsSystem.GenerateReport();

            InformationManager.DisplayMessage(new InformationMessage(report, Colors.Cyan));

        }

        private void ThreatCommand(string[] _)
        {
            float threat = PlayerTracker.Instance.GetThreatLevel();

            int totalBounty = 0;
            try
            {
                var bountySystem = BanditMilitias.Systems.Bounty.BountySystem.Instance;
                foreach (var warlord in WarlordSystem.Instance.GetAllWarlords())
                {
                    totalBounty += bountySystem.GetBounty(warlord.StringId);
                }
            }
            catch (Exception ex) { DebugLogger.Warning("DebugPanel", $"Failed to fetch bounty: {ex.Message}"); }

            string message = $@"
=== PLAYER THREAT ANALYSIS ===
Threat Level: {threat:F2} / 3.0
Total Bounty: {totalBounty:N0}
Status: {GetThreatStatus(threat)}
            ";

            InformationManager.DisplayMessage(new InformationMessage(message, Colors.Yellow));
        }

        private void AmbushCommand(string[] _)
        {
            var ambushPoint = PlayerTracker.Instance.GetMostFrequentRoute();

            if (ambushPoint == null)
            {
                InformationManager.DisplayMessage(
                    new InformationMessage("[Debug] No frequent routes detected", Colors.Gray));
                return;
            }

            InformationManager.DisplayMessage(
                new InformationMessage(
                    $"[Debug] Ambush point: X={ambushPoint.Value.X:F0}, Y={ambushPoint.Value.Y:F0}",
                    Colors.Red));
        }

        private void LogEvent(string message)
        {
            int minute = (int)((CampaignTime.Now.ToHours - (int)CampaignTime.Now.ToHours) * 60);
            _eventLog.Add($"[{CampaignTime.Now.GetHourOfDay:D2}:{minute:D2}] {message}");

            if (_eventLog.Count > MAX_LOG_ENTRIES)
            {
                _eventLog.RemoveAt(0);
            }
        }

        private string GetRecentLog()
        {
            return string.Join("\n", _eventLog.Skip(Math.Max(0, _eventLog.Count - 10)));
        }

        private DebugStats GetCurrentStats()
        {

            var militias = ModuleManager.Instance.ActiveMilitias.ToList();

            int kills = 0;
            var hideout = FindNearestHideout();
            if (hideout != null)
            {
                var rep = PlayerTracker.Instance.GetReputation(hideout);
                if (rep != null) kills = rep.KillCount;
            }

            return new DebugStats
            {
                TotalMilitias = militias.Count,
                ActiveParties = militias.Count(p => p.IsActive),
                PlayerKills = kills,
                ThreatLevel = PlayerTracker.Instance.GetThreatLevel(),
                AvgAITime = 0f
            };
        }

        private string GetThreatStatus(float threat)
        {
            return threat switch
            {
                >= 2.5f => "EXTREMELY DANGEROUS",
                >= 2.0f => "DANGEROUS",
                >= 1.5f => "THREATENING",
                >= 1.0f => "CONCERNING",
                >= 0.5f => "NOTICED",
                _ => "UNKNOWN"
            };
        }

        private TaleWorlds.CampaignSystem.Settlements.Settlement? FindNearestHideout()
        {
            if (Hero.MainHero?.PartyBelongedTo == null) return null;

            var playerPos = CompatibilityLayer.GetPartyPosition(Hero.MainHero.PartyBelongedTo);

            return TaleWorlds.CampaignSystem.Campaign.Current.Settlements
                .Where(s => s.IsHideout && s.IsActive)
                .OrderBy(s => s.GatePosition.Distance(playerPos))
                .FirstOrDefault();
        }

        private struct DebugStats
        {
            public int TotalMilitias;
            public int ActiveParties;
            public int PlayerKills;
            public float ThreatLevel;
            public float AvgAITime;
        }

        public void Dispose()
        {
            if (_onKilled != null) EventBus.Instance.Unsubscribe<MilitiaKilledEvent>(_onKilled);
            if (_onCleared != null) EventBus.Instance.Unsubscribe<HideoutClearedEvent>(_onCleared);
            if (_onMerge != null) EventBus.Instance.Unsubscribe<MilitiaMergeEvent>(_onMerge);
            if (_onRaid != null) EventBus.Instance.Unsubscribe<MilitiaRaidEvent>(_onRaid);
            if (_onAIDecision != null) EventBus.Instance.Unsubscribe<AIDecisionEvent>(_onAIDecision);
        }
    }

    // ── StructuredLogger ─────────────────────────────────────────
    /// <summary>
    /// Structured logging system for better debugging and analytics
    /// Compatible with .NET Framework 4.7.2 (no System.Text.Json)
    /// </summary>
    public static class StructuredLogger
    {
        public enum LogLevel { Debug, Info, Warning, Error, Critical }

        public class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public string System { get; set; } = "";
            public string Message { get; set; } = "";
            public LogLevel Level { get; set; }
            public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();
            public string GameTime { get; set; } = "";
            public float GameTimeInDays { get; set; }

            /// <summary>
            /// Manual JSON serialization for .NET Framework 4.7.2 compatibility
            /// </summary>
            public string ToJson()
            {
                var sb = new StringBuilder();
                _ = sb.Append("{");
                _ = sb.AppendFormat("\"timestamp\":\"{0:yyyy-MM-ddTHH:mm:ss}\",", Timestamp);
                _ = sb.AppendFormat("\"system\":\"{0}\",", EscapeJson(System));
                _ = sb.AppendFormat("\"message\":\"{0}\",", EscapeJson(Message));
                _ = sb.AppendFormat("\"level\":\"{0}\",", Level.ToString().ToLower());
                _ = sb.AppendFormat("\"game_time\":\"{0}\",", GameTime);
                _ = sb.AppendFormat("\"game_time_days\":{0},", GameTimeInDays);

                // Context
                _ = sb.Append("\"context\":{");
                if (Context != null && Context.Count > 0)
                {
                    bool first = true;
                    foreach (var kv in Context)
                    {
                        if (!first) _ = sb.Append(",");
                        first = false;
                        _ = sb.AppendFormat("\"{0}\":\"{1}\"", EscapeJson(kv.Key), EscapeJson(kv.Value?.ToString() ?? "null"));
                    }
                }
                _ = sb.Append("}");

                _ = sb.Append("}");
                return sb.ToString();
            }

            private static string EscapeJson(string str)
            {
                if (string.IsNullOrEmpty(str)) return "";
                return str.Replace("\\", "\\\\")
                         .Replace("\"", "\\\"")
                         .Replace("\n", "\\n")
                         .Replace("\r", "\\r")
                         .Replace("\t", "\\t");
            }
        }

        private static readonly Queue<LogEntry> _recentLogs = new Queue<LogEntry>(100);
        private static readonly object _logLock = new object();

        /// <summary>
        /// Log a structured message with context
        /// </summary>
        public static void Log(string system, string message, LogLevel level = LogLevel.Info,
            Dictionary<string, object>? context = null)
        {
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                System = system,
                Message = message,
                Level = level,
                Context = context ?? new Dictionary<string, object>(),
                GameTime = CampaignTime.Now.ToString(),
                GameTimeInDays = (float)CampaignTime.Now.ToDays
            };

            // Keep recent logs in memory
            lock (_logLock)
            {
                _recentLogs.Enqueue(entry);
                while (_recentLogs.Count > 100)
                    _ = _recentLogs.Dequeue();
            }

            var ctx = context != null && context.Any()
                ? string.Join(", ", context.Select(kv => $"{kv.Key}={kv.Value}"))
                : "";

            // Console output in testing mode
            if (Settings.Instance?.TestingMode == true && level >= LogLevel.Warning)
            {
                var color = level switch
                {
                    LogLevel.Critical => Colors.Red,
                    LogLevel.Error => Colors.Red,
                    LogLevel.Warning => Colors.Yellow,
                    _ => Colors.Green
                };

                InformationManager.DisplayMessage(new InformationMessage(
                    $"[{system}] {message} {ctx}", color));
            }

            // Always log to file for all levels so the user can read it from the .log file
            if (level >= LogLevel.Debug)
            {
                FileLogger.Log($"[{level}] [{system}] {message} {ctx}");
            }
        }

        public static void Debug(string system, string message, Dictionary<string, object>? context = null)
            => Log(system, message, LogLevel.Debug, context);

        public static void Info(string system, string message, Dictionary<string, object>? context = null)
            => Log(system, message, LogLevel.Info, context);

        public static void Warning(string system, string message, Dictionary<string, object>? context = null)
            => Log(system, message, LogLevel.Warning, context);

        public static void Error(string system, string message, Dictionary<string, object>? context = null)
            => Log(system, message, LogLevel.Error, context);

        public static void Critical(string system, string message, Dictionary<string, object>? context = null)
            => Log(system, message, LogLevel.Critical, context);

        /// <summary>
        /// Log spawn attempt with full context
        /// </summary>
        public static void LogSpawnAttempt(Settlement hideout, float chance, bool success, string? reason = null)
        {
            var context = new Dictionary<string, object>
            {
                ["hideout"] = hideout?.Name?.ToString() ?? "null",
                ["hideout_id"] = hideout?.StringId ?? "null",
                ["chance"] = $"{chance:P2}",
                ["success"] = success,
                ["reason"] = reason ?? "none",
                ["militia_count"] = Infrastructure.ModuleManager.Instance?.GetMilitiaCount() ?? -1,
                ["globals_ready"] = Core.Config.Globals.IsInitialized,
                ["clan_ready"] = ClanCache.IsInitialized,
                ["elapsed_days"] = GetElapsedDays()
            };

            Log("Spawn", success ? "SpawnSuccess" : "SpawnFailed",
                success ? LogLevel.Info : LogLevel.Warning, context);
        }

        /// <summary>
        /// Log initialization events
        /// </summary>
        public static void LogInitialization(string component, bool success, string? details = null)
        {
            Log("Init", $"{component} {(success ? "initialized" : "failed")}",
                success ? LogLevel.Info : LogLevel.Error,
                new Dictionary<string, object> { ["details"] = details ?? "none" });
        }

        /// <summary>
        /// Log performance metrics
        /// </summary>
        public static void LogPerformance(string operation, long elapsedMs, Dictionary<string, object>? context = null)
        {
            context ??= new Dictionary<string, object>();
            context["elapsed_ms"] = elapsedMs;

            var level = elapsedMs > 100 ? LogLevel.Warning : LogLevel.Debug;
            Log("Performance", operation, level, context);
        }

        /// <summary>
        /// Get recent logs for diagnostics
        /// </summary>
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
                return Infrastructure.CompatibilityLayer.GetActivationDelayElapsedDays();
            }
            catch
            {
                return 0f;
            }
        }
    }

    // ── VerificationCommands ─────────────────────────────────────────
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

            // 1. Static Cache
            var cache = StaticDataCache.Instance;
            _ = sb.AppendLine($"[StaticDataCache] Hideouts: {cache.AllHideouts.Count}, Villages: {cache.AllVillages.Count}, Towns: {cache.AllTowns.Count}");

            // 2. Staggered Ticks (AiPatrollingBehaviorPatch)
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

            // 3. Swarm Paging
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
            _ = sb.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
            _ = sb.AppendLine("  SPAWN PIPELINE â€” KAPI TESTÄ°");
            _ = sb.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");

            int passCount = 0;
            int failCount = 0;

            void Check(string name, bool condition, string failDetail = "")
            {
                if (condition)
                {
                    _ = sb.AppendLine($"  âœ… PASS: {name}");
                    passCount++;
                }
                else
                {
                    string detail = string.IsNullOrEmpty(failDetail) ? "" : $" ({failDetail})";
                    _ = sb.AppendLine($"  âŒ FAIL: {name}{detail}");
                    failCount++;
                }
            }

            bool hasCampaign = Campaign.Current != null;
            Check("Campaign.Current", hasCampaign, "Oyun kampanyasÄ± yÃ¼klenmemiÅŸ");

            bool hasSettings = Settings.Instance != null;
            Check("Settings.Instance", hasSettings, "MCM/Settings baÅŸlatÄ±lmamÄ±ÅŸ");

            bool spawnEnabled = Settings.Instance?.MilitiaSpawn ?? true;
            Check("Settings.MilitiaSpawn", spawnEnabled, "MCM'de kapalÄ±");

            Check("Spawner.DailyChanceBand",
                MilitiaSpawningSystem.BaseDailySpawnChanceMin > 0f
                && MilitiaSpawningSystem.BaseDailySpawnChanceMax > MilitiaSpawningSystem.BaseDailySpawnChanceMin,
                $"DeÄŸer: {MilitiaSpawningSystem.BaseDailySpawnChanceMin:P0}-{MilitiaSpawningSystem.BaseDailySpawnChanceMax:P0}");

            int infantryCount = BanditMilitias.Core.Config.Globals.BasicInfantry.Count;
            Check("Globals.BasicInfantry", infantryCount > 0, $"SayÄ±: {infantryCount}");

            var lootersClan = ClanCache.GetLootersClan();
            var fallbackClan = ClanCache.GetFallbackBanditClan();
            Check("ClanCache.LootersClan", lootersClan != null, "Looters klanÄ± bulunamadÄ±");
            Check("ClanCache.FallbackClan", fallbackClan != null, "Yedek haydut klanÄ± bulunamadÄ±");

            int hideoutCount = ModuleManager.Instance.HideoutCache.Count;
            int activeHideouts = ModuleManager.Instance.HideoutCache.Count(h => h != null && h.IsHideout && h.IsActive);
            Check("HideoutCache", hideoutCount > 0, $"Total: {hideoutCount}");
            Check("ActiveHideouts", activeHideouts > 0, $"Aktif: {activeHideouts}");

            int currentMilitias = ModuleManager.Instance.GetMilitiaCount();
            int maxMilitias = Settings.Instance?.MaxTotalMilitias ?? 60;
            Check("MaxTotalMilitias limiti", currentMilitias < maxMilitias,
                $"Mevcut: {currentMilitias}/{maxMilitias}");

            if (hasCampaign)
            {
                float elapsedDays = CompatibilityLayer.GetActivationDelayElapsedDays();
                int delay = Settings.Instance?.ActivationDelay ?? 1;
                Check("ActivationDelay", CompatibilityLayer.HasActivationDelayElapsed(delay),
                    $"GeÃ§en gÃ¼n: {elapsedDays:F1}, Gereken: {delay}");
            }
            else
            {
                Check("ActivationDelay", false, "Campaign yok, kontrol yapÄ±lamadÄ±");
            }

            var spawner = ModuleManager.Instance.GetModule<MilitiaSpawningSystem>();
            Check("SpawningSystem kayÄ±tlÄ±", spawner != null, "ModuleManager'da yok");
            if (spawner != null)
            {
                Check("SpawningSystem.IsEnabled", spawner.IsEnabled, "ModÃ¼l devre dÄ±ÅŸÄ±");
            }

            _ = sb.AppendLine("â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€");
            _ = sb.AppendLine($"  SONUÃ‡: {passCount} PASS / {failCount} FAIL");
            if (failCount == 0)
                _ = sb.AppendLine("  â†’ TÃ¼m kapÄ±lar aÃ§Ä±k â€” spawn Ã§alÄ±ÅŸmalÄ±!");
            else
                _ = sb.AppendLine($"  â†’ {failCount} engel spawn'Ä± blokluyor!");
            _ = sb.AppendLine("â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");

            string result = sb.ToString();
            try { FileLogger.Log($"[PipelineCheck]\n{result}"); } catch { }
            return result;
        }
    }

}
