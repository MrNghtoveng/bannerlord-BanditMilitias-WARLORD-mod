using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Territory
{
    // ── Yardımcı tipler ───────────────────────────────────────────────
    public enum HotspotType
    {
        Unknown = 0,
        BattleGround = 1,
        RaidTarget = 2,
        GatheringPoint = 3,
        PatrolRoute = 4
    }

    public class ActivityHotspot
    {
        public Vec2 Position { get; set; }
        public float ActivityScore { get; set; }
        public HotspotType Type { get; set; }
        public CampaignTime LastActivity { get; set; }
    }

    public class TerritoryInfo
    {
        public Settlement? Hideout { get; set; }
        public float ControlStrength { get; set; }
        public CampaignTime LastUpdate { get; set; }
        public int MilitiaCount { get; set; }
        public List<MobileParty> Occupants { get; set; } = new();
    }

    public class TerritoryOpportunityEvent : IGameEvent
    {
        public Vec2 Position { get; set; }
        public float ActivityScore { get; set; }
        public int CurrentMilitias { get; set; }
        public HotspotType Type { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => Settings.Instance?.TestingMode == true;
        public string GetDescription() =>
            $"Territory Opportunity at {Position} (Type: {Type}, Score: {ActivityScore:F1})";
    }

    // ── Ana sistem ────────────────────────────────────────────────────
    public class TerritorySystem : MilitiaModuleBase
    {
        public static readonly TerritorySystem Instance = new TerritorySystem();
        private TerritorySystem() { }

        public override string ModuleName => "TerritoryInfluence";
        public override bool IsEnabled => Settings.Instance?.EnableSpatialAwareness ?? true;
        public override int Priority => 30;

        private const float TERRITORY_RADIUS = 50f;
        private const float PROXIMITY_THRESHOLD = 50f;
        private const int MAX_BATTLE_SITES = 100;
        private const int MAX_HOTSPOTS = 50;

        private Dictionary<Settlement, TerritoryInfo> _territoryMap = new();
        private List<Intelligence.Strategic.BattleSite> _battleSites = new();
        private Dictionary<string, ActivityHotspot> _activityHotspots = new();

        private string ToGridKey(Vec2 pos) =>
            $"{(int)(pos.X / 50f)}_{(int)(pos.Y / 50f)}";

        public override string GetDiagnostics() =>
            $"Territories: {_territoryMap.Count}, " +
            $"BattleSites: {_battleSites.Count}, " +
            $"Hotspots: {_activityHotspots.Count}";

        // ── Lifecycle ─────────────────────────────────────────────────
        public override void Initialize()
        {
            if (!IsEnabled) return;
            EventBus.Instance.Subscribe<MilitiaKilledEvent>(OnMilitiaKilled);
            EventBus.Instance.Subscribe<HideoutClearedEvent>(OnHideoutCleared);

            if (Settings.Instance?.TestingMode == true && Settings.Instance?.ShowTestMessages == true)
                InformationManager.DisplayMessage(
                    new InformationMessage("[TerritoryInfluence] Online", Colors.Green));
        }

        public override void Cleanup()
        {
            EventBus.Instance.Unsubscribe<MilitiaKilledEvent>(OnMilitiaKilled);
            EventBus.Instance.Unsubscribe<HideoutClearedEvent>(OnHideoutCleared);
            _territoryMap.Clear();
            _battleSites.Clear();
            _activityHotspots.Clear();
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled) return;
            UpdateTerritoryControl();
            DecayBattleSites();
            DecayHotspots();
        }

        public override void OnHourlyTick()
        {
            if (!IsEnabled) return;
            UpdateTerritoryOccupancy();
            DetectStrategicOpportunities();
        }

        // ── SyncData ─────────────────────────────────────────────────
        public override void SyncData(IDataStore ds)
        {
            _ = ds.SyncData("_battleSites", ref _battleSites);

            // Territory map
            if (ds.IsSaving)
            {
                var ids = new List<string>();
                var strengths = new List<float>();
                var updates = new List<long>();

                foreach (var kvp in _territoryMap)
                {
                    ids.Add(kvp.Key.StringId);
                    strengths.Add(kvp.Value.ControlStrength);
                    updates.Add((long)kvp.Value.LastUpdate.ToHours);
                }
                _ = ds.SyncData("_tm_ids", ref ids);
                _ = ds.SyncData("_tm_strengths", ref strengths);
                _ = ds.SyncData("_tm_updates", ref updates);
            }
            else if (ds.IsLoading)
            {
                _territoryMap.Clear();
                var ids = new List<string>();
                var strengths = new List<float>();
                var updates = new List<long>();
                _ = ds.SyncData("_tm_ids", ref ids);
                _ = ds.SyncData("_tm_strengths", ref strengths);
                _ = ds.SyncData("_tm_updates", ref updates);

                for (int i = 0; i < ids.Count; i++)
                {
                    var h = ModuleManager.Instance.HideoutCache
                        .FirstOrDefault(s => s.StringId == ids[i]);
                    if (h != null)
                        _territoryMap[h] = new TerritoryInfo
                        {
                            Hideout = h,
                            ControlStrength = strengths[i],
                            LastUpdate = CampaignTime.Hours(updates[i])
                        };
                }
            }

            // Hotspots
            if (ds.IsSaving)
            {
                var keys = new List<string>();
                var posX = new List<float>();
                var posY = new List<float>();
                var scores = new List<float>();
                var types = new List<int>();
                var times = new List<long>();

                foreach (var kvp in _activityHotspots)
                {
                    keys.Add(kvp.Key);
                    posX.Add(kvp.Value.Position.X);
                    posY.Add(kvp.Value.Position.Y);
                    scores.Add(kvp.Value.ActivityScore);
                    types.Add((int)kvp.Value.Type);
                    times.Add((long)kvp.Value.LastActivity.ToHours);
                }
                _ = ds.SyncData("_hs_keys", ref keys);
                _ = ds.SyncData("_hs_posX", ref posX);
                _ = ds.SyncData("_hs_posY", ref posY);
                _ = ds.SyncData("_hs_scores", ref scores);
                _ = ds.SyncData("_hs_types", ref types);
                _ = ds.SyncData("_hs_times", ref times);
            }
            else if (ds.IsLoading)
            {
                _activityHotspots.Clear();
                var keys = new List<string>();
                var posX = new List<float>();
                var posY = new List<float>();
                var scores = new List<float>();
                var types = new List<int>();
                var times = new List<long>();
                _ = ds.SyncData("_hs_keys", ref keys);
                _ = ds.SyncData("_hs_posX", ref posX);
                _ = ds.SyncData("_hs_posY", ref posY);
                _ = ds.SyncData("_hs_scores", ref scores);
                _ = ds.SyncData("_hs_types", ref types);
                _ = ds.SyncData("_hs_times", ref times);

                for (int i = 0; i < keys.Count; i++)
                    _activityHotspots[keys[i]] = new ActivityHotspot
                    {
                        Position = new Vec2(posX[i], posY[i]),
                        ActivityScore = scores[i],
                        Type = (HotspotType)types[i],
                        LastActivity = CampaignTime.Hours(times[i])
                    };
            }
        }

        // ── Kontrol güncellemesi ──────────────────────────────────────
        private void UpdateTerritoryControl()
        {
            var stale = _territoryMap.Keys.Where(h => h == null || !h.IsActive).ToList();
            foreach (var h in stale) _ = _territoryMap.Remove(h);

            foreach (var hideout in ModuleManager.Instance.HideoutCache)
            {
                if (hideout == null || !hideout.IsActive) continue;

                if (!_territoryMap.TryGetValue(hideout, out var t))
                {
                    t = new TerritoryInfo { Hideout = hideout };
                    _territoryMap[hideout] = t;
                }

                if (t.LastUpdate.ElapsedDaysUntilNow < 0.5f &&
                    t.LastUpdate != CampaignTime.Never) continue;

                var militias = GetPartiesInRadius(
                    CompatibilityLayer.GetSettlementPosition(hideout), TERRITORY_RADIUS);

                t.MilitiaCount = militias.Count;
                t.Occupants = militias;
                t.ControlStrength = Math.Min(militias.Count / 8f, 1f);

                var warlord = Intelligence.Strategic.WarlordSystem.Instance
                    .GetWarlordForHideout(hideout);
                if (warlord?.IsAlive == true) t.ControlStrength =
                    Math.Min(t.ControlStrength * 1.5f, 1f);

                t.LastUpdate = CampaignTime.Now;
            }
        }

        private void UpdateTerritoryOccupancy()
        {
            foreach (var kvp in _territoryMap)
            {
                if (kvp.Value.Hideout == null) continue;
                var occ = GetPartiesInRadius(
                    CompatibilityLayer.GetSettlementPosition(kvp.Value.Hideout),
                    TERRITORY_RADIUS);
                kvp.Value.Occupants = occ;
                kvp.Value.MilitiaCount = occ.Count;
            }
        }

        // ── Fırsat tespiti ────────────────────────────────────────────
        private void DetectStrategicOpportunities()
        {
            if (MobileParty.MainParty != null)
            {
                var pp = CompatibilityLayer.GetPartyPosition(MobileParty.MainParty);
                if (pp.IsValid)
                {
                    Settlement? nearest = null;
                    float minDistSq = PROXIMITY_THRESHOLD * PROXIMITY_THRESHOLD;

                    foreach (var kvp in _territoryMap)
                    {
                        if (kvp.Key == null) continue;
                        var sp = CompatibilityLayer.GetSettlementPosition(kvp.Key);
                        if (!sp.IsValid) continue;
                        float d = pp.DistanceSquared(sp);
                        if (d < minDistSq) { minDistSq = d; nearest = kvp.Key; }
                    }

                    if (nearest != null)
                        EventBus.Instance.Publish(new PlayerEnteredTerritoryEvent
                        {
                            NearbyHideout = nearest,
                            Distance = (float)Math.Sqrt(minDistSq),
                            NearbyMilitias = _territoryMap[nearest].MilitiaCount
                        });
                }
            }

            foreach (var hs in _activityHotspots.Values
                .Where(h => h.ActivityScore > 5f)
                .OrderByDescending(h => h.ActivityScore)
                .Take(5))
            {
                int cnt = GetPartiesInRadius(hs.Position, 40f).Count;
                if (cnt > 0 && cnt < 10)
                    EventBus.Instance.PublishDeferred(new TerritoryOpportunityEvent
                    {
                        Position = hs.Position,
                        ActivityScore = hs.ActivityScore,
                        CurrentMilitias = cnt,
                        Type = hs.Type
                    });
            }
        }

        // ── Hotspot / BattleSite ──────────────────────────────────────
        public void RegisterBattleSite(Vec2 position, int casualties, bool militiaWon)
        {
            if (!IsEnabled) return;
            _battleSites.Add(new Intelligence.Strategic.BattleSite
            {
                Position = position,
                Time = CampaignTime.Now,
                Intensity = casualties
            });
            if (_battleSites.Count > MAX_BATTLE_SITES) _battleSites.RemoveAt(0);
            RegisterHotspot(position, HotspotType.BattleGround, casualties / 5f);
        }

        private void RegisterHotspot(Vec2 position, HotspotType type, float score)
        {
            string key = ToGridKey(position);
            if (_activityHotspots.TryGetValue(key, out var hs))
            {
                hs.ActivityScore += score;
                hs.LastActivity = CampaignTime.Now;
            }
            else
            {
                _activityHotspots[key] = new ActivityHotspot
                {
                    Position = position,
                    ActivityScore = score,
                    Type = type,
                    LastActivity = CampaignTime.Now
                };
                if (_activityHotspots.Count > MAX_HOTSPOTS)
                {
                    string oldest = _activityHotspots
                        .OrderBy(k => k.Value.LastActivity.ToHours)
                        .First().Key;
                    _ = _activityHotspots.Remove(oldest);
                }
            }
        }

        private void DecayBattleSites()
        {
            _ = _battleSites.RemoveAll(s =>
                CampaignTime.Now.ToHours - s.Time.ToHours > 30 * 24);
        }

        private void DecayHotspots()
        {
            var remove = new List<string>();
            foreach (var kvp in _activityHotspots)
            {
                float hrs = (float)(CampaignTime.Now.ToHours - kvp.Value.LastActivity.ToHours);
                kvp.Value.ActivityScore *= (float)Math.Pow(0.8, hrs / 24.0);
                if (kvp.Value.ActivityScore < 1f) remove.Add(kvp.Key);
            }
            foreach (var k in remove) _ = _activityHotspots.Remove(k);
        }

        // ── Public API ────────────────────────────────────────────────
        public List<MobileParty> GetPartiesInRadius(Vec2 center, float radius)
        {
            var result = new List<MobileParty>();
            var grid = ModuleManager.Instance
                .GetModule<BanditMilitias.Systems.Grid.SpatialGridSystem>();
            if (grid != null)
            {
                grid.QueryNearby(center, radius, result);
            }
            else
            {
                float rSq = radius * radius;
                foreach (var p in ModuleManager.Instance.ActiveMilitias)
                {
                    if (!p.IsActive || !p.IsVisible) continue;
                    var pos = CompatibilityLayer.GetPartyPosition(p);
                    if (pos.IsValid && pos.DistanceSquared(center) <= rSq)
                        result.Add(p);
                }
            }
            return result;
        }

        public bool IsPlayerNearby(Vec2 position, float threshold = PROXIMITY_THRESHOLD)
        {
            if (MobileParty.MainParty == null) return false;
            var pp = CompatibilityLayer.GetPartyPosition(MobileParty.MainParty);
            return pp.IsValid && pp.DistanceSquared(position) <= threshold * threshold;
        }

        public float GetActivity(Vec2 position)
        {
            return _activityHotspots.TryGetValue(ToGridKey(position), out var hs)
                ? hs.ActivityScore : 0f;
        }

        public List<Intelligence.Strategic.BattleSite> GetRecentBattleSites()
            => new List<Intelligence.Strategic.BattleSite>(_battleSites);

        // ── Event handlers ────────────────────────────────────────────
        private void OnMilitiaKilled(MilitiaKilledEvent e)
        {
            if (e.Victim != null)
                RegisterBattleSite(CompatibilityLayer.GetPartyPosition(e.Victim), 1, false);
        }

        private void OnHideoutCleared(HideoutClearedEvent e)
        {
            if (e.Hideout != null)
                RegisterBattleSite(
                    CompatibilityLayer.GetSettlementPosition(e.Hideout),
                    e.SurvivingMilitias + 10, false);
        }
    }
}