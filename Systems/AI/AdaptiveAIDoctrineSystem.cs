using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Systems.Tracking;
using BanditMilitias.Core.Neural;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;
using MathF = TaleWorlds.Library.MathF;

namespace BanditMilitias.Systems.AI
{
    [Serializable]
    public class AdaptiveDoctrineProfile
    {
        [SaveableProperty(1)]
        public string WarlordId { get; set; } = string.Empty;

        [SaveableProperty(2)]
        public PlayerCombatDoctrine ObservedPlayerDoctrine { get; set; } = PlayerCombatDoctrine.Unknown;

        [SaveableProperty(3)]
        public CounterDoctrine ActiveCounterDoctrine { get; set; } = CounterDoctrine.Balanced;

        [SaveableProperty(4)]
        public float Confidence { get; set; } = 0.55f;

        [SaveableProperty(5)]
        public int SuccessfulEngagements { get; set; }

        [SaveableProperty(6)]
        public int FailedEngagements { get; set; }

        [SaveableProperty(7)]
        public CampaignTime LastSwitchTime { get; set; } = CampaignTime.Zero;

        [SaveableProperty(8)]
        public CampaignTime LastUpdatedTime { get; set; } = CampaignTime.Zero;

        [SaveableProperty(9)]
        public float AggressionBias { get; set; }
    }

    [AutoRegister]
    public class AdaptiveAIDoctrineSystem : MilitiaModuleBase
    {
        public override string ModuleName => "AdaptiveAIDoctrineSystem";
        public override bool IsEnabled => Settings.Instance?.EnableAdaptiveAIDoctrine ?? true;
        public override int Priority => 92;

        private static readonly Lazy<AdaptiveAIDoctrineSystem> _instance =
            new Lazy<AdaptiveAIDoctrineSystem>(() => new AdaptiveAIDoctrineSystem());

        public static AdaptiveAIDoctrineSystem Instance => _instance.Value;

        private readonly object _stateLock = new object();
        private Dictionary<string, AdaptiveDoctrineProfile> _profilesByWarlord = new Dictionary<string, AdaptiveDoctrineProfile>();

        private int _doctrineShifts;
        private int _adaptationUpdates;
        private int _observedDoctrineSamples;
        private int _loggedDoctrineSamples;
        private int _observedBattleSamples;
        private int _loggedBattleSamples;
        private bool _isInitialized;

        private const string GLOBAL_PROFILE_ID = "__global__";

        private AdaptiveAIDoctrineSystem() { }

        public override void Initialize()
        {
            lock (_stateLock)
            {
                if (_isInitialized)
                    return;

                CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
                // FIX-GHOST: WarlordEquipmentMissionBehavior doctrine buff'larını uygulayabilmesi
                // için MapEventStarted'da CurrentBattleDoctrine set edilmesi gerekiyor.
                // Önceden bu bağlantı yoktu — doctrine seçiliyordu ama savaşa aktarılmıyordu.
                CampaignEvents.MapEventStarted.AddNonSerializedListener(this, OnMapEventStarted);
                EventBus.Instance.Subscribe<ThreatLevelChangedEvent>(OnThreatLevelChanged);
                
                // FIX: CampaignTime.Now crash prevention
                EnsureGlobalProfile();

                _isInitialized = true;
            }
        }

        public override void RegisterCampaignEvents()
        {
            // SAFE-FIX: When session is launched, Now is safe.
            // Update any profile that was initialized with Zero to Now to fix user's "0h AI Bug".
            lock (_stateLock)
            {
                foreach (var profile in _profilesByWarlord.Values)
                {
                    if (profile.LastUpdatedTime == CampaignTime.Zero)
                    {
                        profile.LastUpdatedTime = CampaignTime.Now;
                    }
                }
            }
        }

        public override void Cleanup()
        {
            lock (_stateLock)
            {
                CampaignEvents.MapEventEnded.ClearListeners(this);
                CampaignEvents.MapEventStarted.ClearListeners(this);
                EventBus.Instance.Unsubscribe<ThreatLevelChangedEvent>(OnThreatLevelChanged);
                MilitiaEquipmentManager.Instance.ResetMissionEquipmentPolicies();

                _profilesByWarlord.Clear();
                _doctrineShifts = 0;
                _adaptationUpdates = 0;
                _observedDoctrineSamples = 0;
                _loggedDoctrineSamples = 0;
                _observedBattleSamples = 0;
                _loggedBattleSamples = 0;
                _isInitialized = false;
            }
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled || !_isInitialized)
                return;

            if (CompatibilityLayer.IsGameplayActivationDelayed())
                return;

            lock (_stateLock)
            {
                RecomputeDoctrineProfiles();
            }
        }

        public override void SyncData(IDataStore dataStore)
        {
            lock (_stateLock)
            {
                _ = dataStore.SyncData("_adaptiveDoctrineProfiles_v1", ref _profilesByWarlord);
                _ = dataStore.SyncData("_adaptiveDoctrineShifts_v1", ref _doctrineShifts);
                _ = dataStore.SyncData("_adaptiveDoctrineUpdates_v1", ref _adaptationUpdates);
                _ = dataStore.SyncData("_adaptiveDoctrineObservedSamples_v1", ref _observedDoctrineSamples);
                _ = dataStore.SyncData("_adaptiveDoctrineLoggedSamples_v1", ref _loggedDoctrineSamples);
                _ = dataStore.SyncData("_adaptiveDoctrineObservedBattleSamples_v1", ref _observedBattleSamples);
                _ = dataStore.SyncData("_adaptiveDoctrineLoggedBattleSamples_v1", ref _loggedBattleSamples);

                if (dataStore.IsLoading)
                {
                    EnsureGlobalProfile();
                }
            }
        }

        public override string GetDiagnostics()
        {
            if (!_isInitialized)
                return $"{ModuleName} (Not Initialized)";

            lock (_stateLock)
            {
                int profileCount = _profilesByWarlord.Count - (_profilesByWarlord.ContainsKey(GLOBAL_PROFILE_ID) ? 1 : 0);
                float avgConfidence = _profilesByWarlord.Count > 0
                    ? _profilesByWarlord.Values.Average(p => p.Confidence)
                    : 0f;

                return "AdaptiveAIDoctrineSystem:\n" +
                       $"  Profiles: {profileCount}\n" +
                       $"  Doctrine Shifts: {_doctrineShifts}\n" +
                       $"  Adaptation Updates: {_adaptationUpdates}\n" +
                       $"  Doctrine Samples: {_loggedDoctrineSamples}/{_observedDoctrineSamples}\n" +
                       $"  Battle Samples: {_loggedBattleSamples}/{_observedBattleSamples}\n" +
                       $"  Avg Confidence: {avgConfidence:F2}\n" +
                       $"  Global Doctrine: {GetGlobalProfile().ActiveCounterDoctrine}";
            }
        }

        public float GetDecisionModifier(MobileParty party, AIDecisionType decisionType)
        {
            if (!IsEnabled || party == null)
                return 0f;

            lock (_stateLock)
            {
                AdaptiveDoctrineProfile profile = ResolveProfile(party);
                bool isRaider = party.PartyComponent is MilitiaPartyComponent comp
                                && comp.Role == MilitiaPartyComponent.MilitiaRole.Raider;

                float doctrineMod = AdaptiveDoctrineRules.GetDecisionModifier(profile.ActiveCounterDoctrine, decisionType, isRaider);
                float finalMod = doctrineMod + profile.AggressionBias;

                // FIX: If Warlord is poor, boost Raid/Aggression to overcome stagnation
                if (decisionType == AIDecisionType.Raid || decisionType == AIDecisionType.Engage)
                {
                    Warlord? warlord = WarlordSystem.Instance.GetWarlord(profile.WarlordId);
                    if (warlord != null && warlord.Gold < 15000)
                    {
                        finalMod += 10f; // High desperation for gold
                    }
                }

                return finalMod;
            }
        }

        public float GetOverwhelmingThreatRatio(MobileParty party, float baseRatio)
        {
            if (!IsEnabled || party == null)
                return baseRatio;

            lock (_stateLock)
            {
                AdaptiveDoctrineProfile profile = ResolveProfile(party);
                float ratio = baseRatio * AdaptiveDoctrineRules.GetThreatRatioMultiplier(profile.ActiveCounterDoctrine);
                return MathF.Clamp(ratio, 1.1f, 2.6f);
            }
        }

        public float GetChaseDistanceMultiplier(MobileParty party)
        {
            if (!IsEnabled || party == null)
                return 1f;

            lock (_stateLock)
            {
                AdaptiveDoctrineProfile profile = ResolveProfile(party);
                bool isRaider = party.PartyComponent is MilitiaPartyComponent comp
                                && comp.Role == MilitiaPartyComponent.MilitiaRole.Raider;

                return AdaptiveDoctrineRules.GetChaseDistanceMultiplier(profile.ActiveCounterDoctrine, isRaider);
            }
        }

        public AIDecisionType AdaptLearningDecision(MobileParty party, AIDecisionType input)
        {
            if (!IsEnabled || party == null)
                return input;

            lock (_stateLock)
            {
                CounterDoctrine doctrine = ResolveProfile(party).ActiveCounterDoctrine;
                return doctrine switch
                {
                    CounterDoctrine.DefensiveDepth when input == AIDecisionType.Raid => AIDecisionType.Defend,
                    CounterDoctrine.DefensiveDepth when input == AIDecisionType.Engage => AIDecisionType.Patrol,
                    CounterDoctrine.ShockRaid when input == AIDecisionType.Patrol => AIDecisionType.Raid,
                    CounterDoctrine.FastFlank when input == AIDecisionType.Patrol => AIDecisionType.Ambush,
                    _ => input
                };
            }
        }

        public string DescribeDoctrine(MobileParty party)
        {
            if (party == null)
                return CounterDoctrine.Balanced.ToString();

            lock (_stateLock)
            {
                return ResolveProfile(party).ActiveCounterDoctrine.ToString();
            }
        }

        private void RecomputeDoctrineProfiles()
        {
            EnsureGlobalProfile();

            var tracker = PlayerTracker.Instance;
            PlayStyle style = tracker.GetPlayerPlayStyle();
            float threatLevel = tracker.GetThreatLevel();

            // FIX: WarlordTacticsSystem.Instance null olabilir (erken init, sandbox veya geç yükleme).
            // Null durumunda ArmyComposition.Default döndür — sıfır oranlar → Balanced doctrine.
            var tacticsSystem = Systems.Enhancement.WarlordTacticsSystem.Instance;
            var playerArmy = tacticsSystem != null
                ? tacticsSystem.AnalyzePlayerArmy()
                : ArmyComposition.Default;
            float total = Math.Max(1, playerArmy.TotalCount);
            float infantryRatio = playerArmy.InfantryCount / total;
            float rangedRatio = (playerArmy.RangedCount + playerArmy.HorseArcherCount * 0.5f) / total;
            float cavalryRatio = (playerArmy.CavalryCount + playerArmy.HorseArcherCount * 0.5f) / total;

            PlayerCombatDoctrine observed = AdaptiveDoctrineRules.InferPlayerDoctrine(infantryRatio, rangedRatio, cavalryRatio);
            ApplyDoctrineUpdate(GetGlobalProfile(), observed, style, PersonalityType.Cunning, threatLevel, isGlobalProfile: true, BanditMilitias.Systems.Progression.LegitimacyLevel.Warlord);

            var warlords = WarlordSystem.Instance.GetAllWarlords(); // GetAllWarlords zaten IsAlive filtreli

            HashSet<string> activeIds = new HashSet<string>(warlords.Select(w => w.StringId));
            RemoveStaleProfiles(activeIds);

            foreach (var warlord in warlords)
            {
                AdaptiveDoctrineProfile profile = GetOrCreateProfile(warlord.StringId);
                var wLevel = BanditMilitias.Systems.Progression.WarlordLegitimacySystem.Instance.GetLevel(warlord.StringId);
                ApplyDoctrineUpdate(profile, observed, style, warlord.Personality, threatLevel, isGlobalProfile: false, wLevel);
            }
        }

        private void ApplyDoctrineUpdate(
            AdaptiveDoctrineProfile profile,
            PlayerCombatDoctrine observed,
            PlayStyle style,
            PersonalityType personality,
            float threatLevel,
            bool isGlobalProfile,
            BanditMilitias.Systems.Progression.LegitimacyLevel warlordLevel)
        {
            profile.ObservedPlayerDoctrine = observed;
            profile.LastUpdatedTime = CampaignTime.Now;

            CounterDoctrine candidate = AdaptiveDoctrineRules.DetermineCounterDoctrine(observed, style, personality, threatLevel, warlordLevel);
            float cooldownHours = Settings.Instance?.AdaptiveDoctrineSwitchCooldownHours ?? 36;
            float hoursSinceSwitch = profile.LastSwitchTime == CampaignTime.Zero
                ? float.MaxValue
                : (float)(CampaignTime.Now - profile.LastSwitchTime).ToHours;

            bool switched = AdaptiveDoctrineRules.ShouldSwitchDoctrine(
                profile.ActiveCounterDoctrine,
                candidate,
                profile.Confidence,
                hoursSinceSwitch,
                cooldownHours);

            CounterDoctrine oldDoctrine = profile.ActiveCounterDoctrine;
            if (switched)
            {
                profile.ActiveCounterDoctrine = candidate;
                profile.LastSwitchTime = CampaignTime.Now;
                _doctrineShifts++;

                if (!isGlobalProfile)
                {
                    PublishDoctrineShift(profile.WarlordId, oldDoctrine, candidate, profile.Confidence);
                }
            }

            float manualAggressionBias = Settings.Instance?.AdaptiveDoctrineAggressionBias ?? 0f;
            float tierAgressionBonus = warlordLevel switch
            {
                LegitimacyLevel.Outlaw => -3f,
                LegitimacyLevel.Rebel => -1f,
                LegitimacyLevel.FamousBandit => 0f,
                LegitimacyLevel.Warlord => 1.5f,
                LegitimacyLevel.Recognized => 4f,
                _ => 0f
            };

            profile.AggressionBias = MathF.Clamp(
                ComputeAggressionBias(profile.ActiveCounterDoctrine, style, threatLevel, profile.Confidence) + manualAggressionBias + tierAgressionBonus,
                -8f,
                8f);

            _adaptationUpdates++;
            _observedDoctrineSamples++;
        }

        // FIX-GHOST: Bu metot MapEventStarted'a abone edildi (Initialize'da).
        // Her savaş başladığında, bandit partisinin aktif doktrini MilitiaEquipmentManager'a
        // iletiliyor — WarlordEquipmentMissionBehavior agent spawn ederken bunu okuyor.
        private void OnMapEventStarted(MapEvent mapEvent, PartyBase attackerParty, PartyBase defenderParty)
        {
            if (!IsEnabled || !_isInitialized || mapEvent == null) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;

            lock (_stateLock)
            {
                // Saldırgan tarafta milisya parti var mı?
                MobileParty? militiaParty = null;
                foreach (var p in mapEvent.AttackerSide.Parties)
                {
                    if (p?.Party?.MobileParty?.PartyComponent is MilitiaPartyComponent)
                    {
                        militiaParty = p.Party.MobileParty;
                        break;
                    }
                }
                // Yoksa savunucu tarafta ara
                if (militiaParty == null)
                {
                    foreach (var p in mapEvent.DefenderSide.Parties)
                    {
                        if (p?.Party?.MobileParty?.PartyComponent is MilitiaPartyComponent)
                        {
                            militiaParty = p.Party.MobileParty;
                            break;
                        }
                    }
                }

                if (militiaParty == null) return;

                CounterDoctrine doctrine = ResolveProfile(militiaParty).ActiveCounterDoctrine;
                MilitiaEquipmentManager.Instance.ApplyMissionEquipmentPolicy(militiaParty, doctrine);
            }
        }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            if (!IsEnabled || !_isInitialized || mapEvent == null)
                return;
            if (CompatibilityLayer.IsGameplayActivationDelayed())
                return;

            lock (_stateLock)
            {
                foreach (var involved in mapEvent.InvolvedParties)
                {
                    if (involved?.MobileParty?.PartyComponent is MilitiaPartyComponent)
                    {
                        MilitiaEquipmentManager.Instance.ClearMissionEquipmentPolicy(involved.MobileParty);
                    }
                }

                float learningRate = Settings.Instance?.AdaptiveDoctrineLearningRate ?? 0.30f;
                EnsureGlobalProfile();

                foreach (var involved in mapEvent.InvolvedParties)
                {
                    MobileParty party = involved.MobileParty;
                    if (party?.PartyComponent is not MilitiaPartyComponent)
                        continue;

                    Warlord? warlord = WarlordSystem.Instance.GetWarlordForParty(party);
                    string profileId = warlord?.StringId ?? GLOBAL_PROFILE_ID;
                    AdaptiveDoctrineProfile profile = GetOrCreateProfile(profileId);

                    bool militiaIsAttacker = mapEvent.AttackerSide.Parties.Any(p => p.Party.MobileParty == party);
                    bool militiaIsDefender = mapEvent.DefenderSide.Parties.Any(p => p.Party.MobileParty == party);
                    if (!militiaIsAttacker && !militiaIsDefender)
                        continue;

                    bool won = militiaIsAttacker
                        ? mapEvent.WinningSide == BattleSideEnum.Attacker
                        : mapEvent.WinningSide == BattleSideEnum.Defender;

                    float confidenceBefore = profile.Confidence;
                    profile.Confidence = AdaptiveDoctrineRules.UpdateConfidence(profile.Confidence, won, learningRate);
                    profile.LastUpdatedTime = CampaignTime.Now;

                    if (won) profile.SuccessfulEngagements++;
                    else profile.FailedEngagements++;

                    _observedBattleSamples++;
                }
            }
        }

        private void OnThreatLevelChanged(ThreatLevelChangedEvent evt)
        {
            if (!IsEnabled || !_isInitialized || evt == null)
                return;

            lock (_stateLock)
            {
                if (evt.NewThreatLevel < 2.0f)
                    return;

                foreach (var profile in _profilesByWarlord.Values)
                {
                    if (profile.ActiveCounterDoctrine == CounterDoctrine.DefensiveDepth)
                        continue;

                    profile.ActiveCounterDoctrine = CounterDoctrine.DefensiveDepth;
                    profile.LastSwitchTime = CampaignTime.Now;
                    profile.Confidence = Math.Max(profile.Confidence, 0.55f);
                    _doctrineShifts++;
                }
            }
        }

        public AdaptiveDoctrineProfile GetProfileForWarlord(MobileParty party)
        {
            lock (_stateLock)
            {
                return ResolveProfile(party);
            }
        }

        private AdaptiveDoctrineProfile ResolveProfile(MobileParty party)
        {
            if (party?.PartyComponent is MilitiaPartyComponent component)
            {
                if (component.WarlordId is string key && !string.IsNullOrWhiteSpace(key))
                {
                    if (_profilesByWarlord.TryGetValue(key, out var byId))
                    {
                        return byId;
                    }
                }

                Warlord? warlord = WarlordSystem.Instance.GetWarlordForParty(party);
                if (warlord != null)
                {
                    return GetOrCreateProfile(warlord.StringId);
                }
            }

            return GetGlobalProfile();
        }

        private void RemoveStaleProfiles(HashSet<string> aliveWarlordIds)
        {
            List<string> toRemove = _profilesByWarlord.Keys
                .Where(k => k != GLOBAL_PROFILE_ID && !aliveWarlordIds.Contains(k))
                .ToList();

            foreach (var key in toRemove)
            {
                _ = _profilesByWarlord.Remove(key);
            }
        }

        private AdaptiveDoctrineProfile GetOrCreateProfile(string warlordId)
        {
            if (string.IsNullOrWhiteSpace(warlordId))
                return GetGlobalProfile();

            if (_profilesByWarlord.TryGetValue(warlordId, out var existing))
                return existing;

            var profile = new AdaptiveDoctrineProfile
            {
                WarlordId = warlordId,
                ObservedPlayerDoctrine = PlayerCombatDoctrine.Unknown,
                ActiveCounterDoctrine = CounterDoctrine.Balanced,
                Confidence = 0.55f,
                LastUpdatedTime = Campaign.Current != null ? CampaignTime.Now : CampaignTime.Zero
            };

            _profilesByWarlord[warlordId] = profile;
            return profile;
        }

        private void EnsureGlobalProfile()
        {
            if (_profilesByWarlord.ContainsKey(GLOBAL_PROFILE_ID))
                return;

            // FIX: CampaignTime.Now crash prevention.
            var safeTime = Campaign.Current != null ? CampaignTime.Now : CampaignTime.Zero;
            _profilesByWarlord[GLOBAL_PROFILE_ID] = new AdaptiveDoctrineProfile
            {
                WarlordId = GLOBAL_PROFILE_ID,
                ActiveCounterDoctrine = CounterDoctrine.Balanced,
                Confidence = 0.50f,
                LastUpdatedTime = safeTime
            };
        }

        private AdaptiveDoctrineProfile GetGlobalProfile()
        {
            EnsureGlobalProfile();
            return _profilesByWarlord[GLOBAL_PROFILE_ID];
        }

        private static float ComputeAggressionBias(CounterDoctrine doctrine, PlayStyle style, float threatLevel, float confidence)
        {
            float bias = doctrine switch
            {
                CounterDoctrine.ShockRaid => 3.5f,
                CounterDoctrine.FastFlank => 2.5f,
                CounterDoctrine.HarassScreen => 1.5f,
                CounterDoctrine.SpearWall => 0.5f,
                CounterDoctrine.DefensiveDepth => -3.5f,
                _ => 0f
            };

            // FIX-8: Değişken Agresiflik Yapısı (Confidence tabanlı)
            // Confidence (0.05 - 1.0) -> Düşük güven = daha temkinli (-), Yüksek güven = daha riskli (+)
            float confidenceMod = (confidence - 0.5f) * 5.0f; 
            bias += confidenceMod;

            if (style == PlayStyle.Aggressive) bias += 1.0f;
            if (style == PlayStyle.Defensive) bias -= 1.0f;
            if (threatLevel >= 2.0f) bias -= 2.0f;

            return bias;
        }

        private static void PublishDoctrineShift(string warlordId, CounterDoctrine oldDoctrine, CounterDoctrine newDoctrine, float confidence)
        {
            var warlord = WarlordSystem.Instance.GetWarlord(warlordId);
            if (warlord == null)
                return;

            var evt = EventBus.Instance.Get<AdaptiveDoctrineShiftedEvent>();
            evt.Warlord = warlord;
            evt.OldDoctrine = oldDoctrine.ToString();
            evt.NewDoctrine = newDoctrine.ToString();
            evt.Confidence = confidence;
            evt.ChangedAt = CampaignTime.Now;

            NeuralEventRouter.Instance.Publish(evt);
            EventBus.Instance.Return(evt);
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("doctrine_status", "militia")]
        public static string CommandDoctrineStatus(List<string> args)
        {
            var instance = ModuleManager.Instance.GetModule<AdaptiveAIDoctrineSystem>();
            if (instance == null)
            {
                return "AdaptiveAIDoctrineSystem module not found.";
            }

            return instance.GetDiagnostics() + "\n" + AdaptiveDoctrineDataLogger.GetDiagnostics();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("doctrine_export", "militia")]
        public static string CommandDoctrineExport(List<string> args)
        {
            var instance = ModuleManager.Instance.GetModule<AdaptiveAIDoctrineSystem>();
            if (instance == null)
            {
                return "AdaptiveAIDoctrineSystem module not found.";
            }

            List<AdaptiveDoctrineProfile> snapshot;
            lock (instance._stateLock)
            {
                snapshot = instance._profilesByWarlord.Values.ToList();
            }

            AdaptiveDoctrineDataLogger.ExportProfilesSnapshot(snapshot);
            return $"Doctrine snapshot exported to: {AdaptiveDoctrineDataLogger.SnapshotPath}";
        }
    }

    // ── AdaptiveDoctrineRules (inline) ─────────────────────

    public static class AdaptiveDoctrineRules
    {
        public static PlayerCombatDoctrine InferPlayerDoctrine(float infantryRatio, float rangedRatio, float cavalryRatio)
        {
            float inf = Clamp01(infantryRatio);
            float rng = Clamp01(rangedRatio);
            float cav = Clamp01(cavalryRatio);

            if (cav >= 0.45f)
                return PlayerCombatDoctrine.CavalryShock;

            if (rng >= 0.45f)
                return PlayerCombatDoctrine.RangedSkirmish;

            if (inf >= 0.55f)
                return PlayerCombatDoctrine.InfantryWall;

            if (inf + rng + cav <= 0.20f)
                return PlayerCombatDoctrine.Unknown;

            return PlayerCombatDoctrine.MixedArms;
        }

        public static CounterDoctrine DetermineCounterDoctrine(
            PlayerCombatDoctrine observedDoctrine,
            PlayStyle playStyle,
            PersonalityType personality,
            float threatLevel,
            LegitimacyLevel warlordLevel)
        {
            // Tier 1: Outlaws have no tactical capability, always default to basic.
            if (warlordLevel == LegitimacyLevel.Outlaw)
                return CounterDoctrine.Balanced;

            if (threatLevel >= 2.20f)
                return CounterDoctrine.DefensiveDepth;

            var ideal = observedDoctrine switch
            {
                PlayerCombatDoctrine.CavalryShock => personality switch
                {
                    PersonalityType.Aggressive => CounterDoctrine.DoubleSquare,
                    PersonalityType.Cunning => CounterDoctrine.Turan,
                    _ => CounterDoctrine.SpearWall
                },
                PlayerCombatDoctrine.RangedSkirmish => personality switch
                {
                    PersonalityType.Cautious => CounterDoctrine.HarassScreen,
                    PersonalityType.Cunning => CounterDoctrine.Killbox,
                    _ => CounterDoctrine.FastFlank
                },
                PlayerCombatDoctrine.InfantryWall => personality switch
                {
                    PersonalityType.Aggressive => CounterDoctrine.Wedge,
                    PersonalityType.Cunning => CounterDoctrine.FeignedRetreat,
                    _ => CounterDoctrine.HarassScreen
                },
                PlayerCombatDoctrine.MixedArms => ResolveMixedArmsCounter(playStyle, personality),
                PlayerCombatDoctrine.Unknown => personality switch
                {
                    PersonalityType.Aggressive => CounterDoctrine.ShockRaid,
                    PersonalityType.Cautious => CounterDoctrine.DefensiveDepth,
                    PersonalityType.Cunning => CounterDoctrine.RefusedFlank,
                    _ => CounterDoctrine.Balanced
                },
                _ => CounterDoctrine.Balanced
            };

            // Tier 2: Rebels can only use basic guerrilla tactics, not advanced formations.
            if (warlordLevel == LegitimacyLevel.Rebel)
            {
                if (ideal == CounterDoctrine.SpearWall || ideal == CounterDoctrine.ShockRaid)
                    return CounterDoctrine.Balanced;
            }

            // Tier 3: Warlords are smart but not coordinated enough for ShockRaid
            if (warlordLevel == LegitimacyLevel.FamousBandit)
            {
                if (ideal == CounterDoctrine.ShockRaid)
                    return CounterDoctrine.Balanced;
            }
 
            // Tier 3+ (Warlord, Recognized, TheConqueror) have access to ALL doctrines.
            return ideal;
        }

        public static float GetDecisionModifier(CounterDoctrine doctrine, AIDecisionType decision, bool isRaider)
        {
            float baseMod = doctrine switch
            {
                CounterDoctrine.SpearWall => decision switch
                {
                    AIDecisionType.Engage => 12f,
                    AIDecisionType.Defend => 8f,
                    AIDecisionType.Raid => -8f,
                    _ => 0f
                },
                CounterDoctrine.FastFlank => decision switch
                {
                    AIDecisionType.Engage => 9f,
                    AIDecisionType.Ambush => 10f,
                    AIDecisionType.Raid => 4f,
                    AIDecisionType.Flee => -5f,
                    _ => 0f
                },
                CounterDoctrine.HarassScreen => decision switch
                {
                    AIDecisionType.Raid => 12f,
                    AIDecisionType.Patrol => 6f,
                    AIDecisionType.Engage => -4f,
                    _ => 0f
                },
                CounterDoctrine.DefensiveDepth => decision switch
                {
                    AIDecisionType.Defend => 14f,
                    AIDecisionType.Retreat => 10f,
                    AIDecisionType.Flee => 9f,
                    AIDecisionType.Engage => -10f,
                    _ => 0f
                },
                CounterDoctrine.ShockRaid => decision switch
                {
                    AIDecisionType.Raid => 15f,
                    AIDecisionType.Engage => 7f,
                    AIDecisionType.Defend => -6f,
                    _ => 0f
                },
                _ => decision switch
                {
                    AIDecisionType.Patrol => 2f,
                    _ => 0f
                }
            };

            if (!isRaider && decision == AIDecisionType.Raid)
                baseMod -= 4f;

            return baseMod;
        }

        public static float GetThreatRatioMultiplier(CounterDoctrine doctrine)
        {
            return doctrine switch
            {
                CounterDoctrine.DefensiveDepth => 0.82f,
                CounterDoctrine.SpearWall => 0.95f,
                CounterDoctrine.FastFlank => 1.08f,
                CounterDoctrine.ShockRaid => 1.15f,
                _ => 1.00f
            };
        }

        public static float GetChaseDistanceMultiplier(CounterDoctrine doctrine, bool isRaider)
        {
            return doctrine switch
            {
                CounterDoctrine.FastFlank => 1.30f,
                CounterDoctrine.ShockRaid => 1.45f,
                CounterDoctrine.HarassScreen => 1.20f,
                CounterDoctrine.DefensiveDepth => 0.70f,
                _ => 1.00f
            };
        }

        public static float UpdateConfidence(float current, bool won, float learningRate)
        {
            float target = won ? 1.0f : 0.1f;
            float delta = (target - current) * learningRate;
            return MathF.Clamp(current + delta, 0.05f, 1.0f);
        }

        public static bool ShouldSwitchDoctrine(
            CounterDoctrine current,
            CounterDoctrine candidate,
            float confidence,
            float hoursSinceSwitch,
            float cooldownHours)
        {
            if (current == candidate) return false;
            if (hoursSinceSwitch < cooldownHours) return false;

            // Confidence drops below 40% -> switch faster
            if (confidence < 0.40f) return true;

            // Normal operation -> switch if confidence is high enough to risk it
            return confidence > 0.55f;
        }

        private static CounterDoctrine ResolveMixedArmsCounter(PlayStyle style, PersonalityType personality)
        {
            if (style == PlayStyle.Aggressive) return CounterDoctrine.ShockRaid;
            if (personality == PersonalityType.Cautious) return CounterDoctrine.SpearWall;
            return CounterDoctrine.Balanced;
        }

        private static float Clamp01(float v) => MathF.Clamp(v, 0f, 1f);
    }
}
