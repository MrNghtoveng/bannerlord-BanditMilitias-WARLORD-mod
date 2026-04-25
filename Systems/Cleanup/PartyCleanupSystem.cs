using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Config;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Progression;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Cleanup
{
    public class PartyCleanupSystem : MilitiaModuleBase, ICleanupSystem
    {
        private static PartyCleanupSystem? _instance;
        public static PartyCleanupSystem Instance =>
            _instance ??= ModuleManager.Instance.GetModule<PartyCleanupSystem>()
                          ?? new PartyCleanupSystem();

        public override string ModuleName => "CleanupSystem";
        public override bool IsEnabled => true;
        public override int Priority => 40;

        private bool _isInitialized = false;

        public override string GetDiagnostics()
        {
            return $"Queue: {_cleanupQueue.Count}, Quarantine: {_quarantineList.Count}";
        }

        private readonly Queue<MobileParty> _cleanupQueue = new();

        private readonly List<MobileParty> _reusableSnapshot = new(200);

        private readonly Dictionary<MobileParty, int> _quarantineList = new();
        private const int MAX_QUARANTINE_SIZE = AIConstants.MAX_QUARANTINE_SIZE;
        private const int MAX_REPAIR_ATTEMPTS = AIConstants.MAX_REPAIR_ATTEMPTS;

        private readonly Dictionary<MobileParty, CampaignTime> _gracePeriodTracker = new();
        private const float GRACE_PERIOD_HOURS = AIConstants.GRACE_PERIOD_HOURS;

        private static int GetGlobalPartyLimit()
        {
            int configured = Settings.Instance?.GlobalPerformancePartyLimit ?? 3000;
            return Math.Max(1000, configured);
        }

        private static int GetSoftCrowdingThreshold()
        {
            return Math.Max(1200, (int)(GetGlobalPartyLimit() * 0.65f));
        }

        private static int GetHardCrowdingThreshold()
        {
            return Math.Max(1800, (int)(GetGlobalPartyLimit() * 0.85f));
        }

        public override void Initialize()
        {
            if (_isInitialized) return;
            _instance = this;
            _isInitialized = true;
        }

        public void OnPartyDestroyedCleanup(MobileParty party, PartyBase partyBase)
        {
            if (party?.PartyComponent is MilitiaPartyComponent comp)
            {
                // ── YENİLGİ CEZASI: Tier'a göre Legitimacy düşüşü ────────────────────────
                try
                {
                    if (comp.WarlordId != null
                        && WarlordLegitimacySystem.Instance?.IsEnabled == true)
                    {
                        var warlordObj = WarlordSystem.Instance.GetWarlord(comp.WarlordId);
                        if (warlordObj != null)
                        {
                            var level = WarlordLegitimacySystem.Instance.GetLevel(comp.WarlordId);
                            // Düşük tier → büyük ceza, yüksek tier → küçük ceza
                            float penalty = level switch
                            {
                                LegitimacyLevel.Outlaw => -80f,   // Tier 1: çok kritik
                                LegitimacyLevel.Rebel => -60f,   // Tier 2: kritik
                                LegitimacyLevel.FamousBandit => -40f, // Tier 2: orta
                                LegitimacyLevel.Warlord => -20f,   // Tier 3: hafif
                                _ => -10f    // Tier 5+: az
                            };
                            WarlordLegitimacySystem.Instance.ApplyPoints(
                                warlordObj, penalty, "PartyDestroyed");

                            if (Settings.Instance?.TestingMode == true)
                                DebugLogger.Info("CleanupSystem",
                                    $"[YENİLGİ] {comp.WarlordId} Tier={level} Ceza={penalty:F0} puan");
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("CleanupSystem", $"Defeat penalty failed: {ex.Message}");
                }

                // ── SIVIŞMA (SLIPPERY ESCAPE) MEKANİĞİ ────────────────────────
                // Yenilen birliğin %25'i sığınağın manpower havuzuna kaçar
                try
                {
                    if (!string.IsNullOrEmpty(comp.WarlordId))
                    {
                        var warlord = WarlordSystem.Instance.GetWarlord(comp.WarlordId);
                        if (warlord != null && party.MemberRoster != null)
                        {
                            int escapedCount = (int)(party.MemberRoster.TotalManCount * 0.25f);
                            if (escapedCount > 0)
                            {
                                warlord.ReserveManpower += escapedCount;
                                if (Settings.Instance?.TestingMode == true)
                                    DebugLogger.Info("CleanupSystem", $"[SIVIŞMA] {party.Name} yenildi. {escapedCount} asker ormana kaçıp {warlord.Name} havuzuna döndü.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("CleanupSystem", $"Slippery Escape failed: {ex.Message}");
                }
                // ────────────────────────────────────────────────────────────────

                try
                {
                    if (party.LeaderHero != null
                        && party.LeaderHero.IsAlive
                        && party.LeaderHero.Occupation == Occupation.Bandit
                        && party.LeaderHero.Clan?.IsBanditFaction == true)
                    {
                        TaleWorlds.CampaignSystem.Actions.KillCharacterAction.ApplyByRemove(
                            party.LeaderHero, false);
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("CleanupSystem", $"Captain hero cleanup failed: {ex.Message}");
                }

                _ = _quarantineList.Remove(party);
                _ = _gracePeriodTracker.Remove(party);
                // _cleanupQueue contains logic - list handled via queue logic

                var disbandEvt = BanditMilitias.Core.Events.EventBus.Instance.Get<BanditMilitias.Core.Events.MilitiaDisbandedEvent>();
                disbandEvt ??= new BanditMilitias.Core.Events.MilitiaDisbandedEvent();
                disbandEvt.Party = party;
                BanditMilitias.Core.Neural.NeuralEventRouter.Instance.Publish(disbandEvt);
                BanditMilitias.Core.Events.EventBus.Instance.Return(disbandEvt);
            }
        }
        public override void OnDailyTick()
        {

            if (Settings.Instance?.UninstallMode == true)
            {
                PerformUninstallCleanup();
                return;
            }

            DailyCleanup();

            if (Settings.Instance?.EnableAggressiveCleanup == true)
            {
                ExecuteInternalDestruction();
            }
        }

        private void PerformUninstallCleanup()
        {
            if (Campaign.Current == null) return;

            int deletedCount = 0;
            // MobileParty.All (tüm harita) yerine kayıtlı milis listesi
            var toDelete = new List<MobileParty>(ModuleManager.Instance.ActiveMilitias);

            foreach (var party in toDelete)
            {

                ExecuteNuclearCleanup(party);
                deletedCount++;
            }

            if (deletedCount > 0 || Settings.Instance?.TestingMode == true)
            {
                string msg = $"[Bandit Militias] UNINSTALL MODE: Cleaned {deletedCount} parties. " +
                             "System is now clean. Save your game and disable the mod.";
                InformationManager.DisplayMessage(new InformationMessage(msg, Colors.Green));

                BanditMilitias.Intelligence.Strategic.WarlordSystem.Instance.Cleanup();
                BanditMilitias.Intelligence.AI.Components.MilitiaSmartCache.Instance.Clear();

                // ✅ Bir kez çalışıp durmalı - Otomatik olarak kapatıyoruz
                if (Settings.Instance != null)
                {
                    Settings.Instance.UninstallMode = false;
                }
            }
        }

        private int _hourlyTickCounter = 0;

        public override void OnHourlyTick()
        {
            _hourlyTickCounter++;

            if (_hourlyTickCounter % 2 == 0)
            {
                RunSafetySweep();
            }

            if (_hourlyTickCounter % 48 == 0)
            {
                AttemptQuarantineRepairs();
            }

            // Always drain deletion queue each hour. Keeping this behind aggressive mode
            // allowed zombie buildup to persist for too long.
            ExecuteInternalDestruction();

            bool aggressive = Settings.Instance?.EnableAggressiveCleanup == true;
            if (aggressive && _hourlyTickCounter % 48 == 0)
            {
                ScanAndMigrateOrphans();
            }

            int totalParties = Campaign.Current?.MobileParties?.Count ?? 0;
            if (totalParties > GetHardCrowdingThreshold() && _hourlyTickCounter % 168 == 0)
            {
                GlobalThinning(totalParties, emergencyMode: totalParties > GetGlobalPartyLimit(), hourlyMode: true);
            }
        }

        private void RunSafetySweep()
        {
            foreach (var party in ModuleManager.Instance.ActiveMilitias)
            {
                if (party == null || !party.IsActive) continue;
                if (party.PartyComponent is not MilitiaPartyComponent) continue;

                _ = TryRepairParty(party);
                FixPassive(party);
            }
        }

        private void AttemptQuarantineRepairs()
        {

            if (_quarantineList.Count >= MAX_QUARANTINE_SIZE)
            {

                // Manuel min-scan: allocation-free en eski karantina girişini bul
                MobileParty? oldest = null;
                int minAttempts = int.MaxValue;
                foreach (var kvp in _quarantineList)
                {
                    if (kvp.Value < minAttempts)
                    {
                        minAttempts = kvp.Value;
                        oldest = kvp.Key;
                    }
                }
                if (oldest != null)
                {
                    bool repaired = TryRepairParty(oldest);
                    if (!repaired)
                    {

                        ExecuteNuclearCleanup(oldest);
                    }
                    _ = _quarantineList.Remove(oldest);
                }
            }
            if (_quarantineList.Count == 0) return;

            var toRemove = new List<MobileParty>();

            foreach (var kvp in _quarantineList)
            {
                var party = kvp.Key;
                int attempts = kvp.Value;

                if (party == null || !party.IsActive)
                {
                    if (party != null) toRemove.Add(party);
                    continue;
                }

                if (TryRepairParty(party))
                {

                    toRemove.Add(party);
                    LogRepairSuccess(party);
                }
                else
                {

                    _quarantineList[party] = attempts + 1;

                    if (attempts + 1 >= MAX_REPAIR_ATTEMPTS)
                    {

                        ExecuteNuclearCleanup(party);
                        toRemove.Add(party);
                        LogRepairFailure(party);
                    }
                }
            }

            foreach (var p in toRemove)
            {
                _ = _quarantineList.Remove(p);
            }
        }

        private bool TryRepairParty(MobileParty party)
        {
            if (party == null || !party.IsActive) return false;

            bool wasRepaired = false;

            if (party.ActualClan == null)
            {

                var looters = BanditMilitias.Infrastructure.ClanCache.GetLootersClan()
                           ?? BanditMilitias.Infrastructure.ClanCache.GetFallbackBanditClan();

                if (looters != null)
                {
                    party.ActualClan = looters;
                    wasRepaired = true;
                }
            }

            if (party.MapFaction == null && party.ActualClan != null)
            {

                var clan = party.ActualClan;
                party.ActualClan = clan;
                wasRepaired = true;
            }
            else if (party.MapFaction == null && party.ActualClan == null)
            {

                var looters = BanditMilitias.Infrastructure.ClanCache.GetLootersClan()
                           ?? BanditMilitias.Infrastructure.ClanCache.GetFallbackBanditClan();

                if (looters != null)
                {
                    party.ActualClan = looters;
                    wasRepaired = true;
                    if (Settings.Instance?.TestingMode == true)
                        BanditMilitias.Debug.DebugLogger.TestLog($"[Cleanup] Headless party {party.Name} assigned to Looters.", TaleWorlds.Library.Colors.Cyan);
                }
            }

            if (party.PartyComponent is MilitiaPartyComponent militiaComp)
            {
                var home = militiaComp.GetHomeSettlement();
                if (home == null || !home.IsActive)
                {

                    bool migrated = TryMigrate(party);
                    if (migrated)
                    {
                        wasRepaired = true;
                    }
                }
            }

            if (party.ActualClan != null && party.MapFaction != null)
            {
                return true;
            }

            return wasRepaired;
        }

        private void ScanAndMigrateOrphans()
        {

            if (ModuleManager.Instance == null) return;

            List<MobileParty> registry = new();
            ModuleManager.Instance.PopulateSnapshot(registry);

            for (int i = registry.Count - 1; i >= 0; i--)
            {
                var party = registry[i];
                if (party == null || !party.IsActive) continue;

                if (IsAnomaly(party))
                {

                    if (!TryMigrate(party))
                    {

                        ExecuteNuclearCleanup(party);
                    }
                }
            }
        }

        public void MigrateSurvivors(Settlement destroyedHideout)
        {
            if (destroyedHideout == null) return;
            if (ModuleManager.Instance == null) return;

            List<MobileParty> registry = new();
            ModuleManager.Instance.PopulateSnapshot(registry);

            int migratedCount = 0;

            for (int i = 0; i < registry.Count; i++)
            {
                var party = registry[i];
                if (party == null || !party.IsActive) continue;

                if (party.PartyComponent is MilitiaPartyComponent comp)
                {

                    var home = comp.GetHomeSettlement();
                    if (home == destroyedHideout)
                    {

                        if (TryMigrate(party))
                        {
                            migratedCount++;
                        }
                        else
                        {

                            ExecuteNuclearCleanup(party);
                        }
                    }
                }
            }

            if (migratedCount > 0 && Settings.Instance?.TestingMode == true)
            {
                InformationManager.DisplayMessage(new InformationMessage($"[BanditMilitias] {migratedCount} parties migrated from destroyed {destroyedHideout.Name}", Colors.Green));
            }
        }

        public override void SyncData(IDataStore dataStore)
        {
            Dictionary<string, double> buffer = new();

            if (dataStore.IsSaving)
            {
                foreach (var kvp in _gracePeriodTracker)
                {
                    if (kvp.Key != null && kvp.Key.StringId != null)
                    {
                        buffer[kvp.Key.StringId] = kvp.Value.ToHours;
                    }
                }
            }

            _ = dataStore.SyncData("_gracePeriodTracker", ref buffer);

            if (dataStore.IsLoading)
            {
                _gracePeriodTracker.Clear();

            }
        }

        public void RegisterNewParty(MobileParty party)
        {
            if (party != null && !_gracePeriodTracker.ContainsKey(party))
            {
                _gracePeriodTracker[party] = CampaignTime.Now;
            }
        }

        public override void Cleanup()
        {
            _instance = null;
            _cleanupQueue.Clear();
            CampaignEvents.MobilePartyDestroyed.ClearListeners(this);
        }

        public void DailyCleanup()
        {
            // Haftalık tetikleme artık MilitiaAssertionSystem tarafından HOURS_BETWEEN_CHECKS (168h) ile yapılıyor.
            // Bu metot artık boş kalabilir veya günlük hafif temizlik için kullanılabilir.
        }

        public bool IsZombie(MobileParty party) 
        {
            if (party == null || party.MemberRoster == null) return true;
            if (party.MemberRoster.TotalManCount <= 0) return true;
            return false;
        }
        public void RemoveZombie(MobileParty party) => ExecuteNuclearCleanup(party);

        public void FixPassive(MobileParty party)
        {
            if (party == null || !party.IsActive) return;

            if (party.PartyComponent is MilitiaPartyComponent comp)
            {

                if (comp.Role == MilitiaPartyComponent.MilitiaRole.Guardian)
                {
                    comp.Role = MilitiaPartyComponent.MilitiaRole.Raider;
                    party.Aggressiveness = 1.0f;
                }

                if (party.Ai != null && (party.DefaultBehavior == AiBehavior.Hold || party.DefaultBehavior == AiBehavior.None))
                {
                    party.Ai.SetDoNotMakeNewDecisions(false);
                    var home = comp.GetHomeSettlement();
                    if (home != null)
                    {
                        CompatibilityLayer.SetMovePatrolAroundSettlement(party, home);
                    }
                }
            }
        }

        public bool CheckAndMark(MobileParty party)
        {
            if (IsZombie(party))
            {
                MarkForDeletion(party);
                return false;
            }
            if (party.IsActive)
            {
                FixPassive(party);
                return true;
            }
            return false;
        }

        public void MarkForDeletion(MobileParty party)
        {
            if (party == null) return;
            if (!_cleanupQueue.Contains(party))
            {
                _cleanupQueue.Enqueue(party);
            }
        }

        public void CleanupDeadParties()
        {
            ExecuteInternalDestruction();
        }

        public void ExecuteInternalDestruction()
        {
            if (_cleanupQueue.Count == 0) return;

            int processed = 0;
            // Dinamik batch boyutu: dünya nüfusu > 2000 ise agresif temizlik
            int totalParties = Campaign.Current?.MobileParties?.Count ?? 0;
            int maxPerBatch = totalParties > 2000
                ? MBRandom.RandomInt(50, 100)
                : MBRandom.RandomInt(5, 10);

            while (_cleanupQueue.Count > 0 && processed < maxPerBatch)
            {
                var zombie = _cleanupQueue.Dequeue();
                if (zombie != null && zombie.IsActive)
                {
                    ExecuteNuclearCleanup(zombie);
                    processed++;
                }
            }
        }

        public void PerformDeepClean()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            _reusableSnapshot.Clear();

            ModuleManager.Instance.PopulateSnapshot(_reusableSnapshot);

            foreach (var party in _reusableSnapshot)
            {
                if (party?.PartyComponent is MilitiaPartyComponent comp)
                {
                    _ = CheckAndMark(party);

                    if (party.IsActive && party.MemberRoster != null && party.MemberRoster.TotalManCount > 0 && party.MemberRoster.TotalManCount < 10)
                    {
                        if (party.PartyTradeGold < 1500)
                        {
                            ApplyWeakPartyMergeLogic(party, comp);
                        }
                    }
                }
            }

            ExecuteInternalDestruction();

            // PERFORMANCE GUARD: Global Thinning for vanilla bandit parties
            if (Campaign.Current != null && Campaign.Current.MobileParties != null)
            {
                int totalParties = Campaign.Current.MobileParties.Count;
                
                // YENİ: Önce Konsolidasyon ve Milis Absorpsiyonunu çalıştır (1200+)
                if (totalParties > GetSoftCrowdingThreshold())
                {
                    MilitiaConsolidationSystem.Instance.OnHourlyTick();
                }

                if (totalParties > GetHardCrowdingThreshold())
                {
                    GlobalThinning(totalParties, emergencyMode: totalParties > GetGlobalPartyLimit(), hourlyMode: false);
                }
            }

            sw.Stop();
            BanditMilitias.Systems.Dev.DevDataCollector.Instance.RecordModuleTiming("CleanupSystem_Deep", sw.ElapsedMilliseconds);

            // OPTIMIZASYON: _lastMergeTime temizliği (Sızıntı önleme)
            if (_lastMergeTime.Count > 200)
            {
                var keysToRemove = new List<string>();
                foreach (var kvp in _lastMergeTime)
                {
                    if (kvp.Value.ElapsedHoursUntilNow > 48f)
                        keysToRemove.Add(kvp.Key);
                }
                foreach (var key in keysToRemove) _lastMergeTime.Remove(key);
            }

            // OPTIMIZASYON: .ToList() yerine buffer kullanarak temizle
            _reusableSnapshot.Clear();
            foreach (var kvp in _gracePeriodTracker)
            {
                if (!kvp.Key.IsActive && (kvp.Key.MemberRoster == null || kvp.Key.MemberRoster.TotalManCount <= 0))
                {
                    // Not: _reusableSnapshot MobileParty listesidir, burada kullanabiliriz
                    _reusableSnapshot.Add(kvp.Key);
                }
            }
            foreach (var dead in _reusableSnapshot) _ = _gracePeriodTracker.Remove(dead);
        }

        private int GetCleanupPriority(MobileParty party)
        {
            if (party?.MemberRoster == null) return 100;
            int troops = party.MemberRoster.TotalManCount;
            if (troops <= 0) return 95;
            if (party.ActualClan == null) return 85;
            if (troops < 6) return 60;
            if (troops < 10) return 45;
            return 10;
        }

        private void GlobalThinning(int currentTotal, bool emergencyMode, bool hourlyMode)
        {
            int globalLimit = GetGlobalPartyLimit();
            int targetPopulation = emergencyMode
                ? Math.Max(1500, (int)(globalLimit * 0.80f))
                : Math.Max(1500, (int)(globalLimit * 0.88f));
            int targetReduc = Math.Max(0, currentTotal - targetPopulation);

            int floor = hourlyMode ? 25 : 40;
            int ceiling = emergencyMode
                ? (hourlyMode ? 180 : 320)
                : (hourlyMode ? 90 : 160);
            int maxToClean = Math.Min(Math.Max(targetReduc, floor), ceiling);
            if (maxToClean <= 0) return;

            int cleanedCount = 0;
            var candidates = new List<MobileParty>(220);

            foreach (var party in Campaign.Current.MobileParties)
            {
                if (party == null || !party.IsActive || party.IsMainParty) continue;
                if (party.PartyComponent is MilitiaPartyComponent) continue;
                if (party.MapEvent != null || party.SiegeEvent != null) continue;

                string id = (party.StringId ?? string.Empty).ToLowerInvariant();
                bool isLooter = id.Contains("looter");
                bool isBandit = id.Contains("bandit");
                bool isZombie = party.MemberRoster == null || party.MemberRoster.TotalManCount <= 0;
                bool isHeadless = party.ActualClan == null && party.LeaderHero == null;
                bool isSmallBandit = isBandit && !isZombie && party.MemberRoster != null && party.MemberRoster.TotalManCount < 10;

                if (isZombie || isHeadless || isLooter || isSmallBandit)
                {
                    candidates.Add(party);
                    if (candidates.Count > 220) break;
                }
            }

            if (candidates.Count == 0) return;

            candidates.Sort((a, b) => GetCleanupPriority(b).CompareTo(GetCleanupPriority(a)));

            foreach (var party in candidates)
            {
                if (cleanedCount >= maxToClean) break;

                try
                {
                    MarkForDeletion(party);
                    string reason = party.MemberRoster == null || party.MemberRoster.TotalManCount <= 0
                        ? "GlobalThinning_Zombie"
                        : "GlobalThinning_CrowdedWorld";
                    LogCleanupAction(party.Name.ToString(), reason);
                    cleanedCount++;
                }
                catch { }
            }

            if (cleanedCount > 0)
            {
                ExecuteInternalDestruction();
                DebugLogger.Info("CleanupSystem",
                    $"[Performance] Global Thinning: queued {cleanedCount} parties. " +
                    $"Current world parties: {Campaign.Current.MobileParties.Count}, mode={(emergencyMode ? "EMERGENCY" : "NORMAL")}, hourly={hourlyMode}");

                if (Settings.Instance?.TestingMode == true || cleanedCount > 50)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[BanditMilitias] Performance Guard: queued {cleanedCount} excess parties for cleanup.",
                        Colors.Yellow));
                }
            }
        }

        private void LogCleanupAction(string partyName, string reason)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord", "Warlord_Logs", "BanditMilitias", "Cleanup");

                if (!Directory.Exists(dir)) _ = Directory.CreateDirectory(dir);

                string path = Path.Combine(dir, "CleanupHistory.csv");

                if (!File.Exists(path))
                {
                    File.WriteAllText(path, "Timestamp,Party,Reason\n", System.Text.Encoding.UTF8);
                }

                File.AppendAllText(
                    path,
                    SafeTelemetry.CsvRow(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), partyName, reason) + Environment.NewLine, System.Text.Encoding.UTF8);
            }
            catch { }
        }

        private readonly Dictionary<string, CampaignTime> _lastMergeTime = new();

        private void ApplyWeakPartyMergeLogic(MobileParty weakParty, MilitiaPartyComponent comp)
        {
            if (weakParty.MapEvent != null || weakParty.SiegeEvent != null) return;

            if (_lastMergeTime.TryGetValue(weakParty.StringId, out CampaignTime lastMerge))
            {
                if (lastMerge.ElapsedHoursUntilNow < 4f) return;
            }

            var weakPos = CompatibilityLayer.GetPartyPosition(weakParty);
            MobileParty? bestAlly = null;
            float minDistance = 25f;

            foreach (var ally in ModuleManager.Instance.ActiveMilitias)
            {
                if (ally == null || ally == weakParty || !ally.IsActive) continue;
                if (ally.MapEvent != null || ally.SiegeEvent != null) continue;
                if (ally.PartyComponent is MilitiaPartyComponent allyComp && allyComp.Role == comp.Role)
                {
                    string allySubFaction = allyComp.GetHomeSettlement()?.Culture?.StringId ?? "";
                    string weakSubFaction = comp.GetHomeSettlement()?.Culture?.StringId ?? "";
                    
                    if (allySubFaction == weakSubFaction || allyComp.WarlordId == comp.WarlordId)
                    {
                        float dist = weakPos.Distance(CompatibilityLayer.GetPartyPosition(ally));
                        if (dist < minDistance)
                        {
                            minDistance = dist;
                            bestAlly = ally;
                        }
                    }
                }
            }

            if (bestAlly != null)
            {
                _lastMergeTime[weakParty.StringId] = CampaignTime.Now;

                try
                {
                    BanditMilitias.Debug.DebugLogger.Info("CleanupSystem", $"Weak Party Hysteresis Merge: {weakParty.Name} ({weakParty.MemberRoster.TotalManCount}) merging into {bestAlly.Name}");
                    
                    bestAlly.MemberRoster.Add(weakParty.MemberRoster);
                    bestAlly.PrisonRoster.Add(weakParty.PrisonRoster);
                    if (bestAlly.PartyComponent is MilitiaPartyComponent allyComp2)
                    {
                        allyComp2.Gold += comp.Gold;
                    }
                    
                    BanditMilitias.Infrastructure.CompatibilityLayer.DestroyParty(weakParty);
                }
                catch (Exception ex)
                {
                    BanditMilitias.Debug.DebugLogger.Warning("CleanupSystem", $"Hysteresis Merge failed: {ex.Message}");
                }
            }
        }

        private bool IsAnomaly(MobileParty party)
        {

            if (IsBasicAnomaly(party)) return true;

            if (IsInGracePeriod(party)) return false;

            // ── YENİ: Headless parti tespiti — karantina ATLANIR ──────────────
            // Grace period sonrası hâlâ ActualClan veya HomeSettlement yoksa
            // bu parti kurtarılamaz. Askerlerini en yakın Captain'a aktar ve yok et.
            bool isHeadless = IsHeadlessParty(party);
            if (isHeadless)
            {
                TransferToNearestCaptain(party);
                return true; // Hemen temizlenecek (ScanAndMigrateOrphans zaten ExecuteNuclearCleanup çağırır)
            }

            bool isCritical = IsCriticalAnomaly(party);

            bool needsRepair = isCritical || IsBehavioralAnomaly(party);

            return needsRepair && ManageQuarantine(party);
        }

        private bool IsBasicAnomaly(MobileParty party)
        {
            if (string.IsNullOrEmpty(party.StringId)) return true;
            if (party.MemberRoster == null) return true;
            if (!party.IsActive) return true;
            return false;
        }

        private bool IsCriticalAnomaly(MobileParty party)
        {
            if (party.ActualClan == null) return true;
            return false;
        }

        private bool IsInGracePeriod(MobileParty party)
        {
            if (!_gracePeriodTracker.TryGetValue(party, out CampaignTime creationTime))
            {
                _gracePeriodTracker[party] = CampaignTime.Now;
                return true;
            }
            return creationTime.ElapsedHoursUntilNow < GRACE_PERIOD_HOURS;
        }

        private bool IsBehavioralAnomaly(MobileParty party)
        {

            if (party.MemberRoster.TotalManCount <= 0 && party.IsActive && party.IsVisible) return true;

            if (party.LeaderHero != null && !party.LeaderHero.IsAlive) return true;

            if (party.PartyComponent is MilitiaPartyComponent militiaComp)
            {
                var home = militiaComp.GetHomeSettlement();
                if (home == null) return true;
            }

            if (party.MapEvent == null && party.SiegeEvent == null &&
                (party.DefaultBehavior == AiBehavior.Hold || party.DefaultBehavior == AiBehavior.None))
            {
                return true;
            }

            return false;
        }

        private bool ManageQuarantine(MobileParty party)
        {

            if (_quarantineList.Count >= MAX_QUARANTINE_SIZE)
            {
                // Manuel min-scan: allocation-free en eski karantina girişini bul
                MobileParty? oldest = null;
                int minVal = int.MaxValue;
                foreach (var kvp in _quarantineList)
                {
                    if (kvp.Value < minVal)
                    {
                        minVal = kvp.Value;
                        oldest = kvp.Key;
                    }
                }
                if (oldest != null)
                    _ = _quarantineList.Remove(oldest);
            }

            if (!_quarantineList.ContainsKey(party))
            {
                _quarantineList[party] = 0;
                if (Settings.Instance?.TestingMode == true)
                {
                    TaleWorlds.Library.InformationManager.DisplayMessage(
                        new TaleWorlds.Library.InformationMessage($"[Quarantine] Added {party.Name} for repair", TaleWorlds.Library.Colors.Yellow));
                }

                BanditMilitias.Intelligence.AI.Components.MilitiaSmartCache.Instance.Clear();
                return false;
            }

            return _quarantineList[party] >= MAX_REPAIR_ATTEMPTS;
        }

        private void ExecuteNuclearCleanup(MobileParty party)
        {
            if (party == null) return;
            if (Campaign.Current == null || !Campaign.Current.GameStarted) return;

            if (party.MapEvent != null) return;
            if (party.SiegeEvent != null) return;

            if (party.IsMainParty) return;

            if (party.PrisonRoster != null && party.PrisonRoster.Contains(Hero.MainHero.CharacterObject))
            {

                TaleWorlds.CampaignSystem.Actions.EndCaptivityAction.ApplyByPeace(Hero.MainHero);
                if (Settings.Instance?.TestingMode == true)
                {
                    TaleWorlds.Library.InformationManager.DisplayMessage(
                        new TaleWorlds.Library.InformationMessage(
                            $"[Safety Protocol] Player rescued from deleted militia: {party.Name}",
                            TaleWorlds.Library.Colors.Green
                        )
                    );
                }
            }

            if (party.Ai != null)
            {
                party.Ai.SetDoNotMakeNewDecisions(false);
            }

            if (party.ActualClan == null)
            {

                var safeClan = BanditMilitias.Infrastructure.ClanCache.GetLootersClan()
                            ?? BanditMilitias.Infrastructure.ClanCache.GetFallbackBanditClan();
                safeClan ??= Clan.All.FirstOrDefault();
                if (safeClan != null) party.ActualClan = safeClan;
            }

            if (party.ActualClan != null)
            {
                if (party.MemberRoster != null && party.MemberRoster.TotalManCount <= 0)
                {
                    var nearestCaptain = FindNearestFriendlyCaptain(party);
                    if (nearestCaptain != null)
                    {
                        if (Settings.Instance?.TestingMode == true)
                        {
                            TaleWorlds.Library.InformationManager.DisplayMessage(
                                new TaleWorlds.Library.InformationMessage($"[BanditMilitias] Zombie Party {party.Name} merged to {nearestCaptain.Name}.", TaleWorlds.Library.Colors.Cyan)
                            );
                            BanditMilitias.Debug.DebugLogger.Info("CleanupSystem", $"Merges 0-troop zombie party {party.Name} to nearest captain {nearestCaptain.Name}");
                        }
                        try
                        {
                            TaleWorlds.CampaignSystem.Actions.DestroyPartyAction.Apply(nearestCaptain.Party, party);
                        }
                        catch (Exception ex)
                        {
                            BanditMilitias.Debug.DebugLogger.Warning("CleanupSystem", $"DestroyPartyAction merge failed: {ex.Message}");
                            BanditMilitias.Infrastructure.CompatibilityLayer.DestroyParty(party);
                        }
                    }
                    else
                    {
                        BanditMilitias.Infrastructure.CompatibilityLayer.DestroyParty(party);
                    }
                }
                else
                {
                    BanditMilitias.Infrastructure.CompatibilityLayer.DestroyParty(party);
                }
            }
            else
            {

                party.IsActive = false;
                party.IsVisible = false;

                ModuleManager.Instance.UnregisterMilitia(party);

                BanditMilitias.Intelligence.AI.Components.MilitiaSmartCache.Instance.Clear();
            }
        }

        private MobileParty? FindNearestFriendlyCaptain(MobileParty zombieParty)
        {
            if (zombieParty == null || ModuleManager.Instance == null) return null;
            var pos = CompatibilityLayer.GetPartyPosition(zombieParty);
            if (!pos.IsValid) return null;

            MobileParty? closest = null;
            float minDistance = float.MaxValue;

            foreach (var p in ModuleManager.Instance.ActiveMilitias)
            {
                if (p == null || p == zombieParty || !p.IsActive || p.Party == null) continue;
                if (p.PartyComponent is MilitiaPartyComponent comp &&
                   comp.Role >= MilitiaPartyComponent.MilitiaRole.Captain) // Captain, VeteranCaptain ve üzeri
                {
                    var cPos = CompatibilityLayer.GetPartyPosition(p);
                    if (cPos.IsValid)
                    {
                        float dist = pos.Distance(cPos);
                        if (dist < minDistance)
                        {
                            minDistance = dist;
                            closest = p;
                        }
                    }
                }
            }

            return closest;
        }

        // ── Headless Parti Tespiti ─────────────────────────────────────────────
        /// <summary>
        /// Grace period sonrası hâlâ temel verileri eksik olan partileri tespit eder.
        /// ActualClan veya HomeSettlement olmadan yaşayan parti "headless" sayılır.
        /// </summary>
        private bool IsHeadlessParty(MobileParty party)
        {
            if (party == null || !party.IsActive) return false;

            // ActualClan tamamen yok
            if (party.ActualClan == null) return true;

            // HomeSettlement yok veya inactive (Raw check: Inactive olsa bile kalıcı silinmemeli)
            if (party.PartyComponent is MilitiaPartyComponent comp)
            {
                var home = comp.GetHomeSettlementRaw();
                if (home == null) return true;
            }

            return false;
        }

        // ── Headless Parti Transferi ───────────────────────────────────────────
        /// <summary>
        /// Headless partinin askerlerini ve altınını en yakın aktif Captain/VeteranCaptain'a
        /// transfer eder ve partiyi immediate destroy'a işaretler.
        /// Karantina tamamen atlanır.
        /// </summary>
        public void TransferToNearestCaptain(MobileParty headlessParty)
        {
            if (headlessParty == null || !headlessParty.IsActive) return;
            if (headlessParty.MapEvent != null || headlessParty.SiegeEvent != null) return;

            var target = FindNearestFriendlyCaptain(headlessParty);
            if (target == null)
            {
                // Hiç Captain yoksa
                DebugLogger.Info("CleanupSystem",
                    $"[Headless] No captain found for {headlessParty.Name}.");
                return;
            }

            try
            {
                // Asker transferi
                int troopsBefore = target.MemberRoster?.TotalManCount ?? 0;
                int transferTroops = headlessParty.MemberRoster?.TotalManCount ?? 0;

                if (headlessParty.MemberRoster != null && transferTroops > 0 && target.MemberRoster != null)
                {
                    target.MemberRoster.Add(headlessParty.MemberRoster);
                }

                // Mahkum transferi
                if (headlessParty.PrisonRoster != null && headlessParty.PrisonRoster.TotalManCount > 0 && target.PrisonRoster != null)
                {
                    target.PrisonRoster.Add(headlessParty.PrisonRoster);
                }

                // Altın transferi
                if (headlessParty.PartyComponent is MilitiaPartyComponent headlessComp &&
                    target.PartyComponent is MilitiaPartyComponent targetComp)
                {
                    targetComp.Gold += headlessComp.Gold;
                    headlessComp.Gold = 0;
                }

                DebugLogger.Info("CleanupSystem",
                    $"[Headless→Transfer] {headlessParty.Name} ({transferTroops} troops) → {target.Name} (now {target.MemberRoster?.TotalManCount ?? 0})");

                if (Settings.Instance?.TestingMode == true)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[BanditMilitias] Headless party {headlessParty.Name} absorbed by {target.Name} (+{transferTroops} troops)",
                        Colors.Cyan));
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("CleanupSystem",
                    $"[Headless] Transfer failed for {headlessParty.Name}: {ex.Message}");
            }

            // Transfer tamamlandı
        }

        private bool TryMigrate(MobileParty party)
        {
            if (party.PartyComponent is MilitiaPartyComponent comp)
            {
                var newHome = FindNearestActiveHideout(party);
                if (newHome != null)
                {
                    comp.SetHomeSettlement(newHome);

                    BanditMilitias.Intelligence.AI.Components.MilitiaSmartCache.Instance.Clear();

                    if (party.Ai != null)
                    {
                        party.Ai.SetDoNotMakeNewDecisions(false);
                        CompatibilityLayer.SetMoveGoToSettlement(party, newHome);
                    }

                    if (Settings.Instance?.TestingMode == true)
                        BanditMilitias.Debug.DebugLogger.TestLog($"[Cleanup] Party {party.Name} migrated to {newHome.Name}", TaleWorlds.Library.Colors.Green);

                    return true;
                }
            }
            return false;
        }

        private Settlement? FindNearestActiveHideout(MobileParty party)
        {
            Settlement? bestMatch = null;
            float bestDist = float.MaxValue;

            var pPos = CompatibilityLayer.GetPartyPosition(party);
            if (!pPos.IsValid) return null;

            if (ModuleManager.Instance == null) return null;

            foreach (var settlement in ModuleManager.Instance.HideoutCache)
            {
                if (settlement == null) continue;

                if (settlement.IsActive)
                {
                    Vec2 gatePos = new Vec2(settlement.GatePosition.X, settlement.GatePosition.Y);

                    float dist = new Vec2(pPos.X, pPos.Y).DistanceSquared(gatePos);

                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestMatch = settlement;
                    }
                }
            }
            return bestMatch;
        }

        private void LogRepairSuccess(MobileParty party)
        {
            if (Settings.Instance?.TestingMode == true)
            {
                TaleWorlds.Library.InformationManager.DisplayMessage(
                    new TaleWorlds.Library.InformationMessage(
                        $"[Quarantine] Repaired {party.Name}",
                        TaleWorlds.Library.Colors.Green
                    )
                );
            }
        }

        private void LogRepairFailure(MobileParty party)
        {
            if (Settings.Instance?.TestingMode == true)
            {
                TaleWorlds.Library.InformationManager.DisplayMessage(
                    new TaleWorlds.Library.InformationMessage(
                        $"[Quarantine] Failed to repair {party.Name}, destroying",
                        TaleWorlds.Library.Colors.Red
                    )
                );
            }
        }
    }
}
