using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Bounty;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.SaveSystem;
using TaleWorlds.Library;
using MathF = TaleWorlds.Library.MathF;

namespace BanditMilitias.Systems.Diplomacy
{
    [Serializable]
    public class WarlordPoliticalProfile
    {
        [SaveableProperty(1)]
        public string WarlordId { get; set; } = string.Empty;

        [SaveableProperty(2)]
        public float Influence { get; set; } = 25f;

        [SaveableProperty(3)]
        public float Paranoia { get; set; } = 0.25f;

        [SaveableProperty(4)]
        public int AlliancesFormed { get; set; }

        [SaveableProperty(5)]
        public int BetrayalsCommitted { get; set; }

        [SaveableProperty(6)]
        public CampaignTime LastPoliticalActionTime { get; set; } = CampaignTime.Zero;
    }

    [Serializable]
    public class WarlordPoliticalRelation
    {
        [SaveableProperty(1)]
        public string PairKey { get; set; } = string.Empty;

        [SaveableProperty(2)]
        public string WarlordAId { get; set; } = string.Empty;

        [SaveableProperty(3)]
        public string WarlordBId { get; set; } = string.Empty;

        [SaveableProperty(4)]
        public float Score { get; set; }

        [SaveableProperty(5)]
        public bool IsAllied { get; set; }

        [SaveableProperty(6)]
        public bool IsRival { get; set; }

        [SaveableProperty(7)]
        public int CooperationPoints { get; set; }

        [SaveableProperty(8)]
        public int ConflictPoints { get; set; }

        [SaveableProperty(9)]
        public CampaignTime LastUpdated { get; set; } = CampaignTime.Zero;
    }

    [AutoRegister]
    public class BanditPoliticsSystem : MilitiaModuleBase
    {
        public override string ModuleName => "BanditPoliticsSystem";
        public override bool IsEnabled => (Settings.Instance?.EnableWarlords ?? true) && (Settings.Instance?.EnableBanditPolitics ?? true);
        public override int Priority => 57;

        private static readonly Lazy<BanditPoliticsSystem> _instance =
            new Lazy<BanditPoliticsSystem>(() => new BanditPoliticsSystem());

        public static BanditPoliticsSystem Instance => _instance.Value;

        private Dictionary<string, WarlordPoliticalProfile> _profiles = new Dictionary<string, WarlordPoliticalProfile>();
        private Dictionary<string, WarlordPoliticalRelation> _relations = new Dictionary<string, WarlordPoliticalRelation>();

        private readonly object _stateLock = new object();
        private bool _isInitialized;

        private int _alliancesFormed;
        private int _rivalriesDeclared;
        private int _betrayalsTriggered;

        private BanditPoliticsSystem() { }

        public override void Initialize()
        {
            lock (_stateLock)
            {
                if (_isInitialized)
                    return;

                EventBus.Instance.Subscribe<MilitiaRaidCompletedEvent>(OnMilitiaRaidCompleted);
                EventBus.Instance.Subscribe<MilitiaKilledEvent>(OnMilitiaKilled);
                EventBus.Instance.Subscribe<HideoutClearedEvent>(OnHideoutCleared);
                EventBus.Instance.Subscribe<WarlordLevelChangedEvent>(OnWarlordLevelChanged);

                _isInitialized = true;
            }
        }

        public override void Cleanup()
        {
            lock (_stateLock)
            {
                EventBus.Instance.Unsubscribe<MilitiaRaidCompletedEvent>(OnMilitiaRaidCompleted);
                EventBus.Instance.Unsubscribe<MilitiaKilledEvent>(OnMilitiaKilled);
                EventBus.Instance.Unsubscribe<HideoutClearedEvent>(OnHideoutCleared);
                EventBus.Instance.Unsubscribe<WarlordLevelChangedEvent>(OnWarlordLevelChanged);

                _profiles.Clear();
                _relations.Clear();
                _alliancesFormed = 0;
                _rivalriesDeclared = 0;
                _betrayalsTriggered = 0;
                _isInitialized = false;
            }
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled || !_isInitialized)
                return;

            lock (_stateLock)
            {
                List<Warlord> warlords = WarlordSystem.Instance
                    .GetAllWarlords()
                    .Where(w => w != null && w.IsAlive)
                    .ToList();

                EnsureProfilesAndRelations(warlords);
                EvaluatePoliticalLandscape(warlords);
            }
        }

        public override void SyncData(IDataStore dataStore)
        {
            lock (_stateLock)
            {
                _ = dataStore.SyncData("_banditPoliticsProfiles_v1", ref _profiles);
                _ = dataStore.SyncData("_banditPoliticsRelations_v1", ref _relations);
                _ = dataStore.SyncData("_banditPoliticsAlliances_v1", ref _alliancesFormed);
                _ = dataStore.SyncData("_banditPoliticsRivalries_v1", ref _rivalriesDeclared);
                _ = dataStore.SyncData("_banditPoliticsBetrayals_v1", ref _betrayalsTriggered);

                if (dataStore.IsLoading)
                {
                    _profiles ??= new Dictionary<string, WarlordPoliticalProfile>();
                    _relations ??= new Dictionary<string, WarlordPoliticalRelation>();
                }
            }
        }

        public override string GetDiagnostics()
        {
            lock (_stateLock)
            {
                int activeAlliances = _relations.Values.Count(r => r.IsAllied);
                int activeRivalries = _relations.Values.Count(r => r.IsRival);
                float avgInfluence = _profiles.Count > 0 ? _profiles.Values.Average(p => p.Influence) : 0f;

                return "BanditPoliticsSystem:\n" +
                       $"  Profiles: {_profiles.Count}\n" +
                       $"  Relations: {_relations.Count}\n" +
                       $"  Active Alliances: {activeAlliances}\n" +
                       $"  Active Rivalries: {activeRivalries}\n" +
                       $"  Alliances Formed: {_alliancesFormed}\n" +
                       $"  Rivalries Declared: {_rivalriesDeclared}\n" +
                       $"  Betrayals Triggered: {_betrayalsTriggered}\n" +
                       $"  Avg Influence: {avgInfluence:F1}";
            }
        }

        private void EvaluatePoliticalLandscape(List<Warlord> warlords)
        {
            if (warlords.Count < 2)
                return;

            float driftRate = Settings.Instance?.PoliticsDailyDrift ?? 0.04f;
            float allianceThreshold = Settings.Instance?.PoliticsAllianceThreshold ?? 40f;
            float rivalryThreshold = Settings.Instance?.PoliticsRivalryThreshold ?? -35f;
            float betrayalThreshold = Settings.Instance?.PoliticsBetrayalThreshold ?? -55f;

            for (int i = 0; i < warlords.Count - 1; i++)
            {
                for (int j = i + 1; j < warlords.Count; j++)
                {
                    Warlord first = warlords[i];
                    Warlord second = warlords[j];
                    WarlordPoliticalRelation relation = GetOrCreateRelation(first, second);

                    relation.Score = BanditPoliticsRules.ClampRelation(
                        relation.Score + BanditPoliticsRules.GetDailyNeutralDrift(relation.Score, driftRate));
                    relation.LastUpdated = CampaignTime.Now;

                    if (BanditPoliticsRules.ShouldFormAlliance(
                            relation.Score,
                            relation.IsAllied,
                            relation.IsRival,
                            first.CommandedMilitias.Count,
                            second.CommandedMilitias.Count,
                            allianceThreshold))
                    {
                        FormAlliance(first, second, relation);
                        continue;
                    }

                    if (BanditPoliticsRules.ShouldDeclareRivalry(
                            relation.Score,
                            relation.IsAllied,
                            relation.IsRival,
                            rivalryThreshold))
                    {
                        DeclareRivalry(first, second, relation);
                        continue;
                    }

                    if (relation.IsAllied)
                    {
                        TryTriggerBetrayal(first, second, relation, betrayalThreshold);
                    }
                    else if (relation.IsRival && relation.Score > rivalryThreshold + 20f)
                    {
                        relation.IsRival = false;
                        relation.ConflictPoints = Math.Max(0, relation.ConflictPoints - 1);
                    }
                }
            }
        }

        private void FormAlliance(Warlord first, Warlord second, WarlordPoliticalRelation relation)
        {
            relation.IsAllied = true;
            relation.IsRival = false;
            relation.Score = BanditPoliticsRules.ClampRelation(relation.Score + 8f);
            relation.CooperationPoints += 2;
            relation.LastUpdated = CampaignTime.Now;

            if (_profiles.TryGetValue(first.StringId, out var firstProfile))
            {
                firstProfile.AlliancesFormed++;
                firstProfile.LastPoliticalActionTime = CampaignTime.Now;
            }

            if (_profiles.TryGetValue(second.StringId, out var secondProfile))
            {
                secondProfile.AlliancesFormed++;
                secondProfile.LastPoliticalActionTime = CampaignTime.Now;
            }

            ApplyAllianceSupport(first, second);

            _alliancesFormed++;
            PublishAllianceEvent(first, second, relation.Score);

            // Oyuncuya görünür bildirim — yakın warlordlar ittifak kurdu
            try
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Siyaset] {first.FullName} ve {second.FullName} ittifak kurdu!",
                    new TaleWorlds.Library.Color(0.4f, 0.8f, 1.0f)));
            }
            catch { }

            first.IssueOrderToMilitias(new StrategicCommand
            {
                Type = CommandType.Defend,
                Priority = 0.65f,
                Reason = $"Alliance with {second.Name} formed. Stabilize regional control."
            });

            second.IssueOrderToMilitias(new StrategicCommand
            {
                Type = CommandType.Defend,
                Priority = 0.65f,
                Reason = $"Alliance with {first.Name} formed. Stabilize regional control."
            });
        }

        private void DeclareRivalry(Warlord first, Warlord second, WarlordPoliticalRelation relation)
        {
            relation.IsRival = true;
            relation.IsAllied = false;
            relation.Score = BanditPoliticsRules.ClampRelation(relation.Score - 6f);
            relation.ConflictPoints += 2;
            relation.LastUpdated = CampaignTime.Now;

            _rivalriesDeclared++;
            PublishRivalryEvent(first, second, relation.Score);

            // Oyuncuya görünür bildirim — rekabet ilan edildi
            try
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Siyaset] {first.FullName} ve {second.FullName} arasında kan davası başladı!",
                    new TaleWorlds.Library.Color(1.0f, 0.35f, 0.2f)));
            }
            catch { }

            if (ModuleAccess.TryGetEnabled<BountySystem>(out var bountySystem))
            {
                bountySystem.AddBounty(first, 120, BountySource.Vendetta);
                bountySystem.AddBounty(second, 120, BountySource.Vendetta);
            }

            first.IssueOrderToMilitias(new StrategicCommand
            {
                Type = CommandType.Hunt,
                Priority = 0.75f,
                Reason = $"Political rivalry with {second.Name}. Pressure enemy influence."
            });
        }

        private void TryTriggerBetrayal(Warlord first, Warlord second, WarlordPoliticalRelation relation, float betrayalThreshold)
        {
            if (!relation.IsAllied || relation.Score > betrayalThreshold)
                return;

            var firstProfile = GetOrCreateProfile(first);
            var secondProfile = GetOrCreateProfile(second);

            float cooperation = Math.Max(1f, relation.CooperationPoints);
            float conflict = Math.Max(0f, relation.ConflictPoints);
            float conflictPressure = conflict / (cooperation + conflict);
            float influenceGap = Math.Abs(firstProfile.Influence - secondProfile.Influence) / 100f;
            float betrayalPressure = MathF.Clamp(conflictPressure * 0.7f + influenceGap * 0.3f, 0f, 1f);

            float betrayalChance = BanditPoliticsRules.ComputeBetrayalChance(
                relation.Score,
                firstProfile.Paranoia,
                secondProfile.Paranoia,
                betrayalPressure);

            if (TaleWorlds.Core.MBRandom.RandomFloat >= betrayalChance)
                return;

            Warlord betrayer = firstProfile.Paranoia >= secondProfile.Paranoia ? first : second;
            Warlord betrayed = ReferenceEquals(betrayer, first) ? second : first;
            WarlordPoliticalProfile betrayerProfile = ReferenceEquals(betrayer, first) ? firstProfile : secondProfile;

            relation.IsAllied = false;
            relation.IsRival = true;
            relation.Score = BanditPoliticsRules.ClampRelation(relation.Score - 15f);
            relation.ConflictPoints += 3;
            relation.LastUpdated = CampaignTime.Now;

            betrayerProfile.BetrayalsCommitted++;
            betrayerProfile.LastPoliticalActionTime = CampaignTime.Now;

            _betrayalsTriggered++;

            if (ModuleAccess.TryGetEnabled<BountySystem>(out var bountySystem))
            {
                bountySystem.AddBounty(betrayer, 300, BountySource.Vendetta);
            }

            PublishBetrayalEvent(betrayer, betrayed, relation.Score);

            betrayer.IssueOrderToMilitias(new StrategicCommand
            {
                Type = CommandType.Hunt,
                Priority = 0.85f,
                Reason = $"Betrayed {betrayed.Name}. Eliminate retaliation risk."
            });

            betrayed.IssueOrderToMilitias(new StrategicCommand
            {
                Type = CommandType.Defend,
                Priority = 0.90f,
                Reason = $"Betrayed by {betrayer.Name}. Consolidate and defend."
            });
        }

        private void ApplyAllianceSupport(Warlord first, Warlord second)
        {
            Warlord donor = first.Gold >= second.Gold ? first : second;
            Warlord receiver = ReferenceEquals(donor, first) ? second : first;

            float supportPool = Math.Max(0f, donor.Gold - 5000f);
            float transfer = Math.Min(400f, supportPool * 0.05f);

            if (transfer < 25f)
                return;

            donor.Gold -= transfer;
            receiver.Gold += transfer;
        }

        private void OnMilitiaRaidCompleted(MilitiaRaidCompletedEvent evt)
        {
            if (!_isInitialized || evt?.RaiderParty == null)
                return;

            lock (_stateLock)
            {
                var raiderWarlord = WarlordSystem.Instance.GetWarlordForParty(evt.RaiderParty);
                if (raiderWarlord == null)
                    return;

                var profile = GetOrCreateProfile(raiderWarlord);

                switch (evt.Outcome)
                {
                    case BanditMilitias.Systems.Raiding.RaidOutcome.Success:
                    case BanditMilitias.Systems.Raiding.RaidOutcome.PartialSuccess:
                        profile.Influence = MathF.Clamp(profile.Influence + 3f, 0f, 100f);
                        break;
                    case BanditMilitias.Systems.Raiding.RaidOutcome.Repelled:
                        profile.Influence = MathF.Clamp(profile.Influence - 2f, 0f, 100f);
                        break;
                }

                foreach (var other in WarlordSystem.Instance.GetAllWarlords())
                {
                    if (other == null || !other.IsAlive || other.StringId == raiderWarlord.StringId)
                        continue;

                    var relation = GetOrCreateRelation(raiderWarlord, other);
                    if (relation.IsAllied)
                    {
                        relation.Score = BanditPoliticsRules.ClampRelation(relation.Score + 1.5f);
                        relation.CooperationPoints++;
                    }
                    else
                    {
                        relation.Score = BanditPoliticsRules.ClampRelation(relation.Score - 0.8f);
                        relation.ConflictPoints++;
                    }
                }
            }
        }

        private void OnMilitiaKilled(MilitiaKilledEvent evt)
        {
            if (!_isInitialized || evt?.Victim == null || evt.Killer == null)
                return;

            lock (_stateLock)
            {
                var victimWarlord = WarlordSystem.Instance.GetWarlordForParty(evt.Victim);
                var killerWarlord = ResolveWarlordFromHero(evt.Killer);

                if (victimWarlord == null || killerWarlord == null || victimWarlord.StringId == killerWarlord.StringId)
                    return;

                var relation = GetOrCreateRelation(victimWarlord, killerWarlord);
                relation.Score = BanditPoliticsRules.ClampRelation(relation.Score - 8f);
                relation.ConflictPoints += 2;
                relation.LastUpdated = CampaignTime.Now;

                GetOrCreateProfile(killerWarlord).Influence = MathF.Clamp(GetOrCreateProfile(killerWarlord).Influence + 2f, 0f, 100f);
                GetOrCreateProfile(victimWarlord).Influence = MathF.Clamp(GetOrCreateProfile(victimWarlord).Influence - 2f, 0f, 100f);
            }
        }

        private void OnHideoutCleared(HideoutClearedEvent evt)
        {
            if (!_isInitialized || evt?.Hideout == null)
                return;

            lock (_stateLock)
            {
                EnsureProfilesAndRelations(WarlordSystem.Instance.GetAllWarlords().Where(w => w.IsAlive).ToList());
            }
        }

        private void OnWarlordLevelChanged(WarlordLevelChangedEvent evt)
        {
            if (!_isInitialized || evt?.Warlord == null)
                return;

            lock (_stateLock)
            {
                var profile = GetOrCreateProfile(evt.Warlord);
                profile.Influence = MathF.Clamp(profile.Influence + 4f, 0f, 100f);
                profile.LastPoliticalActionTime = CampaignTime.Now;
            }
        }

        private void EnsureProfilesAndRelations(List<Warlord> aliveWarlords)
        {
            HashSet<string> aliveIds = new HashSet<string>(aliveWarlords.Select(w => w.StringId));

            foreach (var warlord in aliveWarlords)
            {
                _ = GetOrCreateProfile(warlord);
            }

            var profileKeysToRemove = _profiles.Keys.Where(id => !aliveIds.Contains(id)).ToList();
            foreach (var key in profileKeysToRemove)
            {
                _ = _profiles.Remove(key);
            }

            var relationKeysToRemove = _relations
                .Where(kv => !aliveIds.Contains(kv.Value.WarlordAId) || !aliveIds.Contains(kv.Value.WarlordBId))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in relationKeysToRemove)
            {
                _ = _relations.Remove(key);
            }
        }

        private WarlordPoliticalProfile GetOrCreateProfile(Warlord warlord)
        {
            if (_profiles.TryGetValue(warlord.StringId, out var existing))
                return existing;

            var profile = new WarlordPoliticalProfile
            {
                WarlordId = warlord.StringId,
                Influence = MathF.Clamp(20f + (warlord.Gold / 1500f) + (warlord.CommandedMilitias.Count * 2f), 0f, 100f),
                Paranoia = GetParanoiaForPersonality(warlord.Personality),
                LastPoliticalActionTime = CampaignTime.Now
            };

            _profiles[warlord.StringId] = profile;
            return profile;
        }

        private WarlordPoliticalRelation GetOrCreateRelation(Warlord first, Warlord second)
        {
            string pairKey = BanditPoliticsRules.MakePairKey(first.StringId, second.StringId);
            if (_relations.TryGetValue(pairKey, out var existing))
                return existing;

            float baseline = BanditPoliticsRules.GetPersonalityCompatibility(first.Personality, second.Personality);

            var relation = new WarlordPoliticalRelation
            {
                PairKey = pairKey,
                WarlordAId = first.StringId,
                WarlordBId = second.StringId,
                Score = BanditPoliticsRules.ClampRelation(baseline),
                IsAllied = false,
                IsRival = false,
                CooperationPoints = 0,
                ConflictPoints = 0,
                LastUpdated = CampaignTime.Now
            };

            _relations[pairKey] = relation;
            return relation;
        }

        private Warlord? ResolveWarlordFromHero(Hero hero)
        {
            var byHero = WarlordSystem.Instance.GetWarlordForHero(hero);
            if (byHero != null)
                return byHero;

            MobileParty? heroParty = hero.PartyBelongedTo;
            if (heroParty == null)
                return null;

            return WarlordSystem.Instance.GetWarlordForParty(heroParty);
        }

        private static float GetParanoiaForPersonality(PersonalityType personality)
        {
            return personality switch
            {
                PersonalityType.Vengeful => 0.72f,
                PersonalityType.Cunning => 0.58f,
                PersonalityType.Cautious => 0.52f,
                PersonalityType.Aggressive => 0.38f,
                _ => 0.45f
            };
        }

        private static void PublishAllianceEvent(Warlord first, Warlord second, float score)
        {
            var evt = EventBus.Instance.Get<WarlordAllianceFormedEvent>();
            evt.PrimaryWarlord = first;
            evt.SecondaryWarlord = second;
            evt.RelationScore = score;
            evt.FormedAt = CampaignTime.Now;
            EventBus.Instance.Publish(evt);
            EventBus.Instance.Return(evt);
        }

        private static void PublishRivalryEvent(Warlord first, Warlord second, float score)
        {
            var evt = EventBus.Instance.Get<WarlordRivalryEscalatedEvent>();
            evt.PrimaryWarlord = first;
            evt.SecondaryWarlord = second;
            evt.RelationScore = score;
            evt.EscalatedAt = CampaignTime.Now;
            EventBus.Instance.Publish(evt);
            EventBus.Instance.Return(evt);
        }

        private static void PublishBetrayalEvent(Warlord betrayer, Warlord betrayed, float score)
        {
            var evt = EventBus.Instance.Get<WarlordBackstabEvent>();
            evt.Betrayer = betrayer;
            evt.Betrayed = betrayed;
            evt.RelationScoreAfter = score;
            evt.HappenedAt = CampaignTime.Now;
            EventBus.Instance.Publish(evt);
            EventBus.Instance.Return(evt);
        }
    }

    // ── BanditPoliticsRules (inline) ──────────────────────────────

    public static class BanditPoliticsRules
    {
        public const float MinRelation = -100f;
        public const float MaxRelation = 100f;

        public static string MakePairKey(string firstWarlordId, string secondWarlordId)
        {
            if (string.IsNullOrEmpty(firstWarlordId) || string.IsNullOrEmpty(secondWarlordId))
                return string.Empty;

            return string.CompareOrdinal(firstWarlordId, secondWarlordId) <= 0
                ? firstWarlordId + "|" + secondWarlordId
                : secondWarlordId + "|" + firstWarlordId;
        }

        public static float ClampRelation(float value)
        {
            return (float)Math.Max(MinRelation, Math.Min(MaxRelation, value));
        }

        public static float GetPersonalityCompatibility(PersonalityType a, PersonalityType b)
        {
            if (a == b)
                return 14f;

            if ((a == PersonalityType.Cunning && b == PersonalityType.Cautious) ||
                (a == PersonalityType.Cautious && b == PersonalityType.Cunning))
                return 8f;

            if ((a == PersonalityType.Aggressive && b == PersonalityType.Vengeful) ||
                (a == PersonalityType.Vengeful && b == PersonalityType.Aggressive))
                return 5f;

            if ((a == PersonalityType.Aggressive && b == PersonalityType.Cautious) ||
                (a == PersonalityType.Cautious && b == PersonalityType.Aggressive))
                return -12f;

            if ((a == PersonalityType.Cunning && b == PersonalityType.Vengeful) ||
                (a == PersonalityType.Vengeful && b == PersonalityType.Cunning))
                return -6f;

            return 0f;
        }

        public static float GetDailyNeutralDrift(float relation, float driftRate)
        {
            float safeRate = Math.Max(0f, driftRate);
            if (Math.Abs(relation) <= 0.001f || safeRate <= 0f)
                return 0f;

            float magnitude = (float)Math.Max(0.10f, Math.Abs(relation) * safeRate);
            return relation > 0f ? -magnitude : magnitude;
        }

        public static bool ShouldFormAlliance(
            float relation,
            bool alreadyAllied,
            bool alreadyRivals,
            int commandedMilitiasA,
            int commandedMilitiasB,
            float allianceThreshold)
        {
            if (alreadyAllied || alreadyRivals)
                return false;

            if (commandedMilitiasA <= 0 || commandedMilitiasB <= 0)
                return false;

            return relation >= allianceThreshold;
        }

        public static bool ShouldDeclareRivalry(
            float relation,
            bool alreadyAllied,
            bool alreadyRivals,
            float rivalryThreshold)
        {
            if (alreadyRivals || alreadyAllied)
                return false;

            return relation <= rivalryThreshold;
        }

        public static float ComputeBetrayalChance(
            float relation,
            float paranoiaA,
            float paranoiaB,
            float betrayalPressure)
        {
            float negativeRelation = Math.Max(0f, -relation);
            float relationFactor = (float)Math.Min(1f, negativeRelation / 80f);
            float paranoiaFactor = Clamp01((paranoiaA + paranoiaB) * 0.5f);
            float pressureFactor = Clamp01(betrayalPressure);

            float chance = relationFactor * 0.45f
                         + paranoiaFactor * 0.35f
                         + pressureFactor * 0.20f;

            return Clamp01(chance);
        }

        private static float Clamp01(float value)
        {
            return (float)Math.Max(0f, Math.Min(1f, value));
        }
    }
}