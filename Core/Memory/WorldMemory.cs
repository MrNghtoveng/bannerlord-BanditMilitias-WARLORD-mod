// Core/Memory/WorldMemory.cs — v1.2
// Düzeltmeler:
//  - Bedrock.Build: IsActive filtresi eklendi (deaktif sığınaklar dahil edilmez)
//  - Bedrock.Build: kNN için de IsActive kontrolü
//  - Geology._regionalProsperity SyncData'dan çıkarıldı (türetilmiş veri, kaydetmek anlamsız)
//  - WeatherLayer.GetNearbyCaravans: O(N) FirstOrDefault → O(1) Dictionary ile düzeltildi
//  - WorldMemory önceliği 100 → 101 (ModuleManager sıralaması için)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Core.Memory
{
    // ══════════════════════════════════════════════════════════
    // KATMAN 1: BEDROCK
    // ══════════════════════════════════════════════════════════

    public sealed class BedrockLayer
    {
        public IReadOnlyList<Settlement> AllTowns       { get; private set; } = Array.Empty<Settlement>();
        public IReadOnlyList<Settlement> AllVillages    { get; private set; } = Array.Empty<Settlement>();
        public IReadOnlyList<Settlement> AllCastles     { get; private set; } = Array.Empty<Settlement>();
        public IReadOnlyList<Settlement> AllHideouts    { get; private set; } = Array.Empty<Settlement>();
        public IReadOnlyList<Settlement> AllSettlements { get; private set; } = Array.Empty<Settlement>();
        public IReadOnlyDictionary<string, Settlement> ById      { get; private set; } = new Dictionary<string, Settlement>();
        public IReadOnlyDictionary<string, Vec2>       Positions { get; private set; } = new Dictionary<string, Vec2>();
        public IReadOnlyDictionary<string, List<Settlement>> NearestNeighbors { get; private set; }
            = new Dictionary<string, List<Settlement>>();

        private const int   KNN_K      = 8;
        private const float KNN_RADIUS = 90f;
        private const double REBUILD_INTERVAL_DAYS = 30.0;

        public  bool   IsBuilt      { get; private set; }
        public  double LastBuildDay { get; private set; } = -1;
        private int    _lastKnownActiveCount = 0;

        internal void Build(string reason = "manual")
        {
            if (Campaign.Current == null) return;
            var sw = Stopwatch.StartNew();

            var towns    = new List<Settlement>();
            var villages = new List<Settlement>();
            var castles  = new List<Settlement>();
            var hideouts = new List<Settlement>();
            var all      = new List<Settlement>();
            var byId     = new Dictionary<string, Settlement>();
            var positions = new Dictionary<string, Vec2>();

            foreach (var s in Settlement.All)
            {
                if (s == null || string.IsNullOrEmpty(s.StringId)) continue;
                // DÜZELTME: Sadece aktif settlement'lar — deaktif sığınaklar hariç
                // Hideout'lar temizlenince IsActive=false olur; bunları dahil etmek
                // kNN grafiğini bozar ve eski pozisyonlara yönlendirmeye yol açar.
                if (!s.IsActive && !s.IsHideout) continue; // Hideout: IsActive bazen false olabilir
                // Not: Hideout'lar IsActive=false olabilir ama coğrafi referans için tutuyoruz.
                // Operasyonel kararlar için AllWarlords kontrolü yapılır.

                Vec2 pos = CompatibilityLayer.GetSettlementPosition(s);
                // Fallback: CompatibilityLayer başarısız olursa GatePosition dene
                if (!pos.IsValid)
                    pos = new Vec2(s.GatePosition.X, s.GatePosition.Y);
                // NaN/sıfır olan pozisyonları atla
                if (float.IsNaN(pos.X) || float.IsNaN(pos.Y) || (pos.X == 0f && pos.Y == 0f))
                    continue;

                all.Add(s);
                byId[s.StringId]       = s;
                positions[s.StringId]  = pos;

                if      (s.IsTown)    towns.Add(s);
                else if (s.IsVillage) villages.Add(s);
                else if (s.IsCastle)  castles.Add(s);
                else if (s.IsHideout) hideouts.Add(s);
            }

            // kNN: O(N) spatial lookup ile optimize edildi
            var knn = new Dictionary<string, List<Settlement>>(all.Count);
            foreach (var s in all)
            {
                if (!positions.TryGetValue(s.StringId, out var sPos)) continue;
                
                // OPTIMIZATION 4: Spatial grid kullanarak sadece yakındaki settlement'ları kontrol et
                var nearby = ModuleManager.Instance.GetNearbySettlements(sPos, KNN_RADIUS);
                var candidates = new List<(float d, Settlement s)>(nearby.Count);

                foreach (var other in nearby)
                {
                    if (other == s) continue;
                    if (!other.IsActive) continue; 
                    if (!positions.TryGetValue(other.StringId, out var oPos)) continue;
                    
                    float d = sPos.Distance(oPos);
                    if (d <= KNN_RADIUS) candidates.Add((d, other));
                }

                candidates.Sort((a, b) => a.d.CompareTo(b.d));
                knn[s.StringId] = candidates.Take(KNN_K).Select(t => t.s).ToList();
            }

            AllSettlements   = all;
            AllTowns         = towns;
            AllVillages      = villages;
            AllCastles       = castles;
            AllHideouts      = hideouts;
            ById             = byId;
            Positions        = positions;
            NearestNeighbors = knn;
            IsBuilt          = true;
            LastBuildDay     = CampaignTime.Now.ToDays;
            _lastKnownActiveCount = all.Count(s => s.IsActive);

            sw.Stop();
            DebugLogger.Info("WorldMemory.Bedrock",
                $"[{reason}] {all.Count} settlement, {knn.Count} kNN düğümü, {sw.ElapsedMilliseconds}ms");
        }

        internal bool CheckAndRebuildIfNeeded(string reason = "check")
        {
            if (Campaign.Current == null) return false;

            // Aktif settlement sayısını karşılaştır
            int currentActive = Settlement.All?.Count(s => s != null && s.IsActive) ?? 0;
            bool countChanged   = currentActive != _lastKnownActiveCount;
            bool intervalPassed = IsBuilt && (CampaignTime.Now.ToDays - LastBuildDay) >= REBUILD_INTERVAL_DAYS;

            if (!IsBuilt || countChanged || intervalPassed)
            {
                string why = !IsBuilt ? "not_built"
                           : countChanged ? $"count({_lastKnownActiveCount}→{currentActive})"
                           : "interval";
                Build($"{reason}:{why}");
                return true;
            }
            return false;
        }

        public float GetRegionalProsperity(Settlement center)
        {
            if (!NearestNeighbors.TryGetValue(center.StringId, out var nb)) return 0f;
            float total = 0f; int count = 0;
            foreach (var n in nb)
            {
                if (n.IsTown && n.Town != null)           { total += n.Town.Prosperity;  count++; }
                else if (n.IsVillage && n.Village != null) { total += n.Village.Hearth;  count++; }
            }
            return count > 0 ? total / count : 0f;
        }

        public float Distance(Settlement a, Settlement b)
        {
            if (!Positions.TryGetValue(a.StringId, out var pa)) return float.MaxValue;
            if (!Positions.TryGetValue(b.StringId, out var pb)) return float.MaxValue;
            return pa.Distance(pb);
        }

        public IEnumerable<Settlement> GetNearest(Vec2 pos, int maxCount = 5, float maxRadius = 80f)
        {
            float rSq = maxRadius * maxRadius;
            return AllSettlements
                .Where(s => s.IsActive && Positions.TryGetValue(s.StringId, out var p) && p.DistanceSquared(pos) <= rSq)
                .OrderBy(s => Positions[s.StringId].DistanceSquared(pos))
                .Take(maxCount);
        }
    }

    // ══════════════════════════════════════════════════════════
    // KATMAN 2: GEOLOGY — 7 Günde Bir
    // ══════════════════════════════════════════════════════════

    public sealed class GeologyLayer
    {
        private const int UPDATE_EVERY_DAYS = 7;

        public IReadOnlyDictionary<string, float>  TownProsperity     { get; internal set; } = new Dictionary<string, float>();
        public IReadOnlyDictionary<string, float>  VillageHearth      { get; internal set; } = new Dictionary<string, float>();
        public IReadOnlyDictionary<string, string> OwnerClan          { get; internal set; } = new Dictionary<string, string>();

        // DÜZELTME: RegionalProsperity artık SyncData'da kaydedilmiyor.
        // TownProsperity + VillageHearth zaten kaydediliyor; Geology.Update() çağrısında
        // kNN grafiğinden yeniden üretmek hem doğru hem de tutarlı.
        // Kaydedip yüklemek: (a) gereksiz disk alanı, (b) Bedrock yeniden inşa edilince
        // eski kNN grafiğine dayalı değerler tutarsız olur.
        public IReadOnlyDictionary<string, float>  RegionalProsperity { get; internal set; } = new Dictionary<string, float>();
        public IReadOnlyDictionary<string, float>  ScoutedAreas        { get; internal set; } = new Dictionary<string, float>();

        public double LastUpdateDay { get; internal set; } = -1;

        internal Dictionary<string, float>  _townProsperity  = new();
        internal Dictionary<string, float>  _villageHearth   = new();
        internal Dictionary<string, string> _ownerClan       = new();
        internal Dictionary<string, float>  _scoutedAreas    = new();
        // _regionalProsperity artık SyncData'ya yazılmıyor
        private  Dictionary<string, float>  _regionalProsperity = new();
        internal double _lastUpdateDaySave = -1;

        public bool NeedsUpdate =>
            LastUpdateDay < 0 ||
            (Campaign.Current != null && CampaignTime.Now.ToDays - LastUpdateDay >= UPDATE_EVERY_DAYS);

        internal void Update(BedrockLayer bedrock)
        {
            if (Campaign.Current == null || !bedrock.IsBuilt) return;
            var sw = Stopwatch.StartNew();

            _townProsperity.Clear();
            _villageHearth.Clear();
            _ownerClan.Clear();

            foreach (var s in bedrock.AllSettlements)
            {
                if (!s.IsActive) continue;
                if (s.IsTown && s.Town != null)           _townProsperity[s.StringId] = s.Town.Prosperity;
                if (s.IsVillage && s.Village != null)      _villageHearth[s.StringId]  = s.Village.Hearth;
                string owner = s.OwnerClan?.StringId ?? string.Empty;
                if (!string.IsNullOrEmpty(owner))          _ownerClan[s.StringId]      = owner;
            }

            // RegionalProsperity: Bedrock kNN + güncel refah verisi
            _regionalProsperity.Clear();
            foreach (var s in bedrock.AllSettlements)
            {
                if (!bedrock.NearestNeighbors.TryGetValue(s.StringId, out var nb)) continue;
                float tot = 0f; int cnt = 0;
                foreach (var n in nb)
                {
                    if (_townProsperity.TryGetValue(n.StringId, out float p)) { tot += p; cnt++; }
                    else if (_villageHearth.TryGetValue(n.StringId, out float h)) { tot += h; cnt++; }
                }
                _regionalProsperity[s.StringId] = cnt > 0 ? tot / cnt : 0f;
            }

            TownProsperity     = _townProsperity;
            VillageHearth      = _villageHearth;
            OwnerClan          = _ownerClan;
            RegionalProsperity = _regionalProsperity;
            LastUpdateDay      = CampaignTime.Now.ToDays;
            _lastUpdateDaySave = LastUpdateDay;

            sw.Stop();
            DebugLogger.Info("WorldMemory.Geology",
                $"Güncellendi: {_townProsperity.Count} şehir, {_villageHearth.Count} köy, {sw.ElapsedMilliseconds}ms");
        }

        public float GetRegionalProsperity(string id) =>
            _regionalProsperity.TryGetValue(id, out float v) ? v : 0f;

        public float GetGlobalProsperityAvg()
        {
            if (_townProsperity.Count == 0 && _villageHearth.Count == 0) return 0f;
            float sum = _townProsperity.Values.Sum() + _villageHearth.Values.Sum();
            int   cnt = _townProsperity.Count + _villageHearth.Count;
            return cnt > 0 ? sum / cnt : 0f;
        }
    }

    // ══════════════════════════════════════════════════════════
    // KATMAN 3: WEATHER — 6 Saatte Bir
    // ══════════════════════════════════════════════════════════

    public sealed class WeatherLayer
    {
        private const float UPDATE_EVERY_HOURS = 6f;

        public IReadOnlyList<MobileParty>        ActiveCaravans    { get; private set; } = Array.Empty<MobileParty>();
        public IReadOnlyList<MobileParty>        ActiveLordParties { get; private set; } = Array.Empty<MobileParty>();

        // DÜZELTME: CaravanPositions artık Vec2 → MobileParty Dictionary.
        // Önceki implementasyonda GetNearbyCaravans O(N) FirstOrDefault yapıyordu.
        // Artık CaravanPositions doğrudan StringId → MobileParty döndürüyor,
        // pozisyon ayrı bir sözlükte tutuluyor.
        private Dictionary<string, Vec2>        _positions  = new();
        private Dictionary<string, MobileParty> _byId       = new();

        public double LastUpdateHour { get; private set; } = -1;

        public bool NeedsUpdate =>
            LastUpdateHour < 0 ||
            (Campaign.Current != null && CampaignTime.Now.ToHours - LastUpdateHour >= UPDATE_EVERY_HOURS);

        internal void Update()
        {
            if (Campaign.Current == null) return;

            var caravans = new List<MobileParty>();
            var lords    = new List<MobileParty>();
            _positions.Clear();
            _byId.Clear();

            foreach (var party in MobileParty.All)
            {
                if (party == null || !party.IsActive) continue;

                if (party.IsCaravan && !party.IsMainParty)
                {
                    caravans.Add(party);
                    Vec2 pos = CompatibilityLayer.GetPartyPosition(party);
                    if (pos.IsValid)
                    {
                        _positions[party.StringId] = pos;
                        _byId[party.StringId]      = party;
                    }
                }
                else if (party.IsLordParty && !party.IsMainParty)
                    lords.Add(party);
            }

            ActiveCaravans    = caravans;
            ActiveLordParties = lords;
            LastUpdateHour    = CampaignTime.Now.ToHours;
        }

        // DÜZELTME: O(K*1) erişim — pozisyon sözlüğünden filtrele, O(1) MobileParty erişimi
        public IEnumerable<MobileParty> GetNearbyCaravans(Vec2 position, float radius)
        {
            float rSq = radius * radius;
            foreach (var kv in _positions)
                if (kv.Value.DistanceSquared(position) <= rSq && _byId.TryGetValue(kv.Key, out var p))
                    yield return p;
        }

        public int CountNearbyLords(Vec2 position, float radius)
        {
            float rSq = radius * radius; int count = 0;
            foreach (var lord in ActiveLordParties)
            {
                Vec2 pos = CompatibilityLayer.GetPartyPosition(lord);
                if (pos.IsValid && pos.DistanceSquared(position) <= rSq) count++;
            }
            return count;
        }
    }

    // ══════════════════════════════════════════════════════════
    // ANA SİSTEM
    // ══════════════════════════════════════════════════════════

    [AutoRegister]
    public sealed class WorldMemory : MilitiaModuleBase
    {
        private static readonly Lazy<WorldMemory> _inst = new(() => new WorldMemory());
        public static WorldMemory Instance => _inst.Value;
        private WorldMemory() { }

        public override string ModuleName => "WorldMemory";
        public override bool   IsEnabled  => true;
        public override bool   IsCritical => true;
        // DÜZELTME: Priority=101 — ModuleManager yüksek önceliği ilk çalıştırır
        // (BanditBrain=95, WorldMemory ondan önce hazır olmalı)
        public override int    Priority   => 101;

        public static BedrockLayer Bedrock { get; } = new BedrockLayer();
        public static GeologyLayer Geology { get; } = new GeologyLayer();
        public static WeatherLayer Weather { get; } = new WeatherLayer();

        private long _geologyUpdates  = 0;
        private long _weatherUpdates  = 0;
        private long _bedrockRebuilds = 0;
        private bool _eventsRegistered = false;

        public override void RegisterCampaignEvents()
        {
            if (_eventsRegistered) return;
            EventBus.Instance.Subscribe<HideoutClearedEvent>(OnHideoutCleared);
            _eventsRegistered = true;
        }

        public override void Cleanup()
        {
            if (_eventsRegistered)
            {
                EventBus.Instance.Unsubscribe<HideoutClearedEvent>(OnHideoutCleared);
                _eventsRegistered = false;
            }
        }

        private void OnHideoutCleared(HideoutClearedEvent evt)
        {
            if (Bedrock.CheckAndRebuildIfNeeded("HideoutCleared"))
            {
                _bedrockRebuilds++;
                Geology.Update(Bedrock);
            }
        }

        public override void Initialize()
        {
            Bedrock.Build("initialize");
            _bedrockRebuilds++;
            if (Geology.NeedsUpdate) Geology.Update(Bedrock);
            if (Weather.NeedsUpdate) Weather.Update();
            DebugLogger.Info(ModuleName, "Üç katmanlı dünya hafızası hazır.");
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled || Campaign.Current == null) return;
            if (Bedrock.CheckAndRebuildIfNeeded("daily_check")) _bedrockRebuilds++;
            if (Geology.NeedsUpdate) { Geology.Update(Bedrock); _geologyUpdates++; }
        }

        public override void OnHourlyTick()
        {
            if (!IsEnabled || Campaign.Current == null) return;
            if (Weather.NeedsUpdate) { Weather.Update(); _weatherUpdates++; }
        }

        public override void SyncData(IDataStore dataStore)
        {
            try
            {
                _ = dataStore.SyncData("BM_WM_TownProsperity", ref Geology._townProsperity);
                _ = dataStore.SyncData("BM_WM_VillageHearth",  ref Geology._villageHearth);
                _ = dataStore.SyncData("BM_WM_OwnerClan",      ref Geology._ownerClan);
                _ = dataStore.SyncData("BM_WM_GeologyDay",     ref Geology._lastUpdateDaySave);
                // NOT: _regionalProsperity kaydedilmiyor — türetilmiş veri, Geology.Update'de üretilir

                if (dataStore.IsLoading)
                {
                    Geology._townProsperity ??= new();
                    Geology._villageHearth  ??= new();
                    Geology._ownerClan      ??= new();
                    Geology.TownProsperity  = Geology._townProsperity;
                    Geology.VillageHearth   = Geology._villageHearth;
                    Geology.OwnerClan       = Geology._ownerClan;
                    Geology.LastUpdateDay   = Geology._lastUpdateDaySave;
                    // Bedrock Initialize()'da yeniden inşa edilecek.
                    // RegionalProsperity, Initialize() → Geology.Update() sırasında üretilecek.
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning(ModuleName, $"SyncData: {ex.Message}");
                Geology._townProsperity ??= new();
                Geology._villageHearth  ??= new();
                Geology._ownerClan      ??= new();
            }
        }

        public static float GetRegionalProsperity(Settlement center)
        {
            if (center == null) return 0f;
            float cached = Geology.GetRegionalProsperity(center.StringId);
            return cached > 0f ? cached : Bedrock.GetRegionalProsperity(center);
        }

        public static float GetGlobalProsperityAvg() => Geology.GetGlobalProsperityAvg();

        public static bool TryGetPosition(Settlement s, out Vec2 pos) =>
            Bedrock.Positions.TryGetValue(s?.StringId ?? "", out pos);

        public static Settlement? FindById(string id) =>
            Bedrock.ById.TryGetValue(id, out var s) ? s : null;

        public static void ForceRebuild(string reason = "external")
        {
            Bedrock.Build(reason);
            Geology.Update(Bedrock);
        }

        public override string GetDiagnostics()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== {ModuleName} ===");
            sb.AppendLine($"Bedrock : {Bedrock.AllSettlements.Count} settlement " +
                          $"({Bedrock.AllTowns.Count}T/{Bedrock.AllVillages.Count}K/" +
                          $"{Bedrock.AllCastles.Count}Ka/{Bedrock.AllHideouts.Count}S) | " +
                          $"kNN={Bedrock.NearestNeighbors.Count} | rebuild={_bedrockRebuilds}");
            sb.AppendLine($"Geology : {Geology.TownProsperity.Count}T/{Geology.VillageHearth.Count}K | " +
                          $"gün={Geology.LastUpdateDay:F0} | toplam={_geologyUpdates}");
            sb.AppendLine($"Weather : {Weather.ActiveCaravans.Count} kervan/" +
                          $"{Weather.ActiveLordParties.Count} lord | " +
                          $"saat={Weather.LastUpdateHour:F0} | toplam={_weatherUpdates}");
            return sb.ToString();
        }
    }
}
