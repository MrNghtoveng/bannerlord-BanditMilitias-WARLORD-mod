using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using BanditMilitias.Systems.Cleanup;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Economy;

namespace BanditMilitias.Systems.Diagnostics
{


    [BanditMilitias.Core.Components.AutoRegister(Priority = 350, IsCritical = false)]
    public class MilitiaAssertionSystem : MilitiaModuleBase
    {
        private static MilitiaAssertionSystem? _instance;
        public static MilitiaAssertionSystem Instance =>
            _instance ??= ModuleManager.Instance.GetModule<MilitiaAssertionSystem>()
                          ?? new MilitiaAssertionSystem();

        public override string ModuleName => "MilitiaAssertionSystem";
        public override bool IsEnabled => true;
        public override int Priority => 85;


        private const int    MILITIA_COUNT_WARNING_RATIO  = 90;

        private const int    MILITIA_COUNT_CRITICAL_RATIO = 100;

        private const int    NULL_HERO_ALERT_THRESHOLD    = 3;

        private const float  MEMORY_WARNING_MB            = 800f;

        private const float  MEMORY_CRITICAL_MB           = 1400f;
        private const int    AI_STAGNATION_SAME_ACTION    = 6;

        private const int    HOURS_BETWEEN_CHECKS         = 4;


        private double _lastCheckHour = 0;
        private int    _totalAssertionViolations = 0;
        private int    _totalAutoHeals = 0;


        private readonly Dictionary<string, (string LastAction, int RepeatCount)> _aiStagnationMap
            = new Dictionary<string, (string, int)>();


        private readonly Queue<string> _recentViolations = new Queue<string>(50);


        private readonly Dictionary<string, (Vec2 Position, double Hour)> _stasisTracker = new();

        public override void Initialize()
        {
            _instance = this;
            _lastCheckHour = 0;
            _aiStagnationMap.Clear();
            _recentViolations.Clear();
        }

        public override void Cleanup()
        {
            _instance = null;
            _aiStagnationMap.Clear();
        }

        public override void OnHourlyTick()
        {
            if (!IsEnabled) return;
            if (ModActivationManager.IsGameplayActivationDelayed()) return;
            if (Campaign.Current == null) return;

            double nowHours = CampaignTime.Now.ToHours;
            if (nowHours - _lastCheckHour < HOURS_BETWEEN_CHECKS) return;
            _lastCheckHour = nowHours;

            RunAllAssertions();
        }


        public AssertionReport RunAllAssertions()
        {
            var report = new AssertionReport();

            AssertMilitiaCount(report);
            AssertNoNullHeroes(report);
            AssertMemoryUsage(report);
            AssertAINotStagnated(report);
            AssertEventBusHealth(report);


            ProbeLinkIntegrity(report);
            ProbeLiquidityBridge(report);
            ProbeStasisDetection(report);


            PartyCleanupSystem.Instance.PerformDeepClean();

            _totalAssertionViolations += report.ViolationCount;

            if (report.ViolationCount > 0 && Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Warning("MilitiaAssertions",
                    $"Assertion cycle: {report.ViolationCount} violations, {report.AutoHealCount} auto-fixes.");
            }

            return report;
        }


        private void AssertMilitiaCount(AssertionReport report)
        {
            if (Settings.Instance == null) return;

            int maxAllowed  = Settings.Instance.MaxTotalMilitias;
            int current     = ModuleManager.Instance?.GetMilitiaCount() ?? 0;
            int warningAt   = maxAllowed * MILITIA_COUNT_WARNING_RATIO / 100;

            if (current > maxAllowed)
            {
                string msg = $"[ASSERT FAIL] Militia count limit exceeded: {current}/{maxAllowed}";
                RecordViolation(report, msg);


                int toRemove = current - maxAllowed;
                AutoHealRemoveWeakestMilitias(toRemove, report);
            }
            else if (current > warningAt)
            {
                string msg = $"[ASSERT WARN] Militia count is near critical threshold: {current}/{maxAllowed} ({MILITIA_COUNT_WARNING_RATIO}%)";
                report.Warnings.Add(msg);
                if (Settings.Instance.TestingMode)
                    DebugLogger.Warning("MilitiaAssertions", msg);
            }
        }

        private void AutoHealRemoveWeakestMilitias(int count, AssertionReport report)
        {
            try
            {
                var weakest = Campaign.Current?.MobileParties
                    .Where(p => p?.IsActive == true &&
                                p.PartyComponent is MilitiaPartyComponent &&
                                p.MemberRoster != null)
                    .OrderBy(p => p.MemberRoster.TotalManCount)
                    .Take(count)
                    .ToList();

                if (weakest == null || weakest.Count == 0) return;

                foreach (var party in weakest)
                {
                    try
                    {
                        TaleWorlds.CampaignSystem.Actions.DestroyPartyAction.Apply(null, party);
                        _totalAutoHeals++;
                        report.AutoHealCount++;
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Warning("MilitiaAssertions", $"DestroyPartyAction failed in AutoHeal: {ex.Message}");
                    }
                }

                string healMsg = $"[AUTO-HEAL] {weakest.Count} weak militias cleaned up (limit violation resolved).";
                report.AutoHeals.Add(healMsg);
                DebugLogger.Warning("MilitiaAssertions", healMsg);
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("MilitiaAssertions", $"AutoHeal militia remove failed: {ex.Message}");
            }
        }


        private void AssertNoNullHeroes(AssertionReport report)
        {
            if (Campaign.Current == null) return;

            int nullHeroCount = 0;
            var orphanParties = new List<MobileParty>();

            foreach (var party in Campaign.Current.MobileParties)
            {
                if (party == null || !party.IsActive) continue;


                if (party.PartyComponent is not MilitiaPartyComponent) continue;


                if (party.LeaderHero == null || !party.LeaderHero.IsAlive)
                {
                    nullHeroCount++;
                    orphanParties.Add(party);
                }
            }

            if (nullHeroCount >= NULL_HERO_ALERT_THRESHOLD)
            {
                string msg = $"[ASSERT FAIL] {nullHeroCount} militia parties have null/dead leaders.";
                RecordViolation(report, msg);


                foreach (var orphan in orphanParties)
                {
                    try
                    {
                        ModuleManager.Instance?.UnregisterMilitia(orphan);


                        TaleWorlds.CampaignSystem.Actions.DestroyPartyAction.Apply(null, orphan);
                        _totalAutoHeals++;
                        report.AutoHealCount++;
                    }
                    catch (Exception ex)
                    {
                         DebugLogger.Warning("MilitiaAssertions", $"Failed to clean up orphan militia {orphan.StringId}: {ex.Message}");
                    }
                }

                if (orphanParties.Count > 0)
                    report.AutoHeals.Add($"[AUTO-HEAL] {orphanParties.Count} orphan militia registrations and parties deleted.");
            }
            else if (nullHeroCount > 0)
            {
                report.Warnings.Add($"[ASSERT WARN] {nullHeroCount} militia parties have null/dead leaders (below threshold, monitoring).");
            }
        }


        private void AssertMemoryUsage(AssertionReport report)
        {
            try
            {
                long memBytes   = GC.GetTotalMemory(forceFullCollection: false);
                float memMB     = memBytes / (1024f * 1024f);

                if (memMB > MEMORY_CRITICAL_MB)
                {
                    string msg = $"[ASSERT FAIL] RAM usage critical: {memMB:F0} MB (threshold: {MEMORY_CRITICAL_MB} MB)";
                    RecordViolation(report, msg);


                    GC.Collect(1, GCCollectionMode.Optimized, blocking: false);
                    report.AutoHeals.Add($"[AUTO-HEAL] GC.Collect(1) triggered — RAM pressure reduced.");
                    _totalAutoHeals++;
                    report.AutoHealCount++;
                }
                else if (memMB > MEMORY_WARNING_MB)
                {
                    report.Warnings.Add($"[ASSERT WARN] RAM usage high: {memMB:F0} MB (warning threshold: {MEMORY_WARNING_MB} MB)");
                }


                DiagnosticsSystem.SetMetric("MilitiaAssertions.MemoryMB", memMB);
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("MilitiaAssertions", $"GC stats check failed: {ex.Message}");
            }
        }


        private void AssertAINotStagnated(AssertionReport report)
        {
            if (Campaign.Current == null) return;

            var stagnatedParties = new List<string>();

            foreach (var party in Campaign.Current.MobileParties)
            {
                if (party == null || !party.IsActive) continue;
                if (party.PartyComponent is not MilitiaPartyComponent comp) continue;

                string partyId   = party.StringId;
                string curAction = comp.CurrentOrder?.TargetParty?.Name?.ToString()
                                   ?? comp.CurrentOrder?.Type.ToString()
                                   ?? "Idle";

                if (_aiStagnationMap.TryGetValue(partyId, out var prev))
                {
                    if (prev.LastAction == curAction)
                    {
                        int newCount = prev.RepeatCount + 1;
                        _aiStagnationMap[partyId] = (curAction, newCount);

                        if (newCount >= AI_STAGNATION_SAME_ACTION)
                        {
                            stagnatedParties.Add(partyId);
                        }
                    }
                    else
                    {
                        _aiStagnationMap[partyId] = (curAction, 1);
                    }
                }
                else
                {
                    _aiStagnationMap[partyId] = (curAction, 1);
                }
            }


            var activeIdSet = new HashSet<string>(
                Campaign.Current?.MobileParties
                    .Where(p => p?.IsActive == true)
                    .Select(p => p.StringId)
                ?? Enumerable.Empty<string>());

            var toRemove = _aiStagnationMap.Keys
                .Where(id => !activeIdSet.Contains(id))
                .ToList();
            foreach (var id in toRemove) _aiStagnationMap.Remove(id);

            if (stagnatedParties.Count > 0)
            {
                string msg = $"[ASSERT WARN] {stagnatedParties.Count} militia parties show signs of AI stagnation " +
                             $"({AI_STAGNATION_SAME_ACTION}+ consecutive same actions).";
                report.Warnings.Add(msg);

                if (Settings.Instance?.TestingMode == true)
                    DebugLogger.Warning("MilitiaAssertions", msg + " IDs: " + string.Join(", ", stagnatedParties.Take(5)));


                foreach (var pid in stagnatedParties)
                {
                    var party = Campaign.Current?.MobileParties.FirstOrDefault(p => p.StringId == pid);
                    if (party != null)
                    {
                        _aiStagnationMap[pid] = ("Reset", 0);
                        if (party.PartyComponent is MilitiaPartyComponent comp)
                        {
                            comp.CurrentOrder = null;
                        }
                    }
                }
                report.AutoHeals.Add($"[AUTO-HEAL] Stagnation state reset for {stagnatedParties.Count} stuck militias.");
                _totalAutoHeals++;
                report.AutoHealCount++;
            }
        }


        private void AssertEventBusHealth(AssertionReport report)
        {
            try
            {
                var bus = BanditMilitias.Core.Events.EventBus.Instance;
                if (bus == null) return;

                string diag = bus.GetQueueDiagnostics();


                int dropped = 0;
                int queueSize = 0;

                int qIdx = diag.IndexOf("Queue=", StringComparison.Ordinal);
                int dIdx = diag.IndexOf("Dropped=", StringComparison.Ordinal);

                if (qIdx >= 0)
                {
                    string after = diag.Substring(qIdx + 6);
                    int slash = after.IndexOf('/');
                    if (slash >= 0 && int.TryParse(after.Substring(0, slash), out int q))
                        queueSize = q;
                }

                if (dIdx >= 0)
                {
                    string after = diag.Substring(dIdx + 8);
                    int end = after.IndexOfAny(new[] { ',', ' ', '\n' });
                    string numStr = end >= 0 ? after.Substring(0, end) : after;
                    int.TryParse(numStr.Trim(), out dropped);
                }

                DiagnosticsSystem.SetMetric("EventBus.QueueSize", queueSize);
                DiagnosticsSystem.SetMetric("EventBus.DroppedEvents", dropped);

                if (dropped > 500)
                {
                    string msg = $"[ASSERT FAIL] EventBus dropped {dropped} events. " +
                                 $"Queue: {queueSize}/5000. Reduce simulation speed or MaxAITasksPerTick.";
                    RecordViolation(report, msg);
                }
                else if (dropped > 100)
                {
                    report.Warnings.Add($"[ASSERT WARN] EventBus dropped {dropped} events. High load monitored.");
                }
            }
            catch { }
        }


        private void ProbeLinkIntegrity(AssertionReport report)
        {
            if (WarlordSystem.Instance == null) return;

            var allWarlords = WarlordSystem.Instance.GetAllWarlords();
            int brokenLinks = 0;

            foreach (var warlord in allWarlords)
            {


                if (warlord.LinkedHero == null || !warlord.LinkedHero.IsAlive)
                {
                    brokenLinks++;
                    if (Settings.Instance?.TestingMode == true)
                        DebugLogger.Warning("DeepProbe", $"Broken Link: Warlord {warlord.Name} has null or dead LinkedHero.");
                }


                var staleParties = warlord.CommandedMilitias.Where(p => p == null || !p.IsActive).ToList();
                if (staleParties.Count > 0)
                {
                    brokenLinks += staleParties.Count;
                    foreach (var p in staleParties)
                    {
                        warlord.ReleaseMilitia(p);
                        if (Settings.Instance?.TestingMode == true)
                            DebugLogger.Warning("DeepProbe", $"Auto-Fixed: Removed stale party link for Warlord {warlord.Name}");
                    }
                }
            }

            if (brokenLinks > 0)
            {
                string msg = $"[PROBE FAIL] Link Integrity: {brokenLinks} broken links detected and cleaned.";
                RecordViolation(report, msg);
            }
        }


        private void ProbeLiquidityBridge(AssertionReport report)
        {
            if (WarlordEconomySystem.Instance == null) return;

            var lastSync = WarlordEconomySystem.Instance.LastGoldSyncTime;
            if (lastSync == CampaignTime.Zero) return;


            double hoursSinceLastSync = (CampaignTime.Now - lastSync).ToHours;


            if (hoursSinceLastSync > 24)
            {
                var wealthyWarlord = WarlordSystem.Instance.GetAllWarlords().FirstOrDefault(w => w.Gold > 10000);
                if (wealthyWarlord != null)
                {
                    string msg = $"[PROBE WARN] Liquidity Bridge: {hoursSinceLastSync:F1} saattir altın transferi yapılmadı (Potansiyel tıkanıklık).";
                    report.Warnings.Add(msg);
                }
            }
        }


        private void ProbeStasisDetection(AssertionReport report)
        {
            if (Campaign.Current == null) return;

            double now = CampaignTime.Now.ToHours;
            int stasisCount = 0;

            foreach (var party in Campaign.Current.MobileParties)
            {
                if (party == null || !party.IsActive || party.PartyComponent is not MilitiaPartyComponent) continue;

                string id = party.StringId;
                Vec2 currentPos = CompatibilityLayer.GetPartyPosition(party);

                if (_stasisTracker.TryGetValue(id, out var prev))
                {
                    double hoursPassed = now - prev.Hour;
                    if (hoursPassed >= 12)

                    {
                        float dist = currentPos.Distance(prev.Position);
                        if (dist < 0.5f)

                        {
                            stasisCount++;
                            if (Settings.Instance?.TestingMode == true)
                                DebugLogger.Warning("DeepProbe", $"Stasis Detected: Party {party.Name} (ID: {id}) hasn't moved for 12h. Dist: {dist:F2}");


                            if (party.PartyComponent is MilitiaPartyComponent comp)
                            {
                                comp.CurrentOrder = null;
                                if (party.Ai != null) party.Ai.SetDoNotMakeNewDecisions(false);
                            }
                        }


                        _stasisTracker[id] = (currentPos, now);
                    }
                }
                else
                {
                    _stasisTracker[id] = (currentPos, now);
                }
            }


            if (now % 24 < 1)

            {
                var activeIds = new HashSet<string>(Campaign.Current.MobileParties.Select(p => p.StringId));
                var staleKeys = _stasisTracker.Keys.Where(k => !activeIds.Contains(k)).ToList();
                foreach (var k in staleKeys) _stasisTracker.Remove(k);
            }

            if (stasisCount > 0)
            {
                string msg = $"[PROBE FAIL] Stasis Detection: {stasisCount} stuck parties detected and AI reset.";
                RecordViolation(report, msg);
            }
        }


        private void RecordViolation(AssertionReport report, string message)
        {
            report.Violations.Add(message);
            report.ViolationCount++;

            if (_recentViolations.Count >= 50) _recentViolations.Dequeue();
            _recentViolations.Enqueue($"[{CampaignTime.Now.ToHours:F0}h] {message}");

            DebugLogger.Error("MilitiaAssertions", message);

            if (Settings.Instance?.TestingMode == true)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[BM Assert] {message}", Colors.Red));
            }
        }

        public string GetAssertionSummary()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== MilitiaAssertionSystem Report ===");
            sb.AppendLine($"Total violations (session): {_totalAssertionViolations}");
            sb.AppendLine($"Total auto-fixes : {_totalAutoHeals}");
            sb.AppendLine($"Last check hour : {_lastCheckHour:F0}h");
            sb.AppendLine($"AI stagnation tracking : {_aiStagnationMap.Count} parties");
            sb.AppendLine($"\nRecent violations ({_recentViolations.Count}):");
            foreach (var v in _recentViolations.Reverse())
                sb.AppendLine($"  {v}");
            return sb.ToString();
        }


        [CommandLineFunctionality.CommandLineArgumentFunction("assert_check", "militia")]
        public static string CommandAssertCheck(List<string> args)
        {
            if (Campaign.Current == null)
                return "[BanditMilitias] Campaign not active.";

            var inst = ModuleManager.Instance.GetModule<MilitiaAssertionSystem>();
            if (inst == null)
                return "[BanditMilitias] MilitiaAssertionSystem not found.";

            var report = inst.RunAllAssertions();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[BanditMilitias] Assertion Check Completed");
            sb.AppendLine($"  Violation: {report.ViolationCount}  Warning: {report.Warnings.Count}  Auto-fix: {report.AutoHealCount}");

            if (report.Violations.Count > 0)
            {
                sb.AppendLine("\nVIOLATIONS:");
                foreach (var v in report.Violations) sb.AppendLine($"  ❌ {v}");
            }

            if (report.Warnings.Count > 0)
            {
                sb.AppendLine("\nWARNINGS:");
                foreach (var w in report.Warnings) sb.AppendLine($"  ⚠ {w}");
            }

            if (report.AutoHeals.Count > 0)
            {
                sb.AppendLine("\nAUTO-FIXES:");
                foreach (var h in report.AutoHeals) sb.AppendLine($"  ✔ {h}");
            }

            if (report.ViolationCount == 0 && report.Warnings.Count == 0)
                sb.AppendLine("  ✅ All assertions passed. System healthy.");

            return sb.ToString();
        }


        [CommandLineFunctionality.CommandLineArgumentFunction("assert_summary", "militia")]
        public static string CommandAssertSummary(List<string> args)
        {
            var inst = ModuleManager.Instance.GetModule<MilitiaAssertionSystem>();
            if (inst == null) return "[BanditMilitias] MilitiaAssertionSystem not found.";
            return inst.GetAssertionSummary();
        }

        public override void OnDailyTick()
        {


            if (Campaign.Current == null) return;

            var activeIds = new HashSet<string>(
                Campaign.Current.MobileParties
                    .Where(p => p?.IsActive == true && p.PartyComponent is MilitiaPartyComponent)
                    .Select(p => p.StringId));

            var staleKeys = _aiStagnationMap.Keys.Where(k => !activeIds.Contains(k)).ToList();
            foreach (var k in staleKeys) _aiStagnationMap.Remove(k);
        }
    }


    public class AssertionReport
    {
        public int ViolationCount { get; set; }
        public int AutoHealCount  { get; set; }
        public List<string> Violations { get; } = new List<string>();
        public List<string> Warnings   { get; } = new List<string>();
        public List<string> AutoHeals  { get; } = new List<string>();
    }
}


