using BanditMilitias.Core.Events;
using BanditMilitias.Infrastructure;
using BanditMilitias.Systems.Tracking;
using BanditMilitias.Systems.Spawning;
using BanditMilitias.Intelligence.Strategic;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;

namespace BanditMilitias.Debug
{
    public class DebugPanel
    {
        private Action<MilitiaKilledEvent>? _onKilled;
        private Action<HideoutClearedEvent>? _onCleared;
        private Action<MilitiaMergeEvent>? _onMerge;
        private Action<MilitiaRaidEvent>? _onRaid;
        private Action<AIDecisionEvent>? _onAIDecision;
        private string? _lastPanelMessage = null;
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
            sb.AppendLine("=== DETAILED STATISTICS ===");
            sb.AppendLine(ModuleManager.Instance.GetDiagnostics());
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
                if (Settings.Instance != null)
                {
                    Settings.Instance.TestingMode = !Settings.Instance.TestingMode;
                    Settings.Instance.ShowTestMessages = Settings.Instance.TestingMode;
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[SHORTCUT] Test Mode: {(Settings.Instance.TestingMode ? "ENABLED" : "DISABLED")}",
                        Settings.Instance.TestingMode ? Colors.Green : Colors.Yellow));
                }
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

            bool bootstrapDone = ModuleManager.Instance.IsSessionBootstrapComplete;
            string bootStatus = bootstrapDone ? "OK" : "PENDING";

            string delayStatus = "";
            try
            {
                float elapsed = Infrastructure.ModActivationManager.GetActivationDelayElapsedDays();
                int required = Settings.Instance?.ActivationDelay ?? 2;
                bool switchClosed = Infrastructure.ModActivationManager.IsGameplayActivationSwitchClosed();
                delayStatus = switchClosed ? "ACTIVE" : $"{elapsed:F1}d/{required}d";
            }
            catch { delayStatus = "?"; }

            var message = $"[BANDIT DEBUG] Boot:{bootStatus} | Delay:{delayStatus} | Militia:{stats.TotalMilitias} | Active:{stats.ActiveParties} | Spawn:{(spawnEnabled ? "ON" : "OFF")} | Hideout(A/T):{activeHideouts}/{totalHideouts} | {captivityStatus}";

            if (message == _lastPanelMessage)
                return;

            _lastPanelMessage = message;
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
            if (!int.TryParse(args.Length > 0 ? args[0] : "1", out int count) || count <= 0)
            {
                InformationManager.DisplayMessage(new InformationMessage("Error: Invalid number. Enter a positive integer. (e.g., bandit.exec sys_spawn 5)", Colors.Red));
                return;
            }
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
            if (!float.TryParse(args.Length > 0 ? args[0] : "50", out float radius) || radius <= 0)
            {
                InformationManager.DisplayMessage(new InformationMessage("Error: Invalid radius. Enter a positive number. (e.g., bandit.exec sys_kill_scan 3)", Colors.Red));
                return;
            }

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
}
