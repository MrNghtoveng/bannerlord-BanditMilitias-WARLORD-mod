using BanditMilitias.Core.Config;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Core.Neural;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Tracking
{
    // ── IPlayerTracker (inline) ───────────────────────────────────
    public interface IPlayerTracker
    {
        bool IsEnabled { get; }
        string ModuleName { get; }
        int Priority { get; }
        void Cleanup();
        string GetDiagnostics();
        BanditMilitias.Intelligence.Strategic.PlayStyle GetPlayerPlayStyle();
        BanditMilitias.Systems.Tracking.HideoutReputation? GetReputation(TaleWorlds.CampaignSystem.Settlements.Settlement? hideout);
        float GetThreatLevel();
        void Initialize();
        bool IsFrequentRoute(TaleWorlds.Library.Vec2 position, out string? hotspotInfo);
        void OnDailyTick();
        void OnHourlyTick();
        void OnTick(float dt);
        TaleWorlds.Library.Vec2? GetMostFrequentRoute();
        TaleWorlds.Library.Vec2? PredictNextPosition();
        void RecordPlayerMovement();
        bool ShouldAvoidPlayer(float militiaStrength);
        bool ShouldPursuePlayer(TaleWorlds.CampaignSystem.Settlements.Settlement hideout);
        void SyncData(IDataStore dataStore);
    }



    public sealed class PlayerTracker : BanditMilitias.Core.Components.MilitiaModuleBase, IPlayerTracker
    {
        public override string ModuleName => "PlayerTracker";
        public override bool IsEnabled => true;
        public override int Priority => 100;
        private const int POSITION_HISTORY = 10;

        private static readonly Lazy<PlayerTracker> _instance =
            new Lazy<PlayerTracker>(() => new PlayerTracker());

        public static PlayerTracker Instance => _instance.Value;

        public override void OnTick(float dt) { }
        public override void OnHourlyTick() { }

        private PlayerBehaviorModel _behaviorModel = new PlayerBehaviorModel();

        private readonly Dictionary<PlayStyle, float> _styleProb = new()
        {
            { PlayStyle.Aggressive, 0.25f },
            { PlayStyle.Defensive, 0.25f },
            { PlayStyle.Economic, 0.25f },
            { PlayStyle.Balanced, 0.25f }
        };

        private Dictionary<string, HideoutReputation> _hideoutReputations = new();

        private readonly Queue<KillRecord> _recentKills = new();
        private const int MAX_KILL_HISTORY = 100;

        private readonly Dictionary<long, RouteCell> _routeHeatmap = new();
        private const float GRID_SIZE = 10f;
        private const int MAX_HEATMAP_CELLS = 1000;

        private readonly Queue<Vec2> _recentPositions = new Queue<Vec2>();

        private float _cachedThreatLevel = 0f;
        private CampaignTime _lastThreatUpdate = CampaignTime.Zero;
        private const float THREAT_UPDATE_INTERVAL = 0.5f;

        private float _lastPublishedThreat = 0f;
        private const float THREAT_CHANGE_THRESHOLD = 0.15f;

        private CampaignTime _lastDecayUpdate = CampaignTime.Zero;
        private const float DECAY_INTERVAL = 24f;
        private const float KILL_DECAY_RATE = 0.95f;

        private int _totalKillsTracked = 0;
        private int _totalMovementRecords = 0;
        private int _heatmapPrunes = 0;
        private int _threatEventsPublished = 0;

        private PlayerTracker() { }

        public override void Initialize()
        {
            EventBus.Instance.Subscribe<MilitiaKilledEvent>(OnMilitiaKilled);
            EventBus.Instance.Subscribe<PlayerEnteredTerritoryEvent>(OnPlayerEnteredTerritory);
            EventBus.Instance.Subscribe<HideoutClearedEvent>(OnHideoutCleared);
        }

        public override void Cleanup()
        {
            EventBus.Instance.Unsubscribe<MilitiaKilledEvent>(OnMilitiaKilled);
            EventBus.Instance.Unsubscribe<PlayerEnteredTerritoryEvent>(OnPlayerEnteredTerritory);
            EventBus.Instance.Unsubscribe<HideoutClearedEvent>(OnHideoutCleared);

            _cachedThreatLevel = 0f;
            _lastPublishedThreat = 0f;
            _lastThreatUpdate = CampaignTime.Zero;
            _lastDecayUpdate = CampaignTime.Zero;

            foreach (var key in _styleProb.Keys.ToList())
                _styleProb[key] = 0.25f;

            _recentKills.Clear();
            _routeHeatmap.Clear();
            _recentPositions.Clear();

        }

        private void OnMilitiaKilled(MilitiaKilledEvent evt)
        {
            if (!evt.WasPlayerKill || evt.HomeHideout == null) return;

            string hideoutId = evt.HomeHideout.StringId;

            var rep = GetOrCreateReputation(hideoutId, evt.HomeHideout);
            rep.KillCount++;
            rep.LastKillTime = CampaignTime.Now;
            rep.TotalBounty += CalculateBounty(evt.Victim);

            _recentKills.Enqueue(new KillRecord
            {
                Timestamp = CampaignTime.Now,
                HideoutId = hideoutId,
                Bounty = CalculateBounty(evt.Victim)
            });

            while (_recentKills.Count > MAX_KILL_HISTORY)
            {
                _ = _recentKills.Dequeue();
            }

            _behaviorModel.TotalKills++;
            _behaviorModel.CombatScore += CalculateCombatScore(evt.Victim);

            UpdatePlayStyleProbabilities(PlayStyle.Aggressive, 0.1f);

            _totalKillsTracked++;

            UpdateThreatImmediate();

            if (rep.KillCount >= 5 && rep.KillCount % 5 == 0)
            {
                NotifyReputationChange(evt.HomeHideout, rep);
            }
        }

        private void OnPlayerEnteredTerritory(PlayerEnteredTerritoryEvent evt)
        {
            if (evt.NearbyHideout == null) return;

            var rep = GetOrCreateReputation(evt.NearbyHideout.StringId, evt.NearbyHideout);
            rep.TimesNear++;
        }

        private void OnHideoutCleared(HideoutClearedEvent evt)
        {
            if (evt.Hideout == null) return;

            var rep = GetOrCreateReputation(evt.Hideout.StringId, evt.Hideout);
            rep.TimesCleared++;
            rep.LastClearedTime = CampaignTime.Now;

            _behaviorModel.HideoutsDestroyed++;

            UpdatePlayStyleProbabilities(PlayStyle.Aggressive, 0.3f);

            UpdateThreatImmediate();
        }

        private void UpdateThreatImmediate()
        {

            float oldThreat = _cachedThreatLevel;
            float newThreat = CalculateThreatLevel();

            _cachedThreatLevel = newThreat;
            _lastThreatUpdate = CampaignTime.Now;

            float threatChange = Math.Abs(newThreat - oldThreat);

            if (threatChange >= THREAT_CHANGE_THRESHOLD ||
                (oldThreat < 1.0f && newThreat >= 1.0f) ||
                (oldThreat < 2.0f && newThreat >= 2.0f))
            {
                PublishThreatChangeEvent(newThreat, oldThreat, GetThreatChangeReason(newThreat, oldThreat));
            }
        }

        private void PublishThreatChangeEvent(float newThreat, float oldThreat, string reason)
        {
            try
            {
                var evt = EventBus.Instance.Get<ThreatLevelChangedEvent>();
                evt.NewThreatLevel = newThreat;
                evt.OldThreatLevel = oldThreat;
                evt.ThreatDelta = newThreat - oldThreat;
                evt.Reason = reason;
                evt.ChangeTime = CampaignTime.Now;

                NeuralEventRouter.Instance.Publish(evt);
                EventBus.Instance.Return(evt);

                _lastPublishedThreat = newThreat;
                _threatEventsPublished++;

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Info("PlayerTracker",
                        $"[Threat Change] {oldThreat:F2} \u203a {newThreat:F2} ({reason})");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("PlayerTracker", $"Failed to publish threat event: {ex.Message}");
            }
        }

        private string GetThreatChangeReason(float newThreat, float oldThreat)
        {
            if (_behaviorModel.TotalKills > 0 && newThreat > oldThreat)
                return "Militia kills increased";

            if (_behaviorModel.HideoutsDestroyed > 0 && newThreat > oldThreat)
                return "Hideout destroyed";

            if (newThreat < oldThreat)
                return "Temporal decay";

            return "Threat assessment updated";
        }

        // RecordPlayerMovement: Şu an hiçbir yerde çağrılmıyor — ilerideki hareket takibi için rezerve.
        public void RecordPlayerMovement()
        {
            if (Hero.MainHero?.PartyBelongedTo == null) return;

            int strongMilitias = ModuleManager.Instance.ActiveMilitias
                .Count(m => m.MemberRoster.TotalManCount > 50);

            if (strongMilitias < 3) return;

            var pos = CompatibilityLayer.GetPartyPosition(Hero.MainHero.PartyBelongedTo);

            long cellKey = GetGridKey(pos);

            if (!_routeHeatmap.TryGetValue(cellKey, out var cell))
            {
                cell = new RouteCell { Position = pos, Visits = 0 };
                _routeHeatmap[cellKey] = cell;
            }

            cell.Visits++;
            cell.LastVisit = CampaignTime.Now;

            _recentPositions.Enqueue(pos);
            if (_recentPositions.Count > POSITION_HISTORY)
            {
                _ = _recentPositions.Dequeue();
            }

            _totalMovementRecords++;

            if (_routeHeatmap.Count > MAX_HEATMAP_CELLS)
            {
                PruneHeatmap();
            }
        }

        private void PruneHeatmap()
        {
            var sorted = _routeHeatmap
                .OrderBy(kvp => kvp.Value.Visits)
                .Take(_routeHeatmap.Count - (MAX_HEATMAP_CELLS * 3 / 4))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in sorted)
            {
                _ = _routeHeatmap.Remove(key);
            }

            _heatmapPrunes++;
        }

        public float GetThreatLevel()
        {
            // Kampanya henüz hazır değilse güvenli sıfır döndür
            if (Campaign.Current == null) return 0f;

            float hoursSinceUpdate = _lastThreatUpdate != CampaignTime.Zero
                ? (float)(CampaignTime.Now - _lastThreatUpdate).ToHours
                : 999f;

            if (hoursSinceUpdate < THREAT_UPDATE_INTERVAL && _cachedThreatLevel >= 0f)
            {
                return _cachedThreatLevel;
            }

            float threat = CalculateThreatLevel();

            _cachedThreatLevel = threat;
            _lastThreatUpdate = CampaignTime.Now;

            return threat;
        }

        private float CalculateThreatLevel()
        {

            float recentKillThreat = CalculateRecentKillThreat();

            float strengthThreat = CalculateStrengthThreat();

            float economicThreat = CalculateEconomicThreat();

            float territoryThreat = Math.Min(1f, _behaviorModel.HideoutsDestroyed / 10f);

            float totalThreat =
                recentKillThreat * 0.4f +
                strengthThreat * 0.3f +
                economicThreat * 0.2f +
                territoryThreat * 0.1f;

            return BanditMilitias.Core.MathUtils.Clamp(totalThreat, 0f, 3f);
        }

        private float CalculateRecentKillThreat()
        {
            if (_recentKills.Count == 0) return 0f;

            float threat = 0f;
            float currentTime = (float)CampaignTime.Now.ToHours;

            foreach (var kill in _recentKills)
            {
                float hoursSince = currentTime - (float)kill.Timestamp.ToHours;
                float decay = (float)Math.Exp(-hoursSince / 24f);
                threat += decay;
            }

            return Math.Min(2f, threat / 20f);
        }

        private static float CalculateStrengthThreat()
        {
            try
            {
                if (Campaign.Current == null || Hero.MainHero?.PartyBelongedTo == null) return 0f;
                float strength = CompatibilityLayer.GetTotalStrength(Hero.MainHero.PartyBelongedTo);
                return Math.Min(1.5f, strength / 300f);
            }
            catch (Exception ex)
            {
                Infrastructure.FileLogger.LogWarning($"Strength threat calculation failed: {ex.Message}");
                return 0f;
            }
        }

        private static float CalculateEconomicThreat()
        {
            try
            {
                if (Campaign.Current == null || Hero.MainHero == null) return 0f;
                float wealth = Hero.MainHero.Gold;
                return Math.Min(0.5f, wealth / 100000f);
            }
            catch (Exception ex)
            {
                Infrastructure.FileLogger.LogWarning($"Economic threat calculation failed: {ex.Message}");
                return 0f;
            }
        }

        public PlayStyle GetPlayerPlayStyle()
        {
            // FIX: _styleProb boşsa (ilk init veya reset sonrası) InvalidOperationException fırlatır.
            // Null/boş dict durumunda güvenli varsayılan döndür.
            if (_styleProb == null || _styleProb.Count == 0)
                return PlayStyle.Balanced;
            return _styleProb.OrderByDescending(kvp => kvp.Value).First().Key;
        }

        private void UpdatePlayStyleProbabilities(PlayStyle observedStyle, float strength)
        {
            foreach (var style in _styleProb.Keys.ToList())
            {
                if (style == observedStyle)
                {
                    _styleProb[style] = Math.Min(1f, _styleProb[style] + strength);
                }
                else
                {
                    _styleProb[style] = Math.Max(0f, _styleProb[style] - strength / 3f);
                }
            }

            float sum = _styleProb.Values.Sum();
            if (sum > 0f)
            {
                foreach (var style in _styleProb.Keys.ToList())
                {
                    _styleProb[style] /= sum;
                }
            }
        }

        public Vec2? PredictNextPosition()
        {
            if (_recentPositions.Count < 3) return null;

            var positions = _recentPositions.ToArray();
            int n = positions.Length;

            Vec2 last = positions[n - 1];
            Vec2 prev = positions[n - 2];

            Vec2 velocity = last - prev;
            return last + velocity;
        }

        public bool IsFrequentRoute(Vec2 position, out string? hotspotInfo)
        {
            hotspotInfo = null;
            long cellKey = GetGridKey(position);

            if (_routeHeatmap.TryGetValue(cellKey, out var cell))
            {
                if (cell.Visits > AIConstants.REPUTATION_HOTSPOT_VISITS)
                {
                    hotspotInfo = $"Hotspot: {cell.Visits} visits";
                    return true;
                }
            }

            return false;
        }

        public Vec2? GetMostFrequentRoute()
        {
            if (_routeHeatmap.Count == 0) return null;

            var hotspot = _routeHeatmap
                .Where(kvp => kvp.Value.Visits > AIConstants.REPUTATION_HOTSPOT_VISITS)
                .OrderByDescending(kvp => kvp.Value.Visits)
                .Select(kvp => (RouteCell?)kvp.Value)
                .FirstOrDefault();

            return hotspot?.Position;
        }

        public HideoutReputation? GetReputation(Settlement? hideout)
        {
            if (hideout == null) return null;

            _ = _hideoutReputations.TryGetValue(hideout.StringId, out var rep);
            return rep;
        }

        public bool ShouldPursuePlayer(Settlement hideout)
        {
            var rep = GetReputation(hideout);
            if (rep == null) return false;

            float hoursSinceKill = (float)(CampaignTime.Now - rep.LastKillTime).ToHours;
            return rep.KillCount >= AIConstants.REPUTATION_PURSUE_KILLS &&
                   hoursSinceKill < AIConstants.REPUTATION_PURSUE_HOURS;
        }

        public bool ShouldAvoidPlayer(float militiaStrength)
        {
            float threatLevel = GetThreatLevel();
            float playerStrength = CalculateStrengthThreat() * 300f;

            return threatLevel > 1.5f &&
                   playerStrength > militiaStrength * AIConstants.PLAYER_AVOID_MULTIPLIER;
        }

        public override void OnDailyTick()
        {
            ApplyTemporalDecay();
        }

        private void ApplyTemporalDecay()
        {
            if (Campaign.Current == null)
                return;

            float hoursSinceDecay = (float)(CampaignTime.Now - _lastDecayUpdate).ToHours;

            if (hoursSinceDecay < DECAY_INTERVAL) return;

            foreach (var rep in _hideoutReputations.Values)
            {
                rep.KillCount = (int)(rep.KillCount * KILL_DECAY_RATE);
            }

            _behaviorModel.TotalKills = (int)(_behaviorModel.TotalKills * KILL_DECAY_RATE);
            _behaviorModel.CombatScore *= KILL_DECAY_RATE;

            var keys = _routeHeatmap.Keys.ToList();
            foreach (var key in keys)
            {
                var cell = _routeHeatmap[key];
                cell.Visits = (int)(cell.Visits * 0.98f);
                _routeHeatmap[key] = cell;
            }

            _lastDecayUpdate = CampaignTime.Now;

            UpdateThreatImmediate();
        }

        private HideoutReputation GetOrCreateReputation(string hideoutId, Settlement hideout)
        {
            if (!_hideoutReputations.TryGetValue(hideoutId, out var rep))
            {
                rep = new HideoutReputation
                {
                    HideoutId = hideoutId,
                    HideoutName = hideout.Name?.ToString() ?? "Unknown"
                };
                _hideoutReputations[hideoutId] = rep;
            }

            return rep;
        }

        private static long GetGridKey(Vec2 position)
        {
            int x = (int)(position.X / GRID_SIZE);
            int y = (int)(position.Y / GRID_SIZE);
            return ((long)x << 32) | (uint)y;
        }

        private static float CalculateBounty(MobileParty? victim)
        {
            if (victim?.MemberRoster == null) return 0f;
            return victim.MemberRoster.TotalManCount * 10f;
        }

        private static float CalculateCombatScore(MobileParty? victim)
        {
            if (victim == null) return 0f;
            return victim.MemberRoster.TotalManCount * 0.1f;
        }

        private static void NotifyReputationChange(Settlement hideout, HideoutReputation rep)
        {
            if (Settings.Instance?.TestingMode != true || Settings.Instance?.ShowTestMessages != true) return;

            string level = rep.KillCount switch
            {
                >= 20 => "ARCH-ENEMY",
                >= 10 => "NEMESIS",
                >= 5 => "INFAMOUS",
                _ => "KNOWN"
            };

            InformationManager.DisplayMessage(new InformationMessage(
                $"[Reputation] {hideout.Name}: {level} ({rep.KillCount} kills)",
                Colors.Red));
        }

        public override void SyncData(IDataStore dataStore)
        {
            _ = dataStore.SyncData("_behaviorModel", ref _behaviorModel);
            _ = dataStore.SyncData("_hideoutReputations", ref _hideoutReputations);

            if (_behaviorModel == null) _behaviorModel = new PlayerBehaviorModel();
            if (_hideoutReputations == null) _hideoutReputations = new Dictionary<string, HideoutReputation>();

            if (dataStore.IsSaving)
            {
                var keys = _routeHeatmap.Keys.ToArray();
                var cells = _routeHeatmap.Values.ToArray();
                _ = dataStore.SyncData("_heatmapKeys", ref keys);
                _ = dataStore.SyncData("_heatmapCells", ref cells);
            }
            else
            {
                long[]? keys = null;
                RouteCell[]? cells = null;
                _ = dataStore.SyncData("_heatmapKeys", ref keys);
                _ = dataStore.SyncData("_heatmapCells", ref cells);

                if (keys != null && cells != null)
                {
                    _routeHeatmap.Clear();
                    for (int i = 0; i < Math.Min(keys.Length, cells.Length); i++)
                    {
                        _routeHeatmap[keys[i]] = cells[i];
                    }
                }
            }
        }

        public override string GetDiagnostics()
        {
            try
            {
                if (_behaviorModel == null || _hideoutReputations == null)
                    return $"{ModuleName}: Initializing tracking models...";

                // Kampanya henüz hazır değilse tehdit hesabı yapma
                float threat = Campaign.Current != null ? GetThreatLevel() : 0f;

                return "PlayerTracker:\n" +
                       $"  Campaign Ready: {Campaign.Current != null}\n" +
                       $"  Threat Level: {threat:F2}\n" +
                       $"  Play Style: {GetPlayerPlayStyle()}\n" +
                       $"  Total Kills: {_behaviorModel.TotalKills}\n" +
                       $"  Hideouts Destroyed: {_behaviorModel.HideoutsDestroyed}\n" +
                       $"  Reputations: {_hideoutReputations.Count}\n" +
                       $"  Heatmap Cells: {_routeHeatmap?.Count ?? 0} (pruned {_heatmapPrunes}x)\n" +
                       $"  Movement Records: {_totalMovementRecords}\n" +
                       $"  Threat Events: {_threatEventsPublished}";
            }
            catch (Exception ex)
            {
                return $"{ModuleName}: GetDiagnostics error - {ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    [Serializable]
    public class PlayerBehaviorModel
    {
        [TaleWorlds.SaveSystem.SaveableProperty(1)]
        public int TotalKills { get; set; }
        [TaleWorlds.SaveSystem.SaveableProperty(2)]
        public int HideoutsDestroyed { get; set; }
        [TaleWorlds.SaveSystem.SaveableProperty(3)]
        public float CombatScore { get; set; }
        [TaleWorlds.SaveSystem.SaveableProperty(4)]
        public float WealthScore { get; set; }
    }

    [Serializable]
    public class HideoutReputation
    {
        [TaleWorlds.SaveSystem.SaveableProperty(1)]
        public string HideoutId { get; set; } = "";

        [TaleWorlds.SaveSystem.SaveableProperty(2)]
        public string HideoutName { get; set; } = "Unknown";

        [TaleWorlds.SaveSystem.SaveableProperty(3)]
        public int KillCount { get; set; }

        [TaleWorlds.SaveSystem.SaveableProperty(4)]
        public CampaignTime LastKillTime { get; set; }

        [TaleWorlds.SaveSystem.SaveableProperty(5)]
        public float TotalBounty { get; set; }

        [TaleWorlds.SaveSystem.SaveableProperty(6)]
        public int TimesCleared { get; set; }

        [TaleWorlds.SaveSystem.SaveableProperty(7)]
        public CampaignTime LastClearedTime { get; set; }

        [TaleWorlds.SaveSystem.SaveableProperty(8)]
        public int TimesNear { get; set; }

        public bool IsArchEnemy => KillCount >= 20;
        public bool IsNemesis => KillCount >= 10;
        public bool IsInfamous => KillCount >= 5;
    }

    [Serializable]
    public struct RouteCell
    {
        [TaleWorlds.SaveSystem.SaveableField(1)]
        public Vec2 Position;
        [TaleWorlds.SaveSystem.SaveableField(2)]
        public int Visits;
        [TaleWorlds.SaveSystem.SaveableField(3)]
        public CampaignTime LastVisit;
    }

    public struct KillRecord
    {
        public CampaignTime Timestamp;
        public string HideoutId;
        public float Bounty;
    }

}
