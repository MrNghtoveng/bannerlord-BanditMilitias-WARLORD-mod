using BanditMilitias.Core.Components;
using BanditMilitias.Infrastructure;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BanditMilitias.Intelligence.AI.Components
{
    // ── StaticDataCache ───────────────────────────────────────────
    /// <summary>
    /// Oturum boyunca degismeyen settlement listelerini RAM'de tutar.
    /// Settlement.All her cagrildiginda O(n) tarama yapar — bunu once yap.
    /// </summary>
    [BanditMilitias.Core.Components.AutoRegister]
    public class StaticDataCache : MilitiaModuleBase
    {
        private static StaticDataCache? _instance;
        public static StaticDataCache Instance =>
            _instance ??= ModuleManager.Instance.GetModule<StaticDataCache>() ?? new StaticDataCache();

        public override string ModuleName => "StaticDataCache";
        public override bool IsEnabled => true;
        public override int Priority => 10;

        public IReadOnlyList<Settlement> AllHideouts { get { EnsureCacheLoaded(); return _allHideouts; } }
        public IReadOnlyList<Settlement> AllVillages { get { EnsureCacheLoaded(); return _allVillages; } }
        public IReadOnlyList<Settlement> AllTowns { get { EnsureCacheLoaded(); return _allTowns; } }
        public IReadOnlyList<Settlement> AllCastles { get { EnsureCacheLoaded(); return _allCastles; } }

        private readonly List<Settlement> _allHideouts = new();
        private readonly List<Settlement> _allVillages = new();
        private readonly List<Settlement> _allTowns = new();
        private readonly List<Settlement> _allCastles = new();

        private bool _isCacheLoaded = false;
        public override void Initialize()
            => CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, SafeRefreshCacheOnSessionLaunched);

        private void SafeRefreshCacheOnSessionLaunched(CampaignGameStarter _)
        {
            _isCacheLoaded = false;
            try 
            {
                // Session launched anında temizle ama hemen doldurma (çakışmaları önlemek için)
                // Gerektiğinde EnsureCacheLoaded üzerinden otomatik yüklenecektir.
            }
            catch (System.Exception ex)
            {
                Debug.DebugLogger.Error("StaticDataCache", $"OnSessionLaunched cache refresh failed: {ex.Message}");
            }
        }

        private void EnsureCacheLoaded()
        {
            if (_isCacheLoaded || Campaign.Current == null) return;
            RefreshCache();
        }

        public void RefreshCache()
        {
            _allHideouts.Clear(); _allVillages.Clear();
            _allTowns.Clear(); _allCastles.Clear();

            if (Campaign.Current == null) return;

            foreach (var s in Settlement.All)
            {
                if (s == null) continue;
                if (s.IsHideout) _allHideouts.Add(s);
                else if (s.IsVillage) _allVillages.Add(s);
                else if (s.IsTown) _allTowns.Add(s);
                else if (s.IsCastle) _allCastles.Add(s);
            }

            _isCacheLoaded = true;
            Debug.DebugLogger.Info("StaticDataCache",
                $"Yenilendi: {_allHideouts.Count} Hideout, {_allVillages.Count} Köy, " +
                $"{_allTowns.Count} Şehir, {_allCastles.Count} Kale");

            // Settlement'lar hazır — mesafe tablosunu sıfırla
            SettlementDistanceCache.Instance.Invalidate();
        }

        public override void Cleanup()
        {
            _allHideouts.Clear(); _allVillages.Clear();
            _allTowns.Clear(); _allCastles.Clear();
            _isCacheLoaded = false;
            CampaignEvents.OnSessionLaunchedEvent.ClearListeners(this);
        }

        public override string GetDiagnostics()
            => $"StaticCache: {_isCacheLoaded} | {_allHideouts.Count}H {_allVillages.Count}V {_allTowns.Count}T {_allCastles.Count}C";
    }

    // ── SettlementDistanceCache ───────────────────────────────────
    /// <summary>
    /// Bannerlord'un NavMesh mesafe tablosunu taklit eder.
    ///
    /// Settlement'lar sabittir — Vec2.Distance hesabini bir kez yap, sonsuza dek oku.
    /// Sorgu: O(1) dictionary lookup (lazy populate).
    ///
    /// NOT: TaleWorlds'ün NavMesh API'si modlara kapalı. Vec2.Distance
    /// gerçek yol mesafesi değil kuş uçuşu mesafesidir — ama single lookup
    /// vs. her çağrıda hesaplama olarak hâlâ net kazanç sağlar.
    /// </summary>
    public class SettlementDistanceCache
    {
        private static readonly SettlementDistanceCache _instance = new();
        public static SettlementDistanceCache Instance => _instance;

        // Key: settlement StringId çifti (alfabetik sıra, tutarlı hash için)
        private readonly Dictionary<long, float> _distTable = new(2048);
        private bool _ready;

        public void Invalidate()
        {
            _distTable.Clear();
            _ready = false;
        }

        /// <summary>
        /// İki settlement arasındaki mesafeyi döndürür.
        /// İlk sorguda hesaplanır, sonraki sorgu O(1).
        /// </summary>
        public float GetDistance(Settlement a, Settlement b)
        {
            if (a == null || b == null) return float.MaxValue;
            if (a == b) return 0f;

            long key = MakeKey(a, b);
            if (_distTable.TryGetValue(key, out float cached))
                return cached;

            float dist = CompatibilityLayer.GetSettlementPosition(a)
                         .Distance(CompatibilityLayer.GetSettlementPosition(b));
            _distTable[key] = dist;
            _ready = true;
            return dist;
        }

        public string GetDiagnostics() => $"Count={_distTable.Count}, Ready={_ready}";
        /// <summary>
        /// Verilen konuma en yakın hideout'u döndürür.
        /// StaticDataCache listesi üzerinden çalışır — O(n) ama n küçük.
        /// </summary>
        public Settlement? FindNearestHideout(Vec2 position)
        {
            Settlement? best = null;
            float bestSq = float.MaxValue;

            foreach (var s in StaticDataCache.Instance.AllHideouts)
            {
                if (s == null || !s.IsActive) continue;
                float dSq = CompatibilityLayer.GetSettlementPosition(s).DistanceSquared(position);
                if (dSq < bestSq) { bestSq = dSq; best = s; }
            }
            return best;
        }

        /// <summary>
        /// Verilen konuma en yakın şehri döndürür.
        /// </summary>
        public Settlement? FindNearestTown(Vec2 position)
        {
            Settlement? best = null;
            float bestSq = float.MaxValue;

            foreach (var s in StaticDataCache.Instance.AllTowns)
            {
                if (s == null || !s.IsActive) continue;
                float dSq = CompatibilityLayer.GetSettlementPosition(s).DistanceSquared(position);
                if (dSq < bestSq) { bestSq = dSq; best = s; }
            }
            return best;
        }

        // Çift sıralı string id → tek long key (sıra bağımsız)
        private static long MakeKey(Settlement a, Settlement b)
        {
            // StringId hash kullan — string karşılaştırma gereksiz
            int ha = a.StringId.GetHashCode();
            int hb = b.StringId.GetHashCode();
            if (ha > hb) { int tmp = ha; ha = hb; hb = tmp; }
            return ((long)(uint)ha << 32) | (uint)hb;
        }

        public int CacheSize => _distTable.Count;
    }

    // ── MilitiaSmartCache ─────────────────────────────────────────
    public class MilitiaSmartCache
    {
        private static readonly MilitiaSmartCache _instance = new();
        public static MilitiaSmartCache Instance => _instance;

        private const double CACHE_TTL_HOURS = 3.0;
        private const int MAX_CACHE_SIZE = 200;

        public struct CachedDecision
        {
            public BanditMilitias.Intelligence.Strategic.AIDecisionType Decision;
            public Settlement? TargetSettlement;
            public MobileParty? ThreatParty;
            public CampaignTime Timestamp;
        }

        private readonly Dictionary<MobileParty, CachedDecision> _decisionCache = new();

        public bool TryGetDecision(MobileParty party, float unused, out CachedDecision decision)
        {
            if (!_decisionCache.TryGetValue(party, out decision)) return false;
            if ((CampaignTime.Now - decision.Timestamp).ToHours > CACHE_TTL_HOURS)
            {
                _decisionCache.Remove(party);
                decision = default;
                return false;
            }
            return true;
        }

        public void CacheDecision(MobileParty party,
            BanditMilitias.Intelligence.Strategic.AIDecisionType type,
            CampaignTime time,
            Settlement? targetSettlement = null,
            MobileParty? targetParty = null)
        {
            EnforceSizeLimit();
            _decisionCache[party] = new CachedDecision
            {
                Decision = type,
                Timestamp = time,
                TargetSettlement = targetSettlement,
                ThreatParty = targetParty
            };
        }

        public BanditMilitias.Intelligence.Strategic.AIDecision? Get(MobileParty party)
        {
            if (!TryGetDecision(party, 0f, out var c)) return null;
            return new BanditMilitias.Intelligence.Strategic.AIDecision
            {
                Action = c.Decision,
                Score = 0f,
                Timestamp = c.Timestamp
            };
        }

        public void Set(MobileParty party, BanditMilitias.Intelligence.Strategic.AIDecision d)
            => CacheDecision(party,
                (BanditMilitias.Intelligence.Strategic.AIDecisionType)(int)d.Action,
                d.Timestamp);

        public void GetNearbyParties(Vec2 position, float radius, List<MobileParty> results)
        {
            if (results == null || Campaign.Current == null) return;
            var seen = new HashSet<MobileParty>(results);

            try
            {
                var grid = ModuleManager.Instance.GetModule<BanditMilitias.Systems.Grid.SpatialGridSystem>();
                if (grid != null)
                {
                    int before = results.Count;
                    grid.QueryNearby(position, radius, results);
                    for (int i = results.Count - 1; i >= before; i--)
                    {
                        if (results[i] == null || !seen.Add(results[i]))
                            results.RemoveAt(i);
                    }
                    
                    // BUG-2 Fix: Grid bozuk değilse (yoklama yapıldıysa ve boş dönmüşse)
                    // Linear Scan maskelemesine düşme ve erken dön. Linear scan CPU leak önlendi.
                    if (!grid.IsEmpty) 
                        return;
                }
            }
            catch (System.Exception ex)
            {
                Debug.DebugLogger.Warning("SmartCache", $"Grid sorgusu başarısız: {ex.Message}");
            }

            float rSq = radius * radius;
            foreach (MobileParty p in Campaign.Current.MobileParties)
            {
                if (p == null || !p.IsActive) continue;
                if (CompatibilityLayer.GetPartyPosition(p).DistanceSquared(position) <= rSq
                    && seen.Add(p))
                    results.Add(p);
            }
        }

        public int GetActiveMilitiaCount() => _decisionCache.Count;
        public void Initialize() => Clear();
        public void Remove(MobileParty p) => _decisionCache.Remove(p);
        public void Clear() => _decisionCache.Clear();

        private void EnforceSizeLimit()
        {
            if (_decisionCache.Count < MAX_CACHE_SIZE) return;
            MobileParty? oldest = null;
            var oldestTs = CampaignTime.Zero;
            bool first = true;
            foreach (var kv in _decisionCache)
            {
                if (first || kv.Value.Timestamp < oldestTs)
                {
                    oldestTs = kv.Value.Timestamp;
                    oldest = kv.Key;
                    first = false;
                }
            }
            if (oldest != null) _ = _decisionCache.Remove(oldest);
        }
    }
}
