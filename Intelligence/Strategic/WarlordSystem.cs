using BanditMilitias.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Systems.Economy;
using BanditMilitias.Infrastructure;
using BanditMilitias.Systems.Bounty;
using BanditMilitias.Systems.Fear;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Core.Neural;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Intelligence.Strategic
{

    public class WarlordSystem : BanditMilitias.Core.Components.MilitiaModuleBase
    {
        public override string ModuleName => "WarlordSystem";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 90;

        private static readonly Lazy<WarlordSystem> _instance =
            new Lazy<WarlordSystem>(() => new WarlordSystem());

        public static WarlordSystem Instance => _instance.Value;
        public int InfightingTriggerCount => _infightingTriggers;
        public float TotalGlobalReserves => _allWarlords.Values.Sum(w => w.ReserveManpower);

        private Dictionary<string, Warlord> _allWarlords = new();
        private Dictionary<Settlement, Warlord> _warlordsByHideout = new();
        private Dictionary<Hero, Warlord> _warlordsByHero = new();

        private Dictionary<Warlord, StrategicState> _warlordStates = new();

        private List<Warlord> _cachedWarlordList = new();
        private bool _warlordListDirty = true;

        private const int HERO_POCKET_MONEY = 7000;
        private const float WEALTH_TAX_RATE = 0.005f;
        private const float INCOME_BASE = 150f;
        private const float INCOME_PER_MILITIA = 170f;

        private const float DEFENSIVE_BUDGET_MULTIPLIER = 1.5f;
        private const float OFFENSIVE_BUDGET_MULTIPLIER = 1.2f;
        private const float SURVIVAL_BUDGET_MULTIPLIER = 2.0f;

        private int _totalWarlords = 0;
        private int _totalMilitias = 0;
        private float _totalWealth = 0f;
        private int _strategicAdjustments = 0;
        private float _exposedRisk = 0f;
        private int _infightingTriggers = 0;

        private bool _isInitialized = false;
        private readonly object _initLock = new object();

        private WarlordSystem() { }

        public override void Initialize()
        {
            lock (_initLock)
            {
                if (_isInitialized) return;

                try
                {

                    EventBus.Instance.Subscribe<MilitiaKilledEvent>(OnMilitiaKilled);
                    EventBus.Instance.Subscribe<HideoutClearedEvent>(OnHideoutCleared);

                    EventBus.Instance.Subscribe<StrategicCommandEvent>(OnStrategicCommand);

                    EventBus.Instance.Subscribe<StrategicAssessmentEvent>(OnStrategicAssessment);

                    EventBus.Instance.Subscribe<CommandCompletionEvent>(OnCommandCompletion);

                    EventBus.Instance.Subscribe<BanditMilitias.Core.Events.MilitiaSpawnedEvent>(OnMilitiaSpawned);

                    if (_allWarlords.Count > 0)
                    {
                        RebuildWarlordMaps();
                        InitializeStrategicStates();
                    }

                    _isInitialized = true;

                    if (Settings.Instance?.TestingMode == true)
                    {
                        DebugLogger.Info("WarlordSystem", $"Initialized with {_allWarlords.Count} warlords (Strategic coordination enabled)");
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Error("WarlordSystem", $"Initialization failed: {ex.Message}");
                    throw;
                }
            }
        }

        public override void RegisterCampaignEvents()
        {
            // SyncData bu noktada Ã§alÄ±ÅmÄ±Å, _allWarlords save'den yÃ¼klenmiÅ.
            // Initialize() sÄ±rasÄ±nda boÅtu, Åimdi map'leri gÃ¼venle inÅa edebiliriz.
            lock (_initLock)
            {
                if (_allWarlords.Count > 0)
                {
                    try
                    {
                        RebuildWarlordMaps();
                        InitializeStrategicStates();

                        if (Settings.Instance?.TestingMode == true)
                        {
                            DebugLogger.Info("WarlordSystem",
                                $"Post-load rebuild: {_allWarlords.Count} warlords aktif.");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Error("WarlordSystem",
                            $"RegisterCampaignEvents rebuild failed: {ex.Message}");
                    }
                }
                else
                {
                    // Yeni oyun ya da warlord henÃ¼z yok - normal
                    if (Settings.Instance?.TestingMode == true)
                    {
                        DebugLogger.Info("WarlordSystem",
                            "RegisterCampaignEvents: warlord yok, ilk promotion bekleniyor.");
                    }
                }
            }
        }


        public override void SyncData(IDataStore dataStore)
        {
            var list = new System.Collections.Generic.List<Warlord>(_allWarlords.Values);
            _ = dataStore.SyncData("BanditMilitias_WarlordList_v2", ref list);

            // Persist Strategic States (Threat levels, postures, etc.)
            var stateMap = new Dictionary<string, StrategicState>();
            if (dataStore.IsSaving)
            {
                foreach (var kvp in _warlordStates)
                {
                    if (kvp.Key != null && kvp.Value != null)
                        stateMap[kvp.Key.StringId] = kvp.Value;
                }
            }

            _ = dataStore.SyncData("BanditMilitias_WarlordStates_v1", ref stateMap);

            if (dataStore.IsLoading)
            {
                if (list != null)
                {
                    _allWarlords.Clear();
                    foreach (var w in list)
                    {
                        if (w?.StringId != null)
                            _allWarlords[w.StringId] = w;
                    }
                }

                // CommandedMilitias rebuild: MobileParty referanslari save'den dogrudan
                // gelmez, StringId'lerden MobileParty.All Ã¼zerinden eslestirilir.
                var partyLookup = MobileParty.All.ToDictionary(p => p.StringId, p => p);
                foreach (var warlord in _allWarlords.Values)
                {
                    warlord.CommandedMilitias.Clear();
                    foreach (var id in warlord.CommandedMilitiaIds)
                    {
                        if (partyLookup.TryGetValue(id, out var party) && party.IsActive)
                            warlord.CommandedMilitias.Add(party);
                    }
                    // Artik yasamamayan partilerin ID'lerini temizle
                    warlord.CommandedMilitiaIds.RemoveAll(id => !partyLookup.ContainsKey(id));

                    // SharedIntel rebuild
                    warlord.SharedIntel.Clear();
                    foreach (var id in warlord.SharedIntelIds)
                    {
                        if (partyLookup.TryGetValue(id, out var party) && party.IsActive)
                            warlord.SharedIntel.Add(party);
                    }
                    warlord.SharedIntelIds.RemoveAll(id => !partyLookup.ContainsKey(id));
                }

                if (stateMap != null)
                {
                    _warlordStates.Clear();
                    foreach (var kvp in stateMap)
                    {
                        var warlord = GetWarlord(kvp.Key);
                        if (warlord != null)
                            _warlordStates[warlord] = kvp.Value;
                    }
                }
            }
        }

        public override void Cleanup()
        {
            lock (_initLock)
            {
                EventBus.Instance.Unsubscribe<MilitiaKilledEvent>(OnMilitiaKilled);
                EventBus.Instance.Unsubscribe<HideoutClearedEvent>(OnHideoutCleared);
                EventBus.Instance.Unsubscribe<StrategicCommandEvent>(OnStrategicCommand);
                EventBus.Instance.Unsubscribe<StrategicAssessmentEvent>(OnStrategicAssessment);
                EventBus.Instance.Unsubscribe<CommandCompletionEvent>(OnCommandCompletion);

                EventBus.Instance.Unsubscribe<BanditMilitias.Core.Events.MilitiaSpawnedEvent>(OnMilitiaSpawned);

                _allWarlords.Clear();
                _warlordsByHideout.Clear();
                _warlordsByHero.Clear();
                _warlordStates.Clear();

                _cachedWarlordList.Clear();
                _warlordListDirty = true;

                _isInitialized = false;
            }
        }

        private void OnStrategicCommand(StrategicCommandEvent evt)
        {
            if (evt.Command == null) return;

            try
            {

                if (evt.TargetParty != null)
                {
                    var warlord = FindWarlordForMilitia(evt.TargetParty);
                    if (warlord != null)
                    {
                        if (!_warlordStates.TryGetValue(warlord, out var state) || state.LastCommandTime == CampaignTime.Zero || (CampaignTime.Now - state.LastCommandTime).ToHours >= 0.5f)
                        {
                            UpdateWarlordStrategicState(warlord, evt.Command);
                        }
                    }
                }
                else
                {

                    foreach (var warlord in _allWarlords.Values)
                    {
                        if (warlord.IsAlive)
                        {
                            if (!_warlordStates.TryGetValue(warlord, out var state) || state.LastCommandTime == CampaignTime.Zero || (CampaignTime.Now - state.LastCommandTime).ToHours >= 0.5f)
                            {
                                UpdateWarlordStrategicState(warlord, evt.Command);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error("WarlordSystem", $"Strategic command handling failed: {ex.Message}");
            }
        }

        private void OnStrategicAssessment(StrategicAssessmentEvent evt)
        {
            if (evt.Assessment == null) return;

            if (evt.TargetWarlord != null)
            {

                ReceiveStrategicAssessment(evt.TargetWarlord, evt.Assessment);
            }
            else
            {

                foreach (var warlord in _allWarlords.Values)
                {
                    if (warlord.IsAlive)
                    {
                        ReceiveStrategicAssessment(warlord, evt.Assessment);
                    }
                }
            }
        }

        private void OnCommandCompletion(CommandCompletionEvent evt)
        {

            if (evt.Party == null) return;

            var warlord = FindWarlordForMilitia(evt.Party);
            if (warlord == null) return;

            if (!_warlordStates.TryGetValue(warlord, out var state)) return;

            if (evt.Status == CommandCompletionStatus.Success)
            {
                state.SuccessfulCommands++;
                state.CommandSuccessRate = state.SuccessfulCommands / (float)Math.Max(1, state.TotalCommands);
            }
            else
            {
                state.FailedCommands++;
            }

            state.TotalCommands++;

            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("WarlordSystem",
                    $"[Warlord Feedback] {warlord.Name}: Command {evt.Status} (Success rate: {state.CommandSuccessRate:P0})");
            }
        }

        private void UpdateWarlordStrategicState(Warlord warlord, StrategicCommand command)
        {
            if (!_warlordStates.TryGetValue(warlord, out var state))
            {
                state = new StrategicState();
                _warlordStates[warlord] = state;
            }

            state.CurrentPosture = command.Type switch
            {
                CommandType.Defend or CommandType.Retreat => StrategicPosture.Defensive,
                CommandType.Hunt or CommandType.Ambush => StrategicPosture.Offensive,
                CommandType.Harass => StrategicPosture.Opportunistic,
                CommandType.Patrol => StrategicPosture.Normal,
                _ => state.CurrentPosture
            };

            state.LastCommandTime = CampaignTime.Now;
            state.ActiveCommandType = command.Type;

            _strategicAdjustments++;
        }

        private void ReceiveStrategicAssessment(Warlord warlord, StrategicAssessment assessment)
        {

            if (!_warlordStates.TryGetValue(warlord, out var state))
            {
                state = new StrategicState();
                _warlordStates[warlord] = state;
            }

            state.ThreatLevel = assessment.ThreatLevel;
            state.RecommendedPosture = assessment.RecommendedPosture;
            state.PlayerThreatLevel = assessment.PlayerThreat;

            if (assessment.ThreatLevel > 0.7f)
            {

                state.MilitaryBudgetMultiplier = SURVIVAL_BUDGET_MULTIPLIER;
                state.RecruitmentPaused = true;
            }
            else if (assessment.ThreatLevel > 0.4f)
            {

                state.MilitaryBudgetMultiplier = DEFENSIVE_BUDGET_MULTIPLIER;
                state.RecruitmentPaused = false;
            }
            else
            {

                state.MilitaryBudgetMultiplier = 1.0f;
                state.RecruitmentPaused = false;
            }

            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("WarlordSystem",
                    $"[Strategic Assessment] {warlord.Name}: Threat={assessment.ThreatLevel:F2}, Posture={assessment.RecommendedPosture}");
            }
        }

        public Warlord? GetWarlordForParty(MobileParty militia)
        {
            return FindWarlordForMilitia(militia);
        }

        private Warlord? FindWarlordForMilitia(MobileParty militia)
        {
            if (militia?.PartyComponent is not BanditMilitias.Components.MilitiaPartyComponent component)
                return null;

            if (component.HomeSettlement == null) return null;

            _ = _warlordsByHideout.TryGetValue(component.HomeSettlement, out var warlord);
            return warlord;
        }

        public Warlord? CreateWarlordFromHero(Hero hero)
        {
            if (hero == null) return null;
            if (_allWarlords.Any(w => w.Value.LinkedHero == hero)) return _allWarlords.First(w => w.Value.LinkedHero == hero).Value;

            var warlord = new Warlord
            {
                StringId = hero.StringId,
                Name = hero.FirstName?.ToString() ?? hero.Name?.ToString() ?? "Bilinmeyen",
                LinkedHero = hero,
                CreationTime = CampaignTime.Now,
                Gold = (float)hero.Gold,
                IsAlive = hero.IsAlive,
                IsRoaming = true
            };

            // AI Karar Layer'Ä±: MÃ¼lkiyet Belirleme
            DetermineInitialProperty(warlord, hero);

            _allWarlords.Add(warlord.StringId, warlord);
            Infrastructure.FileLogger.Log($"[WarlordSystem] Created Warlord: {warlord.FullName}, Property: {warlord.OwnedSettlement?.Name?.ToString() ?? "None"}");
            
            return warlord;
        }

        private void DetermineInitialProperty(Warlord warlord, Hero hero)
        {
            if (hero.CurrentSettlement != null && hero.CurrentSettlement.IsHideout)
            {
                warlord.AssignedHideout = hero.CurrentSettlement;
            }

            // GÃ¼ce dayalÄ± mÃ¼lkiyeti belirle (Castle / Town / Village)
            // 50k+ AltÄ±n ve 100+ Asker varsa bir KALE veya ÅEHÄ°R hedefleyebilir
            float gold = (float)hero.Gold;
            int troops = hero.PartyBelongedTo?.MemberRoster.TotalManCount ?? 0;

            // Settlement.All.Where.OrderBy yerine ModuleManager cache + mesafe filtresi
            var heroPos = CompatibilityLayer.GetHeroPosition(hero).AsVec2;
            float searchSq = 100f * 100f;
            var mm = BanditMilitias.Infrastructure.ModuleManager.Instance;
            var nearbySettlements = new System.Collections.Generic.List<Settlement>();
            foreach (var c in mm.TownCache)
                if (CompatibilityLayer.GetSettlementPosition(c).DistanceSquared(heroPos) < searchSq) nearbySettlements.Add(c);
            foreach (var c in mm.CastleCache)
                if (CompatibilityLayer.GetSettlementPosition(c).DistanceSquared(heroPos) < searchSq) nearbySettlements.Add(c);
            nearbySettlements.Sort((a, b) =>
                CompatibilityLayer.GetSettlementPosition(a).Distance(heroPos)
                .CompareTo(CompatibilityLayer.GetSettlementPosition(b).Distance(heroPos)));

            if (gold > 50000 && troops > 80)
            {
                // YakÄ±ndaki bir Kaleyi veya Åehri sahiplenmiÅ gibi gÃ¶ster (Stratejik Hedef)
                var fortress = nearbySettlements.FirstOrDefault(s => s.IsCastle || s.IsTown);
                if (fortress != null)
                {
                    warlord.OwnedSettlement = fortress;
                    warlord.Title = fortress.IsTown ? "Fatih" : "SavaÅ Lordu";
                }
            }
            else if (gold > 15000)
            {
                // YakÄ±ndaki kÃ¶yleri nÃ¼fuz altÄ±na al
                var villages = nearbySettlements.Where(s => s.IsVillage).Take(2).ToList();
                foreach (var v in villages)
                {
                    warlord.InfluencedVillages.Add(v);
                }
                warlord.Title = "ÃnlÃ¼ EÅkÄ±ya";
            }
            else
            {
                warlord.Title = "Kaptan";
            }
        }

        private void InitializeStrategicStates()
        {
            foreach (var warlord in _allWarlords.Values)
            {
                if (!_warlordStates.ContainsKey(warlord))
                {
                    _warlordStates[warlord] = new StrategicState();
                }
            }
        }

        public Warlord? CreateWarlord(Settlement hideout, Hero? hero = null)
        {
            if (hideout == null || !hideout.IsHideout)
            {
                DebugLogger.Warning("WarlordSystem", "Cannot create warlord: invalid hideout");
                return null;
            }

            lock (_initLock)
            {
                if (_warlordsByHideout.ContainsKey(hideout))
                {
                    DebugLogger.Warning("WarlordSystem", $"Warlord already exists for {hideout.Name}");
                    return _warlordsByHideout[hideout];
                }

                var warlord = new Warlord
                {
                    StringId = $"warlord_{hideout.StringId}_{Guid.NewGuid().ToString().Substring(0, 8)}",
                    Name = GenerateWarlordName(hideout),
                    AssignedHideout = hideout,
                    LinkedHero = hero,
                    Personality = GeneratePersonality(),
                    Motivation = GenerateMotivation(),
                    Gold = 3000,
                    CreationTime = CampaignTime.Now,
                    IsAlive = true
                };

                _allWarlords[warlord.StringId] = warlord;
                _warlordsByHideout[hideout] = warlord;

                _warlordListDirty = true;

                if (hero != null)
                {
                    _warlordsByHero[hero] = warlord;
                }

                _warlordStates[warlord] = new StrategicState();

                _totalWarlords++;
                if (_totalWarlords == 58 || _totalWarlords == (Settings.Instance?.MaxWarlordCount ?? 58))
                {
                    warlord.Name = "MalkoÃ§oÃ°lu";
                    if (hero != null)
                    {
                        var malkocogluName = new TaleWorlds.Localization.TextObject("{=BM_Malkocoglu}MalkoÃ§oÃ°lu");
                        hero.SetName(malkocogluName, malkocogluName);
                    }
                }

                try
                {
                    BanditBrain.Instance.UpdatePlayerProfile();
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("WarlordSystem", $"Failed to notify BanditBrain: {ex.Message}");
                }

                if (Settings.Instance?.TestingMode == true && Settings.Instance?.ShowTestMessages == true)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[Warlord] {warlord.Name} established at {hideout.Name} ({warlord.Personality})",
                        Colors.Yellow));
                }

                return warlord;
            }
        }

        public void RemoveWarlord(Warlord warlord)
        {
            if (warlord == null) return;

            lock (_initLock)
            {
                warlord.IsAlive = false;

                // WarlordFallenEvent fÄ±rlat
                try
                {
                    var fallenEvt = EventBus.Instance.Get<BanditMilitias.Core.Events.WarlordFallenEvent>();
                    if (fallenEvt != null)
                    {
                        fallenEvt.Warlord = warlord;
                        fallenEvt.PeakFear = warlord.FearScore;
                        fallenEvt.WinningTactics = null;
                        fallenEvt.KilledBy = "unknown";
                        NeuralEventRouter.Instance.Publish(fallenEvt);
                        EventBus.Instance.Return(fallenEvt);
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("WarlordSystem", $"WarlordFallenEvent dispatch failed: {ex.Message}");
                }

                _ = _allWarlords.Remove(warlord.StringId);
                _ = _warlordStates.Remove(warlord);

                if (warlord.AssignedHideout != null)
                {
                    _ = _warlordsByHideout.Remove(warlord.AssignedHideout);
                }

                if (warlord.LinkedHero != null)
                {
                    _ = _warlordsByHero.Remove(warlord.LinkedHero);
                }

                _warlordListDirty = true;

                var bestSuccessor = warlord.CommandedMilitias
                    .Where(m => m != null && m.IsActive && m.LeaderHero != null && m.LeaderHero.IsAlive)
                    .OrderByDescending(m => m.MemberRoster.TotalManCount)
                    .FirstOrDefault();

                if (bestSuccessor?.PartyComponent is BanditMilitias.Components.MilitiaPartyComponent successorComp)
                {
                    successorComp.DaysAlive = Math.Max(successorComp.DaysAlive, Settings.Instance?.WarlordMinDaysAlive ?? 30);
                    successorComp.BattlesWon = Math.Max(successorComp.BattlesWon, Settings.Instance?.WarlordMinBattlesWon ?? 3);
                    successorComp.HasBeenPromotedToWarlord = false;

                    if (Settings.Instance?.TestingMode == true)
                    {
                        DebugLogger.Info("WarlordSystem",
                            $"[SUCCESSOR] {bestSuccessor.LeaderHero?.Name} designated as successor for fallen {warlord.Name}");
                    }
                }

                foreach (var militia in warlord.CommandedMilitias.ToList())
                {
                    SynchronizeReleasedMilitiaState(militia);
                    warlord.ReleaseMilitia(militia);
                }

                if (Settings.Instance?.TestingMode == true && Settings.Instance?.ShowTestMessages == true)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[Warlord] {warlord.Name} has fallen",
                        Colors.Red));
                }

                try
                {
                    if (IsFearSystemOperational())
                        FearSystem.Instance.OnWarlordDefeated(warlord.StringId);
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("WarlordSystem", $"Failed to notify FearSystem: {ex.Message}");
                }
            }
        }

        // SeedInitialWarlords: Tasarım gereği boş — warlord oluşturma TryPromoteCaptainToWarlord üzerinden yapılır.

        public bool TryPromoteCaptainToWarlord(MobileParty militia)
        {
            if (militia == null) return false;
            if (militia.PartyComponent is not BanditMilitias.Components.MilitiaPartyComponent comp) return false;

            Hero? captain = militia.LeaderHero;
            Settlement? homeSettlement = comp.HomeSettlement;

            float score = CalculateBanditScore(militia, comp);
            int minTroops = Settings.Instance?.WarlordMinTroops ?? 15;

            // Warlord terfisi iÃ§in ana eÅŸik: 500 puan + min asker (Nam Ã§ok yÃ¼ksekse asker sÄ±nÄ±rÄ± esner)
            bool scoreMet = score >= 500f || (score >= 350f && militia.MemberRoster.TotalManCount >= minTroops);
            
            bool hasExistingWarlordForHideout = homeSettlement != null && _warlordsByHideout.ContainsKey(homeSettlement);
            
            if (!scoreMet || comp.HasBeenPromotedToWarlord || hasExistingWarlordForHideout || captain == null || !captain.IsAlive) 
                return false;

            Settlement promotionHideout = homeSettlement!;
            Hero promotionCaptain = captain!;

            var warlord = CreateWarlord(promotionHideout, promotionCaptain);
            if (warlord == null) return false;

            comp.HasBeenPromotedToWarlord = true;
            warlord.CommandMilitia(militia);
            SynchronizeMilitiaBannerState(militia, warlord);

            _ = WarlordLegitimacySystem.Instance.GetLevel(warlord.StringId);

            InformationManager.DisplayMessage(new InformationMessage(
                $"?? {promotionCaptain.Name} ({score:F0} Nam) {promotionHideout.Name} konumunda bir SAVAÅž LORDU olarak yÃ¼kseldi!",
                Colors.Magenta));

            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("WarlordSystem",
                    $"[PROMOTE] {promotionCaptain.Name} Â› Warlord (Days: {comp.DaysAlive}, Battles: {comp.BattlesWon}, Troops: {militia.MemberRoster.TotalManCount})");
            }

            return true;
        }

        public void TryAssignCaptainToParty(MobileParty militia)
        {
            if (militia == null || militia.PartyComponent is not BanditMilitias.Components.MilitiaPartyComponent comp) return;

            // EÄŸer zaten kaptan veya daha Ã¼stÃ¼ ise dokunma
            if (comp.Role >= BanditMilitias.Components.MilitiaPartyComponent.MilitiaRole.Captain) return;

            // 25+ asker barajÄ±nÄ± geÃ§tiyse kaptan ata
            if (militia.MemberRoster.TotalManCount >= 25)
            {
                comp.Role = BanditMilitias.Components.MilitiaPartyComponent.MilitiaRole.Captain;
                
                // Kaptan ekipmanÄ± ver (Ekonomik RPG gereÄŸi)
                militia.PartyTradeGold += 500; 

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Info("WarlordSystem", $"[CAPTAIN] {militia.Name} ordusu bÃ¼yÃ¼dÃ¼ ve baÅŸÄ±na bir KAPTAN atandÄ±.");
                }
            }
        }

        private void CheckFallbackPromotion()
        {
            var startTime = CompatibilityLayer.GetCampaignStartTime();
            float elapsedDays = startTime != CampaignTime.Zero
                ? (float)(CampaignTime.Now - startTime).ToDays
                : 0f;

            int fallbackDays = Settings.Instance?.FamousBanditFallbackDays ?? 60;
            if (WarlordProgressionRules.ShouldRunFamousBanditFallback(_allWarlords.Count, elapsedDays, fallbackDays))
            {
                var promotablePool = ModuleManager.Instance.ActiveMilitias
                    .Where(m => m != null && m.IsActive
                             && m.LeaderHero != null && m.LeaderHero.IsAlive
                             && m.PartyComponent is BanditMilitias.Components.MilitiaPartyComponent c
                             && !c.HasBeenPromotedToWarlord
                             && c.HomeSettlement != null)
                    .Select(m => new
                    {
                        Party = m,
                        Component = (BanditMilitias.Components.MilitiaPartyComponent)m.PartyComponent
                    })
                    .ToList();

                var candidates = WarlordProgressionRules.SelectTopFallbackCandidates(
                    promotablePool,
                    maxCount: 3,
                    idSelector: x => x.Party.StringId,
                    daysSelector: x => x.Component.DaysAlive,
                    battlesSelector: x => x.Component.BattlesWon,
                    troopSelector: x => x.Party.MemberRoster.TotalManCount);

                foreach (var candidate in candidates)
                {
                    var comp = candidate.Component;
                    comp.DaysAlive = Math.Max(comp.DaysAlive, Settings.Instance?.WarlordMinDaysAlive ?? 30);
                    comp.BattlesWon = Math.Max(comp.BattlesWon, Settings.Instance?.WarlordMinBattlesWon ?? 3);

                    bool promoted = TryPromoteCaptainToWarlord(candidate.Party);
                    if (promoted && Settings.Instance?.TestingMode == true)
                    {
                        DebugLogger.Info("WarlordSystem",
                            $"[FALLBACK] Forced Warlord promotion after {elapsedDays:F0} days.");
                    }
                }
            }

            int warlordCount = _allWarlords.Values.Count(w =>
                w.IsAlive && WarlordLegitimacySystem.Instance.GetLevel(w.StringId) >= LegitimacyLevel.Warlord);
            int maxWarlordLimit = Settings.Instance?.MaxWarlordCount ?? 5;
            bool isWarlordLimitReached = warlordCount >= maxWarlordLimit;

            int warlordFallbackDays = Settings.Instance?.WarlordFallbackDays ?? 150;
            if (WarlordProgressionRules.ShouldRunWarlordFallback(
                _allWarlords.Count,
                isWarlordLimitReached,
                elapsedDays,
                warlordFallbackDays))
            {
                var bestWarlord = _allWarlords.Values
                    .Where(w => w.IsAlive)
                    .OrderByDescending(w => WarlordLegitimacySystem.Instance.GetPoints(w.StringId))
                    .FirstOrDefault();

                if (bestWarlord != null)
                {
                    float currentPoints = WarlordLegitimacySystem.Instance.GetPoints(bestWarlord.StringId);
                    float needed = 1500f - currentPoints;
                    if (needed > 0)
                    {

                        WarlordLegitimacySystem.Instance.AddFallbackPoints(bestWarlord, needed);

                        if (Settings.Instance?.TestingMode == true)
                        {
                            DebugLogger.Info("WarlordSystem",
                                $"[FALLBACK] Forced Warlord promotion for {bestWarlord.Name} after {elapsedDays:F0} days.");
                        }
                    }
                }
            }
        }

        public override void OnDailyTick()
        {
            if (!_isInitialized) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;

            var militiasByHideout = new Dictionary<Settlement, List<MobileParty>>();
            foreach (var m in ModuleManager.Instance.ActiveMilitias)
            {
                if (m?.PartyComponent is BanditMilitias.Components.MilitiaPartyComponent c
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
                    if (militia.PartyComponent is BanditMilitias.Components.MilitiaPartyComponent comp)
                    {
                        comp.DaysAlive++;
                        
                        // NEW: Veteran Captain Promotion
                        if (comp.Role == BanditMilitias.Components.MilitiaPartyComponent.MilitiaRole.Captain)
                        {
                            float score = CalculateBanditScore(militia, comp);
                            if (score >= 150f) 
                            {
                                comp.Role = BanditMilitias.Components.MilitiaPartyComponent.MilitiaRole.VeteranCaptain;
                                InformationManager.DisplayMessage(new InformationMessage(
                                    $"{militia.Name} artÄ±k bir KÄ±demli Kaptan!", Colors.Yellow));
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
                    // NEW: Process merging for Tier 0-1
                    WarlordEconomySystem.Instance.ProcessMerging(warlord);

                    // NEW: Manage Reserves (Convert manpower to XP/Training)
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

            // NEW: Centralized Economy System
            var tier = WarlordCareerSystem.Instance.GetTier(warlord.StringId);
            float netOutcome = WarlordEconomySystem.Instance.CalculateDailyOutcome(warlord, tier);

            // Hero Sync (Income might be adjusted by hero gold)
            SyncGoldWithHero(warlord, ref netOutcome);

            warlord.Gold = Math.Max(0, warlord.Gold + netOutcome);

            if (ShouldMakeStrategicDecisions(warlord, state))
            {
                ConsiderStrategicActions(warlord, state);
            }

            // NEW: Overpopulation Response (Natural Selection)
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

            // XP Multiplier bonus: 0.1 per 500 reserve manpower (cap at 2.5x)
            float bonus = warlord.ReserveManpower / 5000f;
            warlord.XpMultiplier = Math.Min(2.5f, 1.0f + bonus);

            // Reserve Decay: 2% daily (veterans retire or desert if not used)
            warlord.ReserveManpower *= 0.98f;
            if (warlord.ReserveManpower < 1f) warlord.ReserveManpower = 0f;
        }

        private void StrategicAI_OverpopulationResponse(Warlord warlord)
        {
            int totalParties = Campaign.Current.MobileParties.Count;
            if (totalParties < 1400) return;

            // If world is full, issue 'Engage Rival' or 'Aggressive Hunt' orders
            if (warlord.CurrentOrder == null || warlord.CurrentOrder.Type == CommandType.Patrol)
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

            // NEW: Treasury Safety Check (User Request)
            // If hero is a prisoner, dont siphon gold from them. (Corporate Treasury logic)
            if (warlord.LinkedHero.IsPrisoner) 
            {
                if (Settings.Instance?.TestingMode == true)
                    DebugLogger.Info("WarlordSystem", $"[Safety] {warlord.Name} is a prisoner. Siphoning paused.");
                return;
            }

            try
            {
                // Pull surplus gold to Warlord Corporate Treasury
                if (warlord.LinkedHero.Gold > HERO_POCKET_MONEY)
                {
                    int surplus = warlord.LinkedHero.Gold - HERO_POCKET_MONEY;
                    income += surplus;
                    warlord.LinkedHero.ChangeHeroGold(-surplus);
                }

                // If hero is poor, fund them from their own daily income before it goes to treasury
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

            if (warlord.CommandedMilitias.Count < 2 && warlord.Gold > 1000)
            {
                warlord.IssueOrderToMilitias(new StrategicCommand
                {
                    Type = CommandType.Patrol,
                    Priority = 0.5f,
                    Reason = "Low militia count Â patrol mode while waiting for reinforcements"
                });
            }

            if (warlord.Gold > 20000 && state.ThreatLevel < 0.3f)
            {
                warlord.IssueOrderToMilitias(new StrategicCommand
                {
                    Type = CommandType.CommandBuildRepute,
                    Priority = 0.7f,
                    Reason = "Warlord has surplus gold and low threat Â building repute"
                });
            }

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
                            Reason = "High bounty Â laying low to survive"
                        });
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("WarlordSystem", $"Bounty check failed: {ex.Message}");
                }
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
            if (militia?.PartyComponent is not MilitiaPartyComponent comp)
                return;

            comp.WarlordId = warlord.StringId;
            comp.AssignedWarlord = warlord; // ✅ FIX: Performance cache
            comp.SetBannerPrestigeLevel(WarlordLegitimacySystem.Instance.GetLevel(warlord.StringId));
        }

        private static void SynchronizeReleasedMilitiaState(MobileParty militia)
        {
            if (militia?.PartyComponent is not MilitiaPartyComponent comp)
                return;

            comp.WarlordId = null;
            comp.AssignedWarlord = null; // ✅ FIX: Performance cache
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

                if (Settings.Instance?.TestingMode == true)
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

        /// <summary>Warlord'un FearScore'unu gÃ¼venli Åekilde artÄ±r (TerritorySystem vb. iÃ§in).</summary>
        public void AddFearScore(string warlordId, float amount)
        {
            var w = GetWarlord(warlordId);
            if (w == null || !w.IsAlive) return;
            w.FearScore = System.Math.Min(100f, w.FearScore + amount);
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

        private string GenerateWarlordName(Settlement hideout)
        {

            Clan banditClan = hideout.OwnerClan;
            var nameObj = BanditMilitias.Systems.Spawning.MilitiaNameGenerator.GenerateName(hideout, banditClan);

            string baseName = nameObj.ToString();

            var prefixes = new[] { "Warlord", "Chieftain", "Boss", "Captain", "Lord", "Master" };
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
            float renownPart = comp.Renown * 8f; // Nam %40
            float goldPart = (militia.LeaderHero != null ? (float)militia.LeaderHero.Gold / 50f : 0f); // AltÄ±n %15
            float troopPart = militia.MemberRoster.TotalManCount * 4f; // Asker %25 (Limit 15 ise 60 puan)
            float battlePart = comp.BattlesWon * 15f; // BaÅarÄ± %20
            
            return renownPart + goldPart + troopPart + battlePart;
        }

        private void CheckAndApplyChaosNerf()
        {
            if (Settings.Instance == null || !Settings.Instance.EnableChaosNerf) return;

            int maxAllowed = Settings.Instance.MaxWarlordCount;
            var activeWarlords = GetAllWarlords(); // GetAllWarlords zaten IsAlive filtreli

            if (activeWarlords.Count <= maxAllowed) return;

            // Limit asildi: en zayif Warlord'u (en dusuk FearScore) pasiflestirir
            var weakest = activeWarlords.OrderBy(w => w.FearScore).FirstOrDefault();
            if (weakest != null)
            {
                DebugLogger.Info("WarlordSystem",
                    $"ChaosNerf: {activeWarlords.Count} warlord mevcut, limit {maxAllowed}. " +
                    $"'{weakest.Name}' emekliye ayrildi (FearScore: {weakest.FearScore:F1}).");
                RemoveWarlord(weakest);
            }
        }
    }



    // ââ WarlordHelpers (inline) ââââââââââââââââââââââââââââââ
    // ââ WarlordProgressionRules âââââââââââââââââââââââââââââââââââââââââ


    // ── Inline: WarlordData ───────────────────────────────────────
    // NOT: Bu sinif su an aktif sistemler tarafindan instantiate edilmiyor.
    // Warlord sinifi tum islevleri ustleniyor. WarlordData ileride kullanilmak
    // uzere iskelet olarak korunuyor — kaldirmak yerine [Obsolete] isaretlendi.
    [System.Serializable]
    [System.Obsolete("WarlordData su an kullanilmiyor. Aktif sistem: Warlord sinifi.")]
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
            // Reconciled logic to satisfy both Level 1 (60d) and Level 3 (150d) tests:
            // 1. Must not have reached warlord limit (!hasWarlord)
            // 2. Must have passed fallback time (elapsedDays >= fallbackDays)
            // 3. Early seeding (60d): Allowed if 0 warlords
            // 4. Critical mass (150d): Allowed if 2+ warlords
            // 5. Never allowed if exactly 1 warlord (wait for natural progression)
            bool shouldUseWarlordFallbackWindow = activeWarlordCount != 1 &&
                                                  ((fallbackDays <= 60 && activeWarlordCount == 0) ||
                                                   (fallbackDays >= 150 && activeWarlordCount >= 2) ||
                                                   (fallbackDays > 60 && fallbackDays < 150 && activeWarlordCount == 0));
            bool timeCheck = !hasWarlord && elapsedDays >= Math.Max(0, (float)fallbackDays);
            // FIX: 0 warlord durumunda fallback her zaman geÃ§erli (Ã¶nceki kod 150d+ durumunda
            // hiÃ§ Warlord yokken bile fallback'i engelliyordu). 
            bool countCheck = activeWarlordCount == 0   // HiÃ§ Warlord yok â her zaman gerekli
                           || activeWarlordCount >= 2;  // 2+ var ama limit dolmamÄ±Å â devam et

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

    // ââ CoordinationPlanner âââââââââââââââââââââââââââââââââââââââââ

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

    // ââ Warlord âââââââââââââââââââââââââââââââââââââââââ

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

        [SaveableProperty(30)]
        public float WageDiscount { get; set; } = 0f;

        [SaveableProperty(31)]
        public float XpMultiplier { get; set; } = 1f;

        [SaveableProperty(32)]
        public float SpeedBonus { get; set; } = 0f;
        
        [SaveableProperty(33)]
        public float ReserveManpower { get; set; } = 0f;

        public string FullName => $"{Name} {Title}";

        // CommandedMilitias MobileParty referanslari save sisteminde dogrudan
        // serialize edilemez. StringId listesi kaydedilip yÃ¼klemede rebuild edilir.
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

        /// <summary>
        /// Gözlemci (Watcher) sistemini yönetir. Eğer gözlemci yoksa veya ölmüşse yenisini atar.
        /// </summary>
        public void EnsureWatcherSuccession()
        {
            if (CommandedMilitias.Count == 0)
            {
                WatcherPartyId = null;
                return;
            }

            // Mevcut gözlemci yaşıyor mu?
            var currentWatcher = CommandedMilitias.FirstOrDefault(p => p.StringId == WatcherPartyId && p.IsActive);
            if (currentWatcher != null) return;

            // Yeni gözlemci seç: En hızlı veya en düşük asker sayılı (Scout profili)
            var bestScout = CommandedMilitias
                .Where(p => p.IsActive)
                .OrderBy(p => p.MemberRoster.TotalManCount) // Az asker = Daha gizli/hızlı
                .ThenByDescending(p => p.Speed)
                .FirstOrDefault();

            if (bestScout != null)
            {
                WatcherPartyId = bestScout.StringId;
                if (bestScout.PartyComponent is MilitiaPartyComponent comp)
                {
                    comp.IsWatcher = true;
                    DebugLogger.TestLog($"[Watcher] {Name} için yeni gözlemci atandı: {bestScout.Name}", Colors.Cyan);
                }
            }
        }

        /// <summary>
        /// Gözlemci birimin topladığı verileri ağdaki herkese aktarır.
        /// </summary>
        public void UpdateSharedIntelligence(List<MobileParty> detectedParties)
        {
            if (detectedParties == null) return;

            // Eski verileri temizle ve yenilerini ekle (Max 15 stratejik hedef)
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
                    comp.AssignedWarlord = null; // ✅ FIX: Performance cache
            }
        }

        public void IssueOrderToMilitias(StrategicCommand command)
        {
            CurrentOrder = command;

            if (EventBus.Instance != null)
            {

                var evt = EventBus.Instance.Get<StrategicCommandEvent>();
                if (evt != null)
                {
                    evt.Command = command;
                    evt.IssuedBy = "Warlord:" + Name;
                    evt.Timestamp = CampaignTime.Now;
                    evt.TargetRegion = AssignedHideout;
                    NeuralEventRouter.Instance.Publish(evt);
                    EventBus.Instance.Return(evt);
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


    // ââ Types âââââââââââââââââââââââââââââââââââââââââ

    public enum BrainState { Dormant, Active, Degraded }
    public enum DistressLevel { Low, Medium, High, Critical }
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
            CombatEffectiveness = System.Math.Max(0f, System.Math.Min(1f, newLevel));
        }
    }

    public class RunningAverage
    {

        [SaveableField(1)]
        private float _savedValue;

        // Pencere boyutu kaydedilir; yüklemede buffer bu boyutla yeniden oluşturulur.
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
            // Yüklemeden sonra _values null gelebilir; kayıtlı pencere boyutuyla yeniden oluştur.
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


    // ââ Strategies âââââââââââââââââââââââââââââââââââââââââ

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
                return StrategyHelper.Make(warlord, CommandType.Defend, 0.75f, "Savun ve karşı saldır!");

            // Survival Rule: Low level aggressive warlords are more cautious of doomstacks
            if (level < LegitimacyLevel.Warlord && threat.StrengthThreat > 0.80f)
                return StrategyHelper.Make(warlord, CommandType.Retreat, 1.0f, "Çok güçlüler... Bugünlük bu kadar.");

            if (threat.OverallThreat > 0.55f)
                return StrategyHelper.Make(warlord, CommandType.Ambush, 0.82f + threat.OverallThreat * 0.10f, "Pusu kur – fırsatçı ol!");

            if (threat.OverallThreat > 0.25f)
                return StrategyHelper.Make(warlord, CommandType.Hunt, 0.85f, "AV ET!");

            return StrategyHelper.Make(warlord, CommandType.CommandRaidVillage, 0.90f, "Zayıf? YAKIP YIK!");
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
                return StrategyHelper.Make(warlord, CommandType.CommandLayLow, 0.95f, "Çok tehlikeli! Gizlen!");

            if (threat.OverallThreat > 0.40f)
                return StrategyHelper.Make(warlord, CommandType.Defend, 0.75f, "Savun, güvenli kal.");

            if (player.CurrentStrength < 30)
                return StrategyHelper.Make(warlord, CommandType.Harass, 0.58f, "Oyuncu zayıf – taciz et, kazan.");

            return StrategyHelper.Make(warlord, CommandType.Patrol, 0.45f, "Devriye – görünür kal ama dikkatli.");
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

            // Fuzzing fix: Prioritize threat to prevent suicidal behavior
            if (threat.OverallThreat > 0.70f)
                return StrategyHelper.Make(warlord, CommandType.CommandLayLow, 0.85f,
                    "Çok tehlikeli, saklan!");

            // Low level cunning warlords don't risk extortion against powerful players
            if (level < LegitimacyLevel.Warlord && threat.StrengthThreat > 0.6f)
                return StrategyHelper.Make(warlord, CommandType.CommandLayLow, 0.90f, "Henüz hazır değiliz...");

            if (player.PlayStyle == PlayStyle.Aggressive && player.CurrentStrength > 80)
                return StrategyHelper.Make(warlord, CommandType.CommandExtort, 0.75f,
                    "Köylere ödeme yaptır – oyuncuyu yavaşlat.");

            if (threat.OverallThreat < 0.25f)
                return StrategyHelper.Make(warlord, CommandType.CommandExtort, 0.80f,
                    "Kolay av... Haraç toplama zamanı.");

            return StrategyHelper.Make(warlord, CommandType.Ambush, 0.70f, "Tuzağı kur, bekle.");
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
                return StrategyHelper.Make(warlord, CommandType.Retreat, 0.90f, "Bugün değil – güçleneceğim.");

            // Fuzzing fix: Prevent death loops against overwhelming doomstacks (Threat > 0.95)
            if (rage > 0.75f)
            {
                if (threat.OverallThreat < 0.95f)
                    return StrategyHelper.Make(warlord, CommandType.Hunt, 1.0f,
                        $"{player.TotalKills} milis öldürdün. KANIN SORULACAK!");
                else
                    return StrategyHelper.Make(warlord, CommandType.Patrol, 0.80f,
                        "Çok güçlüsün... Seni uzaktan izleyeceğim.");
            }

            if (rage > 0.40f)
                return StrategyHelper.Make(warlord, CommandType.Ambush, 0.85f, "Seni izliyorum...");

            if (player.TotalKills > 3 && threat.OverallThreat < 0.5f)
                return StrategyHelper.Make(warlord, CommandType.CommandRaidVillage, 0.75f, "Kayıplarımı öde!");

            return StrategyHelper.Make(warlord, CommandType.Patrol, 0.50f, "Sessizce izle...");
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
