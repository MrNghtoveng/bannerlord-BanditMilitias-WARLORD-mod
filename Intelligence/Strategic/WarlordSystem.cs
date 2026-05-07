using System;
using System.Collections.Generic;
using System.Linq;
using BanditMilitias.Core.Events;
using BanditMilitias.Core.Neural;
using BanditMilitias.Infrastructure;
using BanditMilitias.Components;
using BanditMilitias.Debug;
using BanditMilitias.Systems.Bounty;
using BanditMilitias.Systems.Economy;
using BanditMilitias.Systems.Fear;
using BanditMilitias.Systems.WarlordLegitimacy;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Systems.Spawning;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Intelligence.Strategic
{
    [BanditMilitias.Core.Components.AutoRegister(Priority = 400, IsCritical = true)]
    public class WarlordSystem : Core.Components.MilitiaModuleBase
    {
        public override string ModuleName => "WarlordSystem";
        private static Lazy<WarlordSystem> _instanceLazy = CreateLazyInstance();
        private static Lazy<WarlordSystem> CreateLazyInstance() => new Lazy<WarlordSystem>(() => new WarlordSystem(), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
        public static WarlordSystem Instance => _instanceLazy.Value;

        private bool _isInitialized;
        public override bool IsEnabled => _isInitialized;

        [SaveableField(1)]
        private Dictionary<string, Warlord> _allWarlords = new Dictionary<string, Warlord>();

        private readonly Dictionary<Settlement, Warlord> _warlordsByHideout = new Dictionary<Settlement, Warlord>();
        private readonly Dictionary<Hero, Warlord> _warlordsByHero = new Dictionary<Hero, Warlord>();
        private readonly Dictionary<Warlord, StrategicState> _warlordStates = new Dictionary<Warlord, StrategicState>();

        private readonly object _initLock = new object();

        private List<Warlord> _cachedWarlordList = new List<Warlord>();
        private bool _warlordListDirty = true;

        private int _totalWarlordsCreated;
        private int _totalWarlordsFallen;
        private int _totalMilitias;
        private float _totalWealth;
        private int _infightingTriggers;
        private float _exposedRisk;

        private const int HERO_POCKET_MONEY = 5000;

        public override void Initialize()
        {
            lock (_initLock)
            {
                if (_isInitialized) return;

                RebuildWarlordMaps();

                BanditMilitias.Core.Events.EventBus.Instance.Subscribe<MilitiaSpawnedEvent>(OnMilitiaSpawned);
                BanditMilitias.Core.Events.EventBus.Instance.Subscribe<MilitiaKilledEvent>(OnMilitiaKilled);
                BanditMilitias.Core.Events.EventBus.Instance.Subscribe<HideoutClearedEvent>(OnHideoutCleared);
                BanditMilitias.Core.Events.EventBus.Instance.Subscribe<ZombiePartyDetectedEvent>(OnZombiePartyDetected);

                _isInitialized = true;
                _warlordListDirty = true;
                DebugLogger.Info("WarlordSystem", "Warlord Management System Initialized.");
            }
        }

        public override void SyncData(IDataStore dataStore)
        {
            lock (_initLock)
            {
                dataStore.SyncData("_allWarlords_v2", ref _allWarlords);
                dataStore.SyncData("_totalWarlordsCreated", ref _totalWarlordsCreated);
                dataStore.SyncData("_totalWarlordsFallen", ref _totalWarlordsFallen);
                dataStore.SyncData("_infightingTriggers", ref _infightingTriggers);

                if (dataStore.IsLoading)
                {
                    _allWarlords ??= new Dictionary<string, Warlord>();
                    RebuildWarlordMaps();
                    _warlordListDirty = true;
                }
            }
        }

        public override void OnSessionStart()
        {
            lock (_initLock)
            {
                ReIdentifyWarlordParties();
            }
        }

        private void ReIdentifyWarlordParties()
        {
            int reIdentified = 0;
            foreach (var warlord in _allWarlords.Values)
            {
                warlord.CommandedMilitias.Clear();
                foreach (var partyId in warlord.CommandedMilitiaIds)
                {
                    var party = Campaign.Current.MobileParties.FirstOrDefault(p => p != null && p.StringId == partyId);
                    if (party != null)
                    {
                        warlord.CommandedMilitias.Add(party);
                        if (party.PartyComponent is MilitiaPartyComponent comp)
                        {
                            comp.AssignedWarlord = warlord;
                            comp.WarlordId = warlord.StringId;
                        }
                        reIdentified++;
                    }
                }
            }

            if (reIdentified > 0)
            {
                DebugLogger.Info("WarlordSystem", $"Re-identified {reIdentified} parties for active warlords.");
            }
        }

        public override void Cleanup()
        {
            lock (_initLock)
            {
                BanditMilitias.Core.Events.EventBus.Instance.Unsubscribe<MilitiaSpawnedEvent>(OnMilitiaSpawned);
                BanditMilitias.Core.Events.EventBus.Instance.Unsubscribe<MilitiaKilledEvent>(OnMilitiaKilled);
                BanditMilitias.Core.Events.EventBus.Instance.Unsubscribe<HideoutClearedEvent>(OnHideoutCleared);
                BanditMilitias.Core.Events.EventBus.Instance.Unsubscribe<ZombiePartyDetectedEvent>(OnZombiePartyDetected);

                _isInitialized = false;
                _allWarlords.Clear();
                _warlordsByHideout.Clear();
                _warlordsByHero.Clear();
                _warlordStates.Clear();
                _cachedWarlordList.Clear();
            }
            _instanceLazy = CreateLazyInstance();
        }

        public Warlord CreateWarlord(Settlement hideout, Hero? hero = null)
        {
            lock (_initLock)
            {
                if (hideout == null || !hideout.IsHideout)
                    throw new ArgumentException("Invalid hideout for Warlord creation.");

                if (_warlordsByHideout.ContainsKey(hideout))
                {
                    return _warlordsByHideout[hideout];
                }

                string id = "BM_Warlord_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                var warlord = new Warlord
                {
                    StringId = id,
                    AssignedHideout = hideout,
                    LinkedHero = hero,
                    Personality = GeneratePersonality(),
                    Motivation = GenerateMotivation(),
                    CreationTime = CampaignTime.Now,
                    Gold = 5000f,
                    IsAlive = true,
                    IsFemale = hero?.IsFemale ?? (MBRandom.RandomInt(100) < (Settings.Instance?.GenderRatio ?? 20))
                };
                warlord.Name = GenerateWarlordName(hideout, warlord.IsFemale);

                _allWarlords[id] = warlord;
                _warlordsByHideout[hideout] = warlord;
                if (hero != null) _warlordsByHero[hero] = warlord;

                _totalWarlordsCreated++;
                _warlordListDirty = true;

                DebugLogger.Info("WarlordSystem", $"New Warlord created: {warlord.Name} at {hideout.Name}");

                return warlord;
            }
        }

        public void RegisterWarlordForTesting(Warlord warlord)
        {
            lock (_initLock)
            {
                if (warlord == null) return;
                _allWarlords[warlord.StringId] = warlord;
                if (warlord.AssignedHideout != null)
                    _warlordsByHideout[warlord.AssignedHideout] = warlord;
                _warlordListDirty = true;
            }
        }

        public Warlord? CreateWarlordFromHero(Hero hero)
        {
            lock (_initLock)
            {
                if (hero == null) return null;

                if (_warlordsByHero.TryGetValue(hero, out var existing))
                    return existing;

                Settlement? hideout = hero.HomeSettlement;
                if (hideout == null || !hideout.IsHideout || _warlordsByHideout.ContainsKey(hideout))
                {
                    hideout = Settlement.All.FirstOrDefault(s => s.IsHideout && s.IsActive && !_warlordsByHideout.ContainsKey(s));
                }

                if (hideout == null)
                {
                    DebugLogger.Warning("WarlordSystem", $"Could not find a free hideout for hero {hero.Name}. Creation aborted.");
                    return null;
                }

                var warlord = CreateWarlord(hideout, hero);
                warlord.OwnedSettlement = hideout;
                return warlord;
            }
        }

        public void RemoveWarlord(Warlord warlord)
        {
            if (warlord == null) return;
            RemoveWarlordById(warlord.StringId);
        }

        public void RemoveWarlordById(string? id)
        {
            if (string.IsNullOrEmpty(id)) return;

            lock (_initLock)
            {
                if (!_allWarlords.TryGetValue(id!, out var warlord)) return;

                warlord.IsAlive = false;
                _ = _allWarlords.Remove(id!);
                
                if (warlord.AssignedHideout != null) _ = _warlordsByHideout.Remove(warlord.AssignedHideout);
                if (warlord.LinkedHero != null) _ = _warlordsByHero.Remove(warlord.LinkedHero);
                _ = _warlordStates.Remove(warlord);

                foreach (var militia in warlord.CommandedMilitias.ToList())
                {
                    warlord.ReleaseMilitia(militia);
                    SynchronizeReleasedMilitiaState(militia);
                }
                warlord.CommandedMilitiaIds.Clear();

                _totalWarlordsFallen++;
                _warlordListDirty = true;

                var evt = BanditMilitias.Core.Events.EventBus.Instance.Get<WarlordFallenEvent>();
                evt.Warlord = warlord;
                evt.Reason = "System Removal";
                try { BanditMilitias.Core.Events.EventBus.Instance.Publish(evt); }
                finally { BanditMilitias.Core.Events.EventBus.Instance.Return(evt); }

                DebugLogger.Info("WarlordSystem", $"Warlord removed: {warlord.Name} (ID: {id})");
            }
        }

        public void RemoveWarlordByHero(Hero hero)
        {
            if (hero == null) return;
            lock (_initLock)
            {
                if (_warlordsByHero.TryGetValue(hero, out var warlord))
                {
                    RemoveWarlord(warlord);
                }
            }
        }

        public void RemoveWarlordByParty(MobileParty party)
        {
            if (party == null) return;
            
            Warlord? warlord = GetWarlordForParty(party);
            if (warlord != null)
            {
                // If it's a leader hero party, the whole Warlord falls.
                if (warlord.LinkedHero != null && party.LeaderHero == warlord.LinkedHero)
                {
                    RemoveWarlord(warlord);
                }
                else
                {
                    // Otherwise just release the party from command
                    warlord.ReleaseMilitia(party);
                    SynchronizeReleasedMilitiaState(party);
                }
            }
        }

        public bool TryPromoteCaptainToWarlord(MobileParty militia)
        {
            var comp = militia.GetMilitiaComponent();
            if (comp == null) return false;

            var result = AscensionEvaluator.Evaluate(militia);
            if (result.IsSuccessful)
            {
                var warlord = CreateWarlord(comp.HomeSettlement!, militia.LeaderHero);
                AssignMilitiaToWarlord(militia, warlord);
                InformationManager.DisplayMessage(new InformationMessage(
                    $"{militia.Name} captain '{warlord.Name}' has taken a new Warlord title!",
                    Colors.Yellow));

                return true;
            }

            return false;
        }

        private void CheckFallbackPromotion()
        {
            var activeWarlords = GetAllWarlords();
            float elapsedDays = ModActivationManager.GetActivationDelayElapsedDays();

            if (WarlordProgressionRules.ShouldRunFamousBanditFallback(activeWarlords.Count, elapsedDays, Settings.Instance?.FamousBanditFallbackDays ?? 60))
            {
                var bestCandidate = AscensionEvaluator.FindBestCandidate();
                if (bestCandidate != null)
                {
                    _ = TryPromoteCaptainToWarlord(bestCandidate);
                    DebugLogger.Info("WarlordSystem", $"[FALLBACK] Promoted {bestCandidate.Name} to Warlord due to system vacancy.");
                }
                else
                {
                    var hideout = ModuleManager.Instance.HideoutCache.FirstOrDefault(h => h.IsHideout && h.IsActive && !_warlordsByHideout.ContainsKey(h));
                    if (hideout != null)
                    {
                        var bestWarlord = MobileParty.All.FirstOrDefault(p => p.IsBandit && p.LeaderHero != null && p.MemberRoster.TotalManCount > 20);
                        if (bestWarlord != null)
                        {
                            _ = CreateWarlord(hideout, bestWarlord.LeaderHero);
                        }

                        if (Settings.Instance?.TestingMode == true)
                        {
                            DebugLogger.Info("WarlordSystem",
                                $"[FALLBACK] Forced Warlord promotion for {bestWarlord?.Name} after {elapsedDays:F0} days.");
                        }
                    }
                }
            }
        }

        public override void OnDailyTick()
        {
            if (!_isInitialized) return;
            if (ModActivationManager.IsGameplayActivationDelayed()) return;

            var militiasByHideout = new Dictionary<Settlement, List<MobileParty>>();
            foreach (var m in ModuleManager.Instance.ActiveMilitias)
            {
                if (m.GetMilitiaComponent() is BanditMilitias.Components.MilitiaPartyComponent c
                    && c.HomeSettlement != null)
                {
                    if (!militiasByHideout.TryGetValue(c.HomeSettlement, out var list))
                    {
                        list = new List<MobileParty>();
                        militiasByHideout[c.HomeSettlement] = list;
                    }
                    list.Add(m);
                }
            }

            try
            {
                foreach (var militia in ModuleManager.Instance.ActiveMilitias)
                {
                    if (militia.GetMilitiaComponent() is BanditMilitias.Components.MilitiaPartyComponent comp)
                    {
                        comp.DaysAlive++;

                        if (comp.Role == BanditMilitias.Components.MilitiaPartyComponent.MilitiaRole.Captain)
                        {
                            float score = CalculateBanditScore(militia, comp);
                            if (score >= 150f)
                            {
                                comp.Role = BanditMilitias.Components.MilitiaPartyComponent.MilitiaRole.VeteranCaptain;
                                InformationManager.DisplayMessage(new InformationMessage(
                                    $"{militia.Name} is now a Veteran Captain!", Colors.Yellow));
                            }
                        }
                    }

                    _ = TryPromoteCaptainToWarlord(militia);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error("WarlordSystem", $"Promotion check failed: {ex.Message}");
            }

            try
            {
                CheckFallbackPromotion();
            }
            catch (Exception ex)
            {
                DebugLogger.Error("WarlordSystem", $"Fallback promotion failed: {ex.Message}");
            }

            if (Settings.Instance?.EnableChaosNerf == true)
            {
                CheckAndApplyChaosNerf();
            }

            UpdateRiskLevel();

            _totalMilitias = 0;
            _totalWealth = 0f;

            foreach (var warlord in GetAllWarlords())
            {
                if (!warlord.IsAlive)
                {
                    RemoveWarlord(warlord);
                    continue;
                }

                try
                {
                    WarlordEconomySystem.Instance.ProcessMerging(warlord);

                    ManageReserves(warlord);

                    ProcessWarlordDay(warlord, militiasByHideout);

                    _totalMilitias += warlord.CommandedMilitias.Count;
                    _totalWealth += warlord.Gold;
                }
                catch (Exception ex)
                {
                    DebugLogger.Error("WarlordSystem", $"Daily tick failed for {warlord.Name}: {ex.Message}");
                }
            }
        }

        private void ProcessWarlordDay(Warlord warlord, Dictionary<Settlement, List<MobileParty>> militiasByHideout)
        {
            if (!_warlordStates.TryGetValue(warlord, out var state))
            {
                state = new StrategicState();
                _warlordStates[warlord] = state;
            }

            RefreshWarlordMilitias(warlord, militiasByHideout);

            var tier = WarlordCareerSystem.Instance.GetTier(warlord.StringId);
            float netOutcome = WarlordEconomySystem.Instance.CalculateDailyOutcome(warlord, tier);

            SyncGoldWithHero(warlord, ref netOutcome);

            warlord.Gold = Math.Max(0, warlord.Gold + netOutcome);

            if (ShouldMakeStrategicDecisions(warlord, state))
            {
                ConsiderStrategicActions(warlord, state);
            }

            StrategicAI_OverpopulationResponse(warlord);

            warlord.DaysActive++;
        }

        private void ManageReserves(Warlord warlord)
        {
            if (warlord.ReserveManpower <= 0)
            {
                warlord.XpMultiplier = 1.0f;
                return;
            }

            float bonus = warlord.ReserveManpower / 5000f;
            warlord.XpMultiplier = Math.Min(2.5f, 1.0f + bonus);

            warlord.ReserveManpower *= 0.98f;
            if (warlord.ReserveManpower < 1f) warlord.ReserveManpower = 0f;
        }

        private void StrategicAI_OverpopulationResponse(Warlord warlord)
        {
            int totalParties = Campaign.Current.MobileParties.Count;
            if (totalParties < 1400) return;

            if (warlord.CurrentOrder is null || warlord.CurrentOrder.Type == CommandType.Patrol)
            {
                var infightingOrder = new StrategicCommand
                {
                    Type = CommandType.Hunt,
                    Priority = 1.0f,
                    Reason = "World Overpopulation (Natural Selection/Infighting)"
                };
                warlord.IssueOrderToMilitias(infightingOrder);
                _infightingTriggers++;

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Info("WarlordAI", $"[INFIGHTING] {warlord.Name} issued aggressive orders due to overpopulation ({totalParties} parties).");
                }
            }
        }

        private void SyncGoldWithHero(Warlord warlord, ref float income)
        {
            if (warlord.LinkedHero == null || !warlord.LinkedHero.IsAlive) return;

            if (warlord.LinkedHero.IsPrisoner)
            {
                if (Settings.Instance?.TestingMode == true)
                    DebugLogger.Info("WarlordSystem", $"[Safety] {warlord.Name} is a prisoner. Siphoning paused.");
                return;
            }

            try
            {
                if (warlord.LinkedHero.Gold > HERO_POCKET_MONEY)
                {
                    int surplus = warlord.LinkedHero.Gold - HERO_POCKET_MONEY;
                    income += surplus;
                    warlord.LinkedHero.ChangeHeroGold(-surplus);
                }

                if (income > 0 && warlord.LinkedHero.Gold < (HERO_POCKET_MONEY / 2))
                {
                    int needed = Math.Min((int)income, HERO_POCKET_MONEY - warlord.LinkedHero.Gold);
                    warlord.LinkedHero.ChangeHeroGold(needed);
                    income -= needed;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("WarlordSystem", $"Gold sync failed for {warlord.Name}: {ex.Message}");
            }
        }

        private bool ShouldMakeStrategicDecisions(Warlord warlord, StrategicState state)
        {
            if (state.RecruitmentPaused) return false;

            float hoursSinceCommand = (float)(CampaignTime.Now - state.LastCommandTime).ToHours;
            if (hoursSinceCommand < 6f) return false;

            if (warlord.Gold < 500) return false;

            if (warlord.CommandedMilitias.Count < 1) return false;

            return true;
        }

        private void ConsiderStrategicActions(Warlord warlord, StrategicState state)
        {
            if (IsBountySystemOperational())
            {
                try
                {
                    int bounty = BountySystem.Instance.GetBounty(warlord.StringId);
                    if (bounty > 10000)
                    {
                        warlord.IssueOrderToMilitias(new StrategicCommand
                        {
                            Type = CommandType.CommandLayLow,
                            Priority = 0.9f,
                            Reason = "High bounty — laying low to survive"
                        });
                        return;
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("WarlordSystem", $"Bounty check failed: {ex.Message}");
                }
            }

            if (warlord.Gold > 20000 && state.ThreatLevel < 0.3f)
            {
                warlord.IssueOrderToMilitias(new StrategicCommand
                {
                    Type = CommandType.CommandBuildRepute,
                    Priority = 0.7f,
                    Reason = "Warlord has surplus gold and low threat — building repute"
                });
            }
            else if (warlord.CommandedMilitias.Count < 2 && warlord.Gold > 1000)
            {
                warlord.IssueOrderToMilitias(new StrategicCommand
                {
                    Type = CommandType.Patrol,
                    Priority = 0.5f,
                    Reason = "Low militia count — patrol mode while waiting for reinforcements"
                });
            }
        }

        private void RefreshWarlordMilitias(Warlord warlord, Dictionary<Settlement, List<MobileParty>> militiasByHideout)
        {
            _ = warlord.CommandedMilitias.RemoveAll(m => m == null || !m.IsActive);

            if (warlord.AssignedHideout == null) return;

            if (!militiasByHideout.TryGetValue(warlord.AssignedHideout, out var potentialMilitias))
                return;

            foreach (var militia in potentialMilitias)
            {
                SynchronizeMilitiaBannerState(militia, warlord);

                if (!warlord.CommandedMilitias.Contains(militia))
                {
                    warlord.CommandMilitia(militia);
                }
            }
        }

        public void AssignMilitiaToWarlord(MobileParty militia, Warlord warlord)
        {
            if (militia == null || warlord == null) return;
            warlord.CommandMilitia(militia);
            SynchronizeMilitiaBannerState(militia, warlord);
        }

        private static void SynchronizeMilitiaBannerState(MobileParty militia, Warlord warlord)
        {
            var comp = militia.GetMilitiaComponent();
            if (comp == null)
                return;

            comp.WarlordId = warlord.StringId;
            comp.AssignedWarlord = warlord;

            var legitSys = WarlordLegitimacySystem.Instance;
            if (legitSys != null)
                comp.SetBannerPrestigeLevel(legitSys.GetLevel(warlord.StringId));
        }

        private static void SynchronizeReleasedMilitiaState(MobileParty militia)
        {
            var comp = militia.GetMilitiaComponent();
            if (comp == null)
                return;

            comp.WarlordId = null;
            comp.AssignedWarlord = null;

            comp.SetBannerPrestigeLevel(LegitimacyLevel.Outlaw);
        }

        private void OnMilitiaSpawned(MilitiaSpawnedEvent evt)
        {
            if (evt?.Party == null || evt.HomeHideout == null) return;

            var warlord = GetWarlordForHideout(evt.HomeHideout);
            if (warlord != null)
            {
                AssignMilitiaToWarlord(evt.Party, warlord);
                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Info("WarlordSystem", $"[Immediate Command] {warlord.Name} took command of new militia at {evt.HomeHideout.Name}");
                }
            }
        }

        private void OnZombiePartyDetected(ZombiePartyDetectedEvent evt)
        {
            if (evt?.Party == null || evt.HomeSettlement == null) return;
            try
            {
                var comp = evt.Party.GetMilitiaComponent();
                if (comp == null) return;
                if (comp.CurrentOrder != null) return;

                var warlord = GetWarlordForParty(evt.Party);
                var homeVec2 = Infrastructure.CompatibilityLayer.ToVec2(evt.HomeSettlement.GatePosition);

                var rescueCommand = new StrategicCommand
                {
                    Type = CommandType.Patrol,
                    TargetLocation = homeVec2,
                    Priority = 0.5f,
                    Reason = "ZombieRescue: returning to home base via WarlordSystem"
                };

                if (warlord != null)
                    warlord.IssueOrderToMilitias(rescueCommand);
                else
                {
                    comp.CurrentOrder = rescueCommand;
                    comp.OrderTimestamp = TaleWorlds.CampaignSystem.CampaignTime.Now;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("WarlordSystem", $"OnZombiePartyDetected failed: {ex.Message}");
                // Fallback: set order directly on the component without going through WarlordSystem.
                try
                {
                    var comp = evt.Party?.GetMilitiaComponent();
                    if (comp != null && comp.HomeSettlement != null)
                    {
                        comp.CurrentOrder = new StrategicCommand
                        {
                            Type = CommandType.Patrol,
                            TargetLocation = Infrastructure.CompatibilityLayer.ToVec2(comp.HomeSettlement.GatePosition),
                            Priority = 0.1f,
                            Reason = "ZombieRescue fallback"
                        };
                        comp.OrderTimestamp = TaleWorlds.CampaignSystem.CampaignTime.Now;
                    }
                }
                catch { }
            }
        }

        private void OnMilitiaKilled(MilitiaKilledEvent evt)
        {
            if (evt.HomeHideout == null) return;

            var warlord = GetWarlordForHideout(evt.HomeHideout);
            if (warlord != null && evt.Victim != null)
            {
                warlord.ReleaseMilitia(evt.Victim);
            }
        }

        private void OnHideoutCleared(HideoutClearedEvent evt)
        {
            if (evt.Hideout == null) return;

            var warlord = GetWarlordForHideout(evt.Hideout);
            if (warlord != null)
            {
                RemoveWarlord(warlord);
            }
        }

        public void OnHeroPrisonerTaken(Hero prisoner, PartyBase capturer)
        {
            if (!_warlordsByHero.TryGetValue(prisoner, out var warlord)) return;

            if (MBRandom.RandomFloat < 0.3f)
            {
                RemoveWarlord(warlord);

                if (Settings.Instance?.RemovePrisonerMessages == false && Settings.Instance?.TestingMode == true)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[Warlord] {warlord.Name} was executed in captivity",
                        Colors.Red));
                }
            }
        }

        public IReadOnlyList<Warlord> GetAllWarlords()
        {
            if (_warlordListDirty)
            {
                _cachedWarlordList = _allWarlords.Values.Where(w => w.IsAlive).ToList();
                _warlordListDirty = false;
            }
            return _cachedWarlordList;
        }

        public Warlord? GetWarlord(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_initLock)
            {
                return _allWarlords.TryGetValue(id!, out var warlord) ? warlord : null;
            }
        }

        public Warlord? GetWarlordById(string? id) => GetWarlord(id);

        public void TryAssignCaptainToParty(MobileParty party)
        {
            var comp = party.GetMilitiaComponent();
            if (comp == null) return;
            if (comp.AssignedWarlord != null) return;
            if (comp.HomeSettlement != null)
            {
                var existing = GetWarlordForHideout(comp.HomeSettlement);
                if (existing != null)
                {
                    AssignMilitiaToWarlord(party, existing);
                    return;
                }
            }
            if (party.LeaderHero != null)
                TryPromoteCaptainToWarlord(party);
        }

        public void AddFear(string warlordId, float amount)
        {
            var w = GetWarlord(warlordId);
            if (w == null || !w.IsAlive)
                return;

            w.FearScore = Math.Min(100f, w.FearScore + amount);
        }

        public Warlord? GetWarlordForHideout(Settlement hideout)
        {
            if (hideout == null) return null;

            lock (_initLock)
            {
                _ = _warlordsByHideout.TryGetValue(hideout, out var warlord);
                return warlord?.IsAlive == true ? warlord : null;
            }
        }

        public Warlord? GetWarlordForHero(Hero hero)
        {
            if (hero == null) return null;

            lock (_initLock)
            {
                _ = _warlordsByHero.TryGetValue(hero, out var warlord);
                return warlord?.IsAlive == true ? warlord : null;
            }
        }

        public StrategicState? GetStrategicState(Warlord warlord)
        {
            return _warlordStates.TryGetValue(warlord, out var state) ? state : null;
        }

        private static bool IsFearSystemOperational()
        {
            var module = ModuleManager.Instance.GetModule<FearSystem>();
            return module?.IsEnabled == true;
        }

        private static bool IsLegitimacySystemOperational()
        {
            var module = ModuleManager.Instance.GetModule<WarlordLegitimacySystem>();
            return module?.IsEnabled == true;
        }

        private static bool IsBountySystemOperational()
        {
            var module = ModuleManager.Instance.GetModule<BountySystem>();
            return module?.IsEnabled == true;
        }

        private void UpdateRiskLevel()
        {
            _exposedRisk = MathF.Max(0f, _exposedRisk - 0.05f);
        }

        private void RebuildWarlordMaps()
        {
            _warlordsByHideout.Clear();
            _warlordsByHero.Clear();

            foreach (var warlord in _allWarlords.Values)
            {
                if (!warlord.IsAlive) continue;

                if (warlord.AssignedHideout != null)
                {
                    _warlordsByHideout[warlord.AssignedHideout] = warlord;
                }

                if (warlord.LinkedHero != null)
                {
                    _warlordsByHero[warlord.LinkedHero] = warlord;
                }
            }
        }

        private string GenerateWarlordName(Settlement hideout, bool isFemale)
        {
            Clan banditClan = hideout.OwnerClan;
            var nameObj = BanditMilitias.Systems.Spawning.MilitiaNameGenerator.GenerateName(hideout, banditClan);

            string baseName = nameObj.ToString();

            string[] prefixes;
            if (isFemale)
            {
                prefixes = new[] { "Warlordess", "Chieftainess", "Queen", "Captain", "Lady", "Mistress" };
            }
            else
            {
                prefixes = new[] { "Warlord", "Chieftain", "Boss", "Captain", "Lord", "Master" };
            }
            string prefix = prefixes[MBRandom.RandomInt(prefixes.Length)];

            return $"{prefix} of {baseName}";
        }

        private PersonalityType GeneratePersonality()
        {
            var values = Enum.GetValues(typeof(PersonalityType));
            object? value = values.GetValue(MBRandom.RandomInt(values.Length));
            return (PersonalityType)(value ?? PersonalityType.Cunning);
        }

        private MotivationType GenerateMotivation()
        {
            var values = Enum.GetValues(typeof(MotivationType));
            object? value = values.GetValue(MBRandom.RandomInt(values.Length));
            return (MotivationType)(value ?? MotivationType.Power);
        }

        public float CalculateBanditScore(MobileParty militia, BanditMilitias.Components.MilitiaPartyComponent comp)
        {
            float renownPart = comp.Renown * 8f;

            float goldPart = (militia.LeaderHero != null ? (float)militia.LeaderHero.Gold / 50f : 0f);

            float troopPart = militia.MemberRoster.TotalManCount * 4f;

            float battlePart = comp.BattlesWon * 15f;

            return renownPart + goldPart + troopPart + battlePart;
        }

        private void CheckAndApplyChaosNerf()
        {
            if (Settings.Instance == null || !Settings.Instance.EnableChaosNerf) return;

            int maxAllowed = Settings.Instance.MaxWarlordCount;
            var activeWarlords = GetAllWarlords();

            if (activeWarlords.Count <= maxAllowed) return;

            var weakest = activeWarlords.OrderBy(w => w.FearScore).FirstOrDefault();
            if (weakest != null)
            {
                DebugLogger.Info("WarlordSystem",
                    $"ChaosNerf: {activeWarlords.Count} warlords present, limit {maxAllowed}. " +
                    $"'{weakest.Name}' retired (FearScore: {weakest.FearScore:F1}).");
                RemoveWarlord(weakest);
            }
        }

        public Warlord? GetWarlordForParty(MobileParty party)
        {
            if (party?.PartyComponent is not BanditMilitias.Components.MilitiaPartyComponent comp) return null;
            return comp.AssignedWarlord;
        }
    }

    [System.Serializable]
    [System.Obsolete("WarlordData is not currently used. Active system: Warlord class.")]
    public class WarlordData
    {
        [TaleWorlds.SaveSystem.SaveableProperty(1)] public string Id { get; set; } = "";
        [TaleWorlds.SaveSystem.SaveableProperty(2)] public string Name { get; set; } = "";
        [TaleWorlds.SaveSystem.SaveableProperty(3)] public TaleWorlds.CampaignSystem.Settlements.Settlement? Hideout { get; set; }
        [TaleWorlds.SaveSystem.SaveableProperty(4)] public TaleWorlds.CampaignSystem.Hero? LinkedHero { get; set; }
        [TaleWorlds.SaveSystem.SaveableProperty(5)] public PersonalityType Personality { get; set; }
        [TaleWorlds.SaveSystem.SaveableProperty(6)] public MotivationType Motivation { get; set; }
        [TaleWorlds.SaveSystem.SaveableProperty(7)] public float Gold { get; set; } = 3000f;
        [TaleWorlds.SaveSystem.SaveableProperty(8)] public int DaysActive { get; set; }
        [TaleWorlds.SaveSystem.SaveableProperty(9)] public TaleWorlds.CampaignSystem.CampaignTime CreatedAt { get; set; }
        [TaleWorlds.SaveSystem.SaveableProperty(10)] public bool IsAlive { get; set; } = true;
        [TaleWorlds.SaveSystem.SaveableProperty(11)] public int Tier { get; set; } = 0;
        [TaleWorlds.SaveSystem.SaveableProperty(12)] public int BattlesWon { get; set; }
        [TaleWorlds.SaveSystem.SaveableProperty(13)] public float TotalBounty { get; set; }
        [TaleWorlds.SaveSystem.SaveableProperty(14)] public float FearScore { get; set; }

        public System.Collections.Generic.List<TaleWorlds.CampaignSystem.Party.MobileParty> Militias { get; } = new();

        public string FullTitle => Tier switch
        {
            0 => $"{Name} (Outlaw)",
            1 => $"{Name} (Rebel)",
            2 => $"{Name} (Famous Bandit)",
            3 => $"{Name} (Warlord)",
            4 => $"{Name} (Sovereign)",
            5 => $"{Name} (Conqueror)",
            _ => Name
        };
    }

    public static class WarlordProgressionRules
    {
        public static bool CanPromoteCaptainToWarlord(
            bool isMilitiaActive,
            bool hasPartyComponent,
            bool alreadyPromoted,
            bool hasHomeSettlement,
            bool hasExistingWarlordForHideout,
            bool hasLeaderHero,
            bool isLeaderAlive,
            int daysAlive,
            int battlesWon,
            int troopCount,
            int minDaysAlive,
            int minBattlesWon,
            int minTroops)
        {
            if (!isMilitiaActive || !hasPartyComponent || alreadyPromoted)
            {
                return false;
            }

            if (!hasHomeSettlement || hasExistingWarlordForHideout)
            {
                return false;
            }

            if (!hasLeaderHero || !isLeaderAlive)
            {
                return false;
            }

            return daysAlive >= Math.Max(0, minDaysAlive)
                && battlesWon >= Math.Max(0, minBattlesWon)
                && troopCount >= Math.Max(1, minTroops);
        }

        public static bool ShouldRunFamousBanditFallback(int activeWarlordCount, float elapsedDays, int fallbackDays)
        {
            return activeWarlordCount <= 0 && elapsedDays >= Math.Max(0, fallbackDays);
        }

        public static bool ShouldRunWarlordFallback(int activeWarlordCount, bool hasWarlord, float elapsedDays, int fallbackDays)
        {
            bool shouldUseWarlordFallbackWindow = activeWarlordCount != 1 &&
                                                  ((fallbackDays <= 60 && activeWarlordCount == 0) ||
                                                   (fallbackDays >= 150 && activeWarlordCount >= 2) ||
                                                   (fallbackDays > 60 && fallbackDays < 150 && activeWarlordCount == 0));
            bool timeCheck = !hasWarlord && elapsedDays >= Math.Max(0, (float)fallbackDays);

            bool countCheck = activeWarlordCount == 0
                           || activeWarlordCount >= 2;

            return timeCheck && shouldUseWarlordFallbackWindow;
        }

        public static int ComputeFallbackCandidateScore(int daysAlive, int battlesWon, int troopCount)
        {
            long safeDays = Math.Max(0, daysAlive);
            long safeBattles = Math.Max(0, battlesWon);
            long safeTroops = Math.Max(0, troopCount);

            long score = (safeDays * 2L) + (safeBattles * 10L) + safeTroops;
            return score > int.MaxValue ? int.MaxValue : (int)score;
        }

        public static List<TCandidate> SelectTopFallbackCandidates<TCandidate>(
            IEnumerable<TCandidate> candidates,
            int maxCount,
            Func<TCandidate, string> idSelector,
            Func<TCandidate, int> daysSelector,
            Func<TCandidate, int> battlesSelector,
            Func<TCandidate, int> troopSelector)
        {
            if (candidates == null || maxCount <= 0)
            {
                return new List<TCandidate>();
            }

            if (idSelector == null) throw new ArgumentNullException(nameof(idSelector));
            if (daysSelector == null) throw new ArgumentNullException(nameof(daysSelector));
            if (battlesSelector == null) throw new ArgumentNullException(nameof(battlesSelector));
            if (troopSelector == null) throw new ArgumentNullException(nameof(troopSelector));

            return candidates
                .OrderByDescending(c => ComputeFallbackCandidateScore(daysSelector(c), battlesSelector(c), troopSelector(c)))
                .ThenByDescending(daysSelector)
                .ThenByDescending(battlesSelector)
                .ThenByDescending(troopSelector)
                .ThenBy(c => idSelector(c) ?? string.Empty, StringComparer.Ordinal)
                .Take(maxCount)
                .ToList();
        }
    }

    public class CoordinationPlanner
    {
        private readonly List<CoordinatedStrike> _active = new();
        private readonly List<CoordinatedStrike> _ready = new();

        public void AddCoordination(CoordinatedStrike strike)
        {
            if (strike != null && strike.IsValid())
            {
                _active.Add(strike);
            }
        }

        public void Update()
        {
            _ready.Clear();

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var strike = _active[i];

                if (!strike.IsValid())
                {
                    _active.RemoveAt(i);
                    continue;
                }

                if (strike.IsReadyToExecute())
                {
                    _ready.Add(strike);
                    _active.RemoveAt(i);
                }
                else if ((CampaignTime.Now - strike.ScheduledTime).ToHours > 24f)
                {
                    _active.RemoveAt(i);
                }
            }
        }

        public List<CoordinatedStrike> GetReadyCoordinations() => _ready;

        public void NotifyCoalitionFormed(MobileParty coalition)
        {
            foreach (var strike in _active)
            {
                if (strike.ParticipatingMilitias.Contains(coalition))
                {
                    strike.Priority *= 1.2f;
                }
            }
        }

        public void Clear()
        {
            _active.Clear();
            _ready.Clear();
        }
    }

    public class CoordinatedStrike
    {
        public Vec2 RallyPoint { get; set; }
        public CampaignTime ScheduledTime { get; set; }
        public List<MobileParty> ParticipatingMilitias { get; set; } = new();
        public CommandType Mission { get; set; }
        public Vec2 TargetLocation { get; set; }
        public float Priority { get; set; } = 1.0f;

        public bool IsValid()
        {
            return ParticipatingMilitias != null &&
                   ParticipatingMilitias.Any(p => p != null && p.IsActive);
        }

        public bool IsReadyToExecute()
        {
            if (CampaignTime.Now < ScheduledTime) return false;
            if (!IsValid()) return false;

            int ready = ParticipatingMilitias.Count(p =>
                p != null &&
                p.IsActive &&
                CompatibilityLayer.GetPartyPosition(p).Distance(RallyPoint) < 20f);

            return ready >= ParticipatingMilitias.Count * 0.4f;
        }
    }

    public class Warlord
    {
        [SaveableProperty(1)]
        public string StringId { get; set; } = "";

        [SaveableProperty(2)]
        public string Name { get; set; } = "";

        [SaveableProperty(3)]
        public Settlement? AssignedHideout { get; set; }

        [SaveableProperty(4)]
        public Hero? LinkedHero { get; set; }

        [SaveableProperty(5)]
        public PersonalityType Personality { get; set; }

        [SaveableProperty(6)]
        public MotivationType Motivation { get; set; }

        [SaveableProperty(7)]
        public float Gold { get; set; }

        [SaveableProperty(8)]
        public int DaysActive { get; set; }

        [SaveableProperty(9)]
        public CampaignTime CreationTime { get; set; }

        [SaveableProperty(10)]
        public bool IsAlive { get; set; } = true;

        [SaveableProperty(11)]
        public string Title { get; set; } = "";

        [SaveableProperty(12)]
        public BackstoryType Backstory { get; set; }

        [SaveableProperty(13)]
        public int Kills { get; set; }

        [SaveableProperty(14)]
        public float TotalBounty { get; set; }

        [SaveableProperty(15)]
        public float FearScore { get; set; }

        [SaveableProperty(20)]
        public string? VassalOf { get; set; } = null;

        [SaveableProperty(21)]
        public bool IsLordHunting { get; set; } = false;

        [SaveableProperty(22)]
        public Settlement? OwnedSettlement { get; set; }

        [SaveableProperty(23)]
        public List<Settlement> InfluencedVillages { get; set; } = new List<Settlement>();

        [SaveableProperty(24)]
        public bool IsRoaming { get; set; } = true;

        [SaveableProperty(25)]
        public bool IsFemale { get; set; } = false;

        [SaveableProperty(30)]
        public float WageDiscount { get; set; } = 0f;

        [SaveableProperty(31)]
        public float XpMultiplier { get; set; } = 1f;

        [SaveableProperty(32)]
        public float SpeedBonus { get; set; } = 0f;

        [SaveableProperty(33)]
        public float ReserveManpower { get; set; } = 0f;

        public string FullName => $"{Name} {Title}";

        [SaveableProperty(34)]
        public List<string> CommandedMilitiaIds { get; set; } = new();

        [SaveableProperty(35)]
        public string? WatcherPartyId { get; set; }

        [SaveableProperty(36)]
        public List<string> SharedIntelIds { get; set; } = new();

        public List<MobileParty> SharedIntel { get; set; } = new();

        public List<MobileParty> CommandedMilitias { get; set; } = new();
        public int BattlesWon => CommandedMilitias
            .Where(p => p?.PartyComponent is MilitiaPartyComponent)
            .Sum(p => ((MilitiaPartyComponent)p.PartyComponent).BattlesWon);

        public StrategicCommand? CurrentOrder { get; set; }

        public void CommandMilitia(MobileParty militia)
        {
            if (militia != null && !CommandedMilitias.Contains(militia))
            {
                CommandedMilitias.Add(militia);
                if (!CommandedMilitiaIds.Contains(militia.StringId))
                    CommandedMilitiaIds.Add(militia.StringId);

                if (militia.PartyComponent is MilitiaPartyComponent comp)
                {
                    comp.WarlordId = StringId;
                    comp.AssignedWarlord = this;
                }

                EnsureWatcherSuccession();
            }
        }

        public void EnsureWatcherSuccession()
        {
            if (CommandedMilitias.Count == 0)
            {
                WatcherPartyId = null;
                return;
            }

            var currentWatcher = CommandedMilitias.FirstOrDefault(p => p.StringId == WatcherPartyId && p.IsActive);
            if (currentWatcher != null) return;

            var bestScout = CommandedMilitias
                .Where(p => p.IsActive)
                .OrderBy(p => p.MemberRoster.TotalManCount)
                .ThenByDescending(p => p.Speed)
                .FirstOrDefault();

            if (bestScout != null)
            {
                WatcherPartyId = bestScout.StringId;
                if (bestScout.PartyComponent is MilitiaPartyComponent comp)
                {
                    comp.IsWatcher = true;
                    DebugLogger.Debug("Watcher", $"New watcher assigned for {Name}: {bestScout.Name}");
                }
            }
        }

        public void UpdateSharedIntelligence(List<MobileParty> detectedParties)
        {
            if (detectedParties == null) return;

            SharedIntel.Clear();
            SharedIntelIds.Clear();
            foreach (var p in detectedParties)
            {
                if (p != null && p.IsActive && !CommandedMilitias.Contains(p))
                {
                    SharedIntel.Add(p);
                    if (p.StringId != null) SharedIntelIds.Add(p.StringId);

                    if (SharedIntel.Count >= 15) break;
                }
            }
        }

        public void ReleaseMilitia(MobileParty militia)
        {
            _ = CommandedMilitias.Remove(militia);
            if (militia != null)
            {
                _ = CommandedMilitiaIds.Remove(militia.StringId);
                if (militia.PartyComponent is MilitiaPartyComponent comp)
                    comp.AssignedWarlord = null;
            }
        }

        public void IssueOrderToMilitias(StrategicCommand command)
        {
            CurrentOrder = command;

            if (BanditMilitias.Core.Events.EventBus.Instance != null)
            {
                var evt = BanditMilitias.Core.Events.EventBus.Instance.Get<StrategicCommandEvent>();
                if (evt != null)
                {
                    evt.Command = command;
                    evt.IssuedBy = "Warlord:" + Name;
                    evt.Timestamp = CampaignTime.Now;
                    evt.TargetRegion = AssignedHideout;
                    NeuralEventRouter.Instance.Publish(evt);
                    BanditMilitias.Core.Events.EventBus.Instance.Return(evt);
                }
            }

            if (BanditMilitias.Settings.Instance?.TestingMode == true)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Warlord {Name}] Dispatched neural pulse: {command.Type} to {CommandedMilitias.Count} units.",
                    Colors.Cyan));
            }
        }
    }

    public enum BackstoryType { BetrayedNoble, FailedMercenary, ExiledLeader, VengefulSurvivor, AmbitionDriven }
    public enum MotivationType { Revenge, Greed, Survival, Power, Freedom }
    public enum PersonalityType { Aggressive, Cunning, Cautious, Vengeful }

    public enum BrainState { Dormant, Active, Degraded }
    public enum DistressLevel { None, Low, Medium, High, Critical }
    public enum PlayStyle { Passive, Balanced, Aggressive, Strategic, Defensive, Economic }
    public enum StrategicPosture { Defensive, Offensive, Opportunistic, Normal }
    public enum CommandCompletionStatus { Success, Failure, Cancelled, Expired }

    public enum CommandType
    {
        Patrol,
        Ambush,
        Retreat,
        Hunt,
        Harass,
        AvoidCrowd,
        Defend,
        Raid,
        Engage,
        CommandRaidVillage,
        CommandLayLow,
        CommandExtort,
        CommandBuildRepute,
        Scavenge,
        Retrieve
    }

    public enum AIDecisionType
    {
        Patrol,
        Raid,
        Engage,
        Flee,
        Defend,
        Retreat,
        Ambush
    }

    public enum AIController
    {
        VanillaAI,
        CustomAI,
        StrategicOrder,
        BanditBrain
    }

    public class StrategicCommand
    {
        [SaveableField(1)]
        public CommandType Type;

        [SaveableField(2)]
        public Vec2 TargetLocation;

        [SaveableField(3)]
        public MobileParty? TargetParty;

        [SaveableField(4)]
        public float Priority;

        [SaveableField(5)]
        public string Reason = "";

        public void Reset()
        {
            Type = CommandType.Patrol;
            TargetLocation = Vec2.Invalid;
            TargetParty = null;
            Priority = 0.5f;
            Reason = "";
        }
    }

    public class ThreatAssessment
    {
        [SaveableProperty(1)]
        public Settlement? Settlement { get; set; }
        [SaveableProperty(2)]
        public float ProximityThreat { get; set; }
        [SaveableProperty(3)]
        public float StrengthThreat { get; set; }
        [SaveableProperty(4)]
        public float AggressionThreat { get; set; }
        [SaveableProperty(5)]
        public float OverallThreat { get; set; }
    }

    public struct CombatOutcome
    {
        [SaveableField(1)]
        public bool PlayerWon;
        [SaveableField(2)]
        public float PlayerStrength;
        [SaveableField(3)]
        public float MilitiaStrength;
        [SaveableField(4)]
        public CampaignTime Timestamp;
        [SaveableField(5)]
        public float MilitiaRemainingStrength;
        [SaveableField(6)]
        public float PlayerRemainingStrength;
    }

    public class MilitiaPerformance
    {
        [SaveableField(1)]
        public int Kills;
        [SaveableField(2)]
        public int Deaths;
        [SaveableField(3)]
        public CampaignTime LastDeathTime;

        public float SurvivalRate => Deaths > 0 ? Kills / (float)Deaths : 1.0f;
    }

    [Serializable]
    public class PlayerProfile
    {
        [SaveableField(1)]
        public int TotalKills;
        [SaveableField(2)]
        public int HideoutsDestroyed;
        [SaveableField(3)]
        public int CurrentStrength;
        [SaveableField(4)]
        public int ClanTier;
        [SaveableField(5)]
        public int DaysPlayed;
        [SaveableField(6)]
        public CampaignTime LastKillTime;
        [SaveableField(7)]
        public PlayStyle PlayStyle;
        [SaveableField(8)]
        public float CombatEffectiveness;

        public float GetCurrentThreat()
        {
            float killThreat = Math.Min(1f, TotalKills / 100f);
            float strengthThreat = Math.Min(1f, CurrentStrength / 200f);
            float experienceThreat = Math.Min(1f, ClanTier / 6f);

            return (killThreat * 0.5f) + (strengthThreat * 0.3f) + (experienceThreat * 0.2f);
        }

        public void UpdateThreatCache(float newLevel)
        {
            CombatEffectiveness = Math.Max(0f, Math.Min(1f, newLevel));
        }
    }

    public class RunningAverage
    {
        [SaveableField(1)]
        private float _savedValue;

        [SaveableField(2)]
        private int _windowSize = 20;

        private CircularBuffer<float> _values;
        public float Value { get => _savedValue; private set => _savedValue = value; }

        public RunningAverage()
        {
            _windowSize = 20;
            _values = new CircularBuffer<float>(_windowSize);
        }

        public RunningAverage(int windowSize)
        {
            _windowSize = windowSize > 0 ? windowSize : 20;
            _values = new CircularBuffer<float>(_windowSize);
        }

        public void Add(float value)
        {
            _values ??= new CircularBuffer<float>(_windowSize > 0 ? _windowSize : 20);
            _values.Add(value);
            Value = _values.Count > 0 ? _values.Average() : 0f;
            _savedValue = Value;
        }
    }

    public class CommandFeedback
    {
        [SaveableField(1)]
        public int TotalCommands;
        [SaveableField(2)]
        public int SuccessCount;
        [SaveableField(3)]
        public int FailureCount;
        [SaveableField(4)]
        public float SuccessRate;
        [SaveableField(5)]
        public float StrategyConfidence = 0.5f;
    }

    public class StrategicAssessment
    {
        [SaveableField(1)]
        public float ThreatLevel;
        [SaveableField(2)]
        public StrategicPosture RecommendedPosture;
        [SaveableField(3)]
        public float PlayerThreat;
        [SaveableField(4)]
        public float Confidence;
        [SaveableField(5)]
        public CampaignTime AssessmentTime;
    }

    public class AIDecision
    {
        public AIDecisionType Action { get; set; }
        public object? Target { get; set; }
        public Vec2 TargetPosition { get; set; }
        public float Score { get; set; }
        public CampaignTime Timestamp { get; set; }
        public float Duration { get; set; }
    }

    public class AIAuthority
    {
        public AIController Controller { get; set; }
        public CampaignTime GrantedAt { get; set; }
    }

    public class CommandExecution
    {
        public StrategicCommand? Command { get; set; }
        public CampaignTime StartTime { get; set; }
        public float Progress { get; set; }
    }

    public class BattleSite
    {
        [SaveableProperty(1)]
        public Vec2 Position { get; set; }
        [SaveableProperty(2)]
        public CampaignTime Time { get; set; }
        [SaveableProperty(3)]
        public float Intensity { get; set; }
    }

    public class StrategicState
    {
        [SaveableField(1)]
        public StrategicPosture CurrentPosture = StrategicPosture.Normal;
        [SaveableField(2)]
        public StrategicPosture RecommendedPosture = StrategicPosture.Normal;
        [SaveableField(3)]
        public float ThreatLevel;
        [SaveableField(4)]
        public float PlayerThreatLevel;

        [SaveableField(5)]
        public CommandType ActiveCommandType;
        [SaveableField(6)]
        public CampaignTime LastCommandTime;

        [SaveableField(7)]
        public float MilitaryBudgetMultiplier = 1.0f;
        [SaveableField(8)]
        public bool RecruitmentPaused;

        [SaveableField(9)]
        public int TotalCommands;
        [SaveableField(10)]
        public int SuccessfulCommands;
        [SaveableField(11)]
        public int FailedCommands;
        [SaveableField(12)]
        public float CommandSuccessRate;
    }

    public class StrategicContext
    {
        public float OwnCombatPower { get; set; }
        public float EnemyCombatPower { get; set; }

        public float WarlordGold { get; set; }
        public float ThreatLevel { get; set; }

        public float AverageRegionFear { get; set; } = 0f;
        public LegitimacyLevel WarlordLevel { get; set; } = LegitimacyLevel.Outlaw;

        public int WarlordBounty { get; set; } = 0;
        public bool HasActiveHunter { get; set; } = false;
    }

    public interface IPersonalityStrategy
    {
        StrategicCommand DetermineResponse(Warlord warlord, LegitimacyLevel level, ThreatAssessment threat, PlayerProfile player);
        float GetActionAffinity(CommandType action);
    }

    public class AggressiveStrategy : IPersonalityStrategy
    {
        public StrategicCommand DetermineResponse(Warlord warlord, LegitimacyLevel level, ThreatAssessment threat, PlayerProfile player)
        {
            if (threat.OverallThreat > 0.85f)
                return StrategyHelper.Make(warlord, CommandType.Defend, 0.75f, "Defend and counter-attack!");

            if (level < LegitimacyLevel.Warlord && threat.StrengthThreat > 0.80f)
                return StrategyHelper.Make(warlord, CommandType.Retreat, 1.0f, "They are too strong... That's it for today.");

            if (threat.OverallThreat > 0.55f)
                return StrategyHelper.Make(warlord, CommandType.Ambush, 0.82f + threat.OverallThreat * 0.10f, "Set an ambush – be opportunistic!");

            if (threat.OverallThreat > 0.25f)
                return StrategyHelper.Make(warlord, CommandType.Hunt, 0.85f, "HUNT!");

            return StrategyHelper.Make(warlord, CommandType.CommandRaidVillage, 0.90f, "Weak? RAZE!");
        }

        public float GetActionAffinity(CommandType action) => action switch
        {
            CommandType.Hunt => 1.60f,
            CommandType.Ambush => 1.40f,
            CommandType.CommandRaidVillage => 1.35f,
            CommandType.Harass => 1.20f,
            CommandType.Engage => 1.20f,
            CommandType.Defend => 0.65f,
            CommandType.Retreat => 0.20f,
            CommandType.CommandLayLow => 0.10f,
            _ => 1.0f
        };
    }

    public class CautiousStrategy : IPersonalityStrategy
    {
        public StrategicCommand DetermineResponse(Warlord warlord, LegitimacyLevel level, ThreatAssessment threat, PlayerProfile player)
        {
            if (threat.OverallThreat > 0.65f)
                return StrategyHelper.Make(warlord, CommandType.CommandLayLow, 0.95f, "Too dangerous! Hide!");

            if (threat.OverallThreat > 0.40f)
                return StrategyHelper.Make(warlord, CommandType.Defend, 0.75f, "Defend, stay safe.");

            if (player.CurrentStrength < 30)
                return StrategyHelper.Make(warlord, CommandType.Harass, 0.58f, "Player is weak – harass, win.");

            return StrategyHelper.Make(warlord, CommandType.Patrol, 0.45f, "Patrol – stay visible but careful.");
        }

        public float GetActionAffinity(CommandType action) => action switch
        {
            CommandType.CommandLayLow => 1.80f,
            CommandType.Defend => 1.50f,
            CommandType.Patrol => 1.30f,
            CommandType.Retreat => 1.20f,
            CommandType.Harass => 0.80f,
            CommandType.Hunt => 0.40f,
            CommandType.Ambush => 0.50f,
            _ => 1.0f
        };
    }

    public class CunningStrategy : IPersonalityStrategy
    {
        public StrategicCommand DetermineResponse(Warlord warlord, LegitimacyLevel level, ThreatAssessment threat, PlayerProfile player)
        {
            if (threat.OverallThreat > 0.70f)
                return StrategyHelper.Make(warlord, CommandType.CommandLayLow, 0.85f,
                    "Too dangerous, hide!");

            if (level < LegitimacyLevel.Warlord && threat.StrengthThreat > 0.6f)
                return StrategyHelper.Make(warlord, CommandType.CommandLayLow, 0.90f, "We are not ready yet...");

            if (player.PlayStyle == PlayStyle.Aggressive && player.CurrentStrength > 80)
                return StrategyHelper.Make(warlord, CommandType.CommandExtort, 0.75f,
                    "Make villages pay – slow down the player.");

            if (threat.OverallThreat < 0.25f)
                return StrategyHelper.Make(warlord, CommandType.CommandExtort, 0.80f,
                    "Easy prey... Time to collect tribute.");

            return StrategyHelper.Make(warlord, CommandType.Ambush, 0.70f, "Set the trap, wait.");
        }

        public float GetActionAffinity(CommandType action) => action switch
        {
            CommandType.CommandExtort => 1.60f,
            CommandType.Harass => 1.40f,
            CommandType.Ambush => 1.40f,
            CommandType.CommandBuildRepute => 1.25f,
            CommandType.CommandLayLow => 1.10f,
            CommandType.Hunt => 0.70f,
            CommandType.Patrol => 0.80f,
            _ => 1.0f
        };
    }

    public class VengefulStrategy : IPersonalityStrategy
    {
        public StrategicCommand DetermineResponse(Warlord warlord, LegitimacyLevel level, ThreatAssessment threat, PlayerProfile player)
        {
            float rage = TaleWorlds.Library.MathF.Min(1f, player.TotalKills / 20f);

            if (threat.OverallThreat > 0.85f)
                return StrategyHelper.Make(warlord, CommandType.Retreat, 0.90f, "Not today – I will get stronger.");

            if (rage > 0.75f)
            {
                if (threat.OverallThreat < 0.95f)
                    return StrategyHelper.Make(warlord, CommandType.Hunt, 1.0f,
                        $"{player.TotalKills} militias killed. BLOOD WILL BE SPILLED!");
                else
                    return StrategyHelper.Make(warlord, CommandType.Patrol, 0.80f,
                        "You are too strong... I will watch you from afar.");
            }

            if (rage > 0.40f)
                return StrategyHelper.Make(warlord, CommandType.Ambush, 0.85f, "I am watching you...");

            if (player.TotalKills > 3 && threat.OverallThreat < 0.5f)
                return StrategyHelper.Make(warlord, CommandType.CommandRaidVillage, 0.75f, "Pay for my losses!");

            return StrategyHelper.Make(warlord, CommandType.Patrol, 0.50f, "Watch silently...");
        }

        public float GetActionAffinity(CommandType action) => action switch
        {
            CommandType.Hunt => 1.80f,
            CommandType.Ambush => 1.40f,
            CommandType.CommandRaidVillage => 1.25f,
            CommandType.Harass => 1.10f,
            CommandType.Retreat => 0.30f,
            CommandType.CommandLayLow => 0.20f,
            _ => 1.0f
        };

    }

    internal static class StrategyHelper
    {
        internal static StrategicCommand Make(Warlord w, CommandType t, float p, string r)
            => new StrategicCommand { Type = t, Priority = p, Reason = $"{w.Name}: {r}" };
    }
}


