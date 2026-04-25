// ============================================================
// Core/Neural/NervousSystem.cs
// BanditMilitias Dağıtık Sinir Sistemi — v1.0
//
// SORUN (Bu dosya olmadan):
//   • 45 ayrı sistem, her saatte ActiveMilitias'ı bağımsız tarıyor
//   • WarlordSystem.GetAllWarlords() 26 farklı yerden çağrılıyor
//   • PlayerTracker.GetThreatLevel(), FearSystem.GetSettlementFear()
//     her saat 4-6 kez yeniden hesaplanıyor
//   • Tüm modüller ProcessModuleTicks'te seri olarak çalışıyor
//   • Hiçbir sistem diğerinin ne kadar yüklü olduğunu bilmiyor
//
// ÇÖZÜM (Bu dosya ile):
//
//  1. SharedPercept — "Duyusal Anlık Görüntü"
//     Her saatin başında TEK bir tarama: tüm milislerin pozisyonu,
//     warlord listesi, tehdit seviyesi, korku durumu hesaplanır ve
//     salt-okunur bir tampona yazılır. Tüm sistemler bu tamponu okur.
//
//  2. DendriticPartitioner — "İş Bölüşümü"
//     ActiveMilitias listesi, kanalara (dendrite) deterministik olarak
//     bölünür. Her kanal belirli milis dilimini işler. Kanal sayısı
//     yüke göre dinamik artar (yüksek yük → daha fazla kanal).
//
//  3. GanglionGroup — "Sinir Düğümü"
//     İşlevsel olarak ilişkili sistemleri bir arada tutan grup.
//     • Sensory  : PlayerTracker, FearSystem, TerritorySystem
//     • Motor    : AIScheduler, LogisticsSystem, BehaviorSystem
//     • Associative : BanditBrain, SwarmCoordinator
//     • Autonomic   : Economy, Workshop, Seasonal, Crisis
//     Her grup kendi yük bütçesini yönetir; aşarsa Associative grubu yardıma gelir.
//
//  4. InhibitorySignal — "Çift İş Engeli"
//     Bir kanal milisi işlemeye başladığında, o milisi inhibitory
//     kümesine ekler. Diğer kanallar aynı milisi atlar.
//
//  5. MyelinCache — "Hızlı Yol Önbelleği"
//     Sık erişilen hesaplamalar (warlord güç skoru, tehdit mesafesi)
//     tick boyunca önbelleğe alınır. Bir sonraki tick'te geçerliliği
//     sona erer.
//
// Entegrasyon:
//   SubModule → NervousSystem.Instance kaydı
//   NervousSystem.OnHourlyTick → SharedPercept'i doldur → kanalları çalıştır
//   Mevcut sistemler: DOKUNULMAZ — sadece SharedPercept.Current okuma
//   eklenecek (isteğe bağlı, geriye uyumlu).
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BanditMilitias.Components;
using BanditMilitias;
using BanditMilitias.Core.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Neural;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Diagnostics;
using BanditMilitias.Systems.Fear;
using BanditMilitias.Systems.Logistics;
using BanditMilitias.Systems.Scheduling;
using BanditMilitias.Systems.Tracking;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Core.Neural
{
    // ══════════════════════════════════════════════════════════════
    // 1. SHARED PERCEPT — Duyusal Anlık Görüntü
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Bir saatlik tick'in başında TEK kez doldurulur.
    /// Tüm sistemlerin tekrar hesaplamak zorunda kaldığı pahalı sorguları
    /// önbelleğe alır. Sistemler bu nesneyi okur, EventBus sinyallerini değil.
    ///
    /// Örnek tasarruf:
    ///   GetAllWarlords() — 26 çağrı → 1 çağrı
    ///   GetThreatLevel() — 6 çağrı  → 1 çağrı
    /// </summary>
    public sealed class SharedPercept
    {
        // ── Anlık Görüntü Verisi ─────────────────────────────────
        public IReadOnlyList<Warlord>      AllWarlords      { get; private set; } = Array.Empty<Warlord>();
        public IReadOnlyList<MobileParty>  ActiveMilitias   { get; private set; } = Array.Empty<MobileParty>();
        public float                       ThreatLevel      { get; private set; }
        public bool                        IsHighLoad       { get; private set; }
        public CampaignTime                SnapshotTime     { get; private set; }
        public int                         TotalPartyCount  { get; private set; }

        // ── Korku & Bölge Özeti ───────────────────────────────────
        /// <summary>Key: WarlordStringId → ortalama korku skoru</summary>
        public IReadOnlyDictionary<string, float> WarlordFearIndex { get; private set; }
            = new Dictionary<string, float>();

        // ── Myelin Önbelleği (tick boyunca geçerli) ───────────────
        private readonly Dictionary<string, float>  _myelinFloat  = new();
        private readonly Dictionary<string, object> _myelinObject = new();

        public bool IsStale =>
            Campaign.Current == null ||
            (CampaignTime.Now - SnapshotTime).ToHours > 1.5;

        // ── Singleton ─────────────────────────────────────────────
        private static SharedPercept _current = new SharedPercept();
        public  static SharedPercept Current  => _current;

        // ── Doldurma ─────────────────────────────────────────────
        internal static void Refresh()
        {
            if (Campaign.Current == null) return;

            var next = new SharedPercept();
            next.SnapshotTime    = CampaignTime.Now;
            next.IsHighLoad      = DiagnosticsSystem.IsHighLoad;
            next.TotalPartyCount = Campaign.Current.MobileParties.Count;

            // Warlord listesi
            var ws = WarlordSystem.Instance;
            next.AllWarlords = ws != null
                ? (IReadOnlyList<Warlord>)ws.GetAllWarlords()  // GetAllWarlords zaten IsAlive filtreli
                : Array.Empty<Warlord>();

            // Aktif milisler
            var mm = ModuleManager.Instance;
            next.ActiveMilitias = mm?.ActiveMilitias ?? Array.Empty<MobileParty>();

            // Tehdit seviyesi
            var tracker = PlayerTracker.Instance;
            next.ThreatLevel = tracker != null ? tracker.GetThreatLevel() : 0f;

            // Warlord korku indeksi
            var fear = FearSystem.Instance;
            if (fear != null && next.AllWarlords.Count > 0)
            {
                var fearIdx = new Dictionary<string, float>(next.AllWarlords.Count);
                foreach (var w in next.AllWarlords)
                    fearIdx[w.StringId] = fear.GetAverageFearForWarlord(w.StringId);
                next.WarlordFearIndex = fearIdx;
            }

            // Atomik takas (eski percept'i bir anda yenisiyle değiştir)
            System.Threading.Interlocked.Exchange(ref _current, next);
        }

        // ── Myelin Önbellek Erişimi ───────────────────────────────
        public bool TryGetCached(string key, out float value)
            => _myelinFloat.TryGetValue(key, out value);

        public void Cache(string key, float value)
            => _myelinFloat[key] = value;

        public bool TryGetCachedObject<T>(string key, out T? value) where T : class
        {
            if (_myelinObject.TryGetValue(key, out var raw) && raw is T typed)
            { value = typed; return true; }
            value = default;
            return false;
        }

        public void CacheObject(string key, object value)
            => _myelinObject[key] = value;
    }

    // ══════════════════════════════════════════════════════════════
    // 2. DENDRITIC PARTITIONER — İş Bölüşümü
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// ActiveMilitias listesini, belirlenen kanal sayısına böler.
    /// Her kanal belirli bir milis dilimini işler. Bölme deterministiktir
    /// (aynı hash → aynı kanal) ve load-aware'dir (yüksek yük → daha küçük dilimler).
    /// </summary>
    public static class DendriticPartitioner
    {
        /// <summary>
        /// Bir milisin hangi kanala ait olduğunu belirler.
        /// Bannerlord StringId deterministiktir — kanal ataması tutarlı kalır.
        /// </summary>
        public static int GetChannel(MobileParty militia, int channelCount)
        {
            if (channelCount <= 1) return 0;
            int hash = Math.Abs(militia.StringId?.GetHashCode() ?? militia.GetHashCode());
            return hash % channelCount;
        }

        /// <summary>
        /// Bir gruptaki aktif milisleri channel dahilindeki dilimine döndürür.
        /// </summary>
        public static IEnumerable<MobileParty> GetSlice(
            IReadOnlyList<MobileParty> militias,
            int channel,
            int channelCount)
        {
            foreach (var m in militias)
            {
                if (m == null || !m.IsActive) continue;
                if (GetChannel(m, channelCount) == channel)
                    yield return m;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    // 3. INHIBITORY SIGNAL — Çift İş Engeli
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Aynı tick içinde birden fazla grubun ayný milisi işlemesini engeller.
    /// Bir grup milisi talep ettiğinde inhibitory set'e eklenir;
    /// diğer gruplar bu seti kontrol ederek atlar.
    ///
    /// Biyolojik karşılığı: lateral inhibition — bir nöron ateşlendiğinde
    /// komşularının aynı stimulus'a tepki vermesini bastırır.
    /// </summary>
    public sealed class InhibitorySignal
    {
        private readonly HashSet<string> _claimed = new HashSet<string>();

        /// <summary>Milisi talep et. Zaten talep edilmişse false döner.</summary>
        public bool TryClaim(MobileParty militia)
        {
            if (militia?.StringId == null) return false;
            return _claimed.Add(militia.StringId);
        }

        /// <summary>Tick sonunda tüm talepleri serbest bırak.</summary>
        public void Reset() => _claimed.Clear();

        public int ClaimedCount => _claimed.Count;
    }

    // ══════════════════════════════════════════════════════════════
    // 4. GANGLION GROUP — Sinir Düğümü
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// İşlevsel olarak ilişkili sistemleri kapsülleyen sinir düğümü.
    ///
    /// Dört düğüm tipi:
    ///   Sensory     → dünyayı algıla (PlayerTracker, Fear, Territory)
    ///   Associative → karar ver     (BanditBrain, Swarm)
    ///   Motor       → hareket et    (AIScheduler, Logistics, Behavior)
    ///   Autonomic   → arka plan     (Economy, Workshop, Crisis, Seasonal)
    ///
    /// Her düğüm:
    ///   • Kendi işlemci bütçesini takip eder
    ///   • Aşarsa yük_taşıyıcı (overflow target) gruba sinyal gönderir
    ///   • SharedPercept'i kirletmez — sadece okur
    /// </summary>
    public sealed class GanglionGroup
    {
        public string  Name         { get; }
        public int     TickBudget   { get; set; }   // Bu saatte işleyebileceği milis sayısı
        public int     Processed    { get; private set; }
        public float   LoadRatio    => TickBudget > 0 ? (float)Processed / TickBudget : 0f;
        public bool    IsOverloaded => LoadRatio > 0.85f;

        private GanglionGroup? _overflowTarget;
        private readonly List<Action<MobileParty, SharedPercept, InhibitorySignal>> _processors = new();
        private readonly List<Action<SharedPercept>>  _groupProcessors = new();

        public GanglionGroup(string name, int budget)
        {
            Name       = name;
            TickBudget = budget;
        }

        // ── Bağlantılar ───────────────────────────────────────────

        /// <summary>Yük taşıyıcı: Bu grup aşırı yüklendiğinde hedef gruba devret.</summary>
        public GanglionGroup SetOverflowTarget(GanglionGroup target)
        { _overflowTarget = target; return this; }

        // ── İşleyici Kayıtları ────────────────────────────────────

        /// <summary>Milis başına çalışacak işleyici ekle.</summary>
        public GanglionGroup AddProcessor(Action<MobileParty, SharedPercept, InhibitorySignal> fn)
        { _processors.Add(fn); return this; }

        /// <summary>Grup düzeyinde (tüm milislerden önce) çalışacak işleyici ekle.</summary>
        public GanglionGroup AddGroupProcessor(Action<SharedPercept> fn)
        { _groupProcessors.Add(fn); return this; }

        // ── Tick ─────────────────────────────────────────────────

        public void Tick(SharedPercept percept, InhibitorySignal inhibitory, int channel, int channelCount)
        {
            Processed = 0;
            foreach (var gp in _groupProcessors)
            {
                try { gp(percept); }
                catch (Exception ex) { DebugLogger.Warning(Name, $"GroupProcessor: {ex.Message}"); }
            }
            TickMilitias(percept, inhibitory, channel, channelCount);
        }

        /// <summary>Sadece grup düzeyindeki işlemleri çalıştır (milis başına değil).</summary>
        public void TickGroupOnly(SharedPercept percept)
        {
            foreach (var gp in _groupProcessors)
            {
                try { gp(percept); }
                catch (Exception ex) { DebugLogger.Warning(Name, $"GroupProcessor: {ex.Message}"); }
            }
        }

        /// <summary>Sadece milis dilimini işle (grup işlemleri hariç).</summary>
        public void TickMilitias(SharedPercept percept, InhibitorySignal inhibitory, int channel, int channelCount)
        {
            var slice = DendriticPartitioner.GetSlice(
                percept.ActiveMilitias, channel, channelCount);

            foreach (var militia in slice)
            {
                // Yük aşıldıysa overflow grubuna devret
                if (IsOverloaded)
                {
                    _overflowTarget?.AcceptOverflow(militia, percept, inhibitory);
                    continue;
                }

                if (!inhibitory.TryClaim(militia)) continue;

                foreach (var proc in _processors)
                {
                    try { proc(militia, percept, inhibitory); }
                    catch (Exception ex)
                    { DebugLogger.Warning(Name, $"Processor[{militia.Name}]: {ex.Message}"); }
                }

                Processed++;
            }
        }

        /// <summary>Overflow alımı: Başka bir grubun sığdıramadığı milisleri işle.</summary>
        public void AcceptOverflow(MobileParty militia, SharedPercept percept, InhibitorySignal inhibitory)
        {
            if (!inhibitory.TryClaim(militia)) return;
            foreach (var proc in _processors)
            {
                try { proc(militia, percept, inhibitory); }
                catch (Exception ex)
                { DebugLogger.Warning($"{Name}[overflow]", $"{militia.Name}: {ex.Message}"); }
            }
            Processed++;
        }

        public void Reset() => Processed = 0;

        public string GetDiagnostics() =>
            $"[{Name,-14}] budget={TickBudget,3} done={Processed,3} " +
            $"load={LoadRatio:P0} overloaded={IsOverloaded}";
    }

    // ══════════════════════════════════════════════════════════════
    // 5. NERVOUS SYSTEM — Ana Koordinatör
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Tüm sistemleri dört işlevsel düğüm grubuna organize eder ve
    /// her saatlik tick'i dört aşamada yönetir:
    ///
    ///   Faz 1 — Algı  : SharedPercept doldurulur (tek tarama).
    ///   Faz 2 — Duyum : Sensory grubu tehdit/korku sinyallerini işler.
    ///   Faz 3 — Karar : Associative grubu kanallar üzerinden bölünmüş çalışır.
    ///   Faz 4 — Hareket: Motor grubu kararları çalıştırır.
    ///   Faz 5 — Otonom : Economy/Workshop/Seasonal arka planda çalışır.
    ///
    /// Motor gruplar Associative'den taşan işleri alabilir (inhibitory engel ile).
    ///
    /// Kanal Sayısı:
    ///   Normal yük → 2 kanal (milislerin %50'si/kanal)
    ///   Yüksek yük → 3 kanal (milislerin %33'ü/kanal)
    ///   Kritik yük → 4 kanal (sadece %25'i/kanal, çerçeve zamanı korunur)
    /// </summary>
    [AutoRegister]
    public sealed class NervousSystem : MilitiaModuleBase
    {
        // ── Singleton ─────────────────────────────────────────────
        private static readonly Lazy<NervousSystem> _inst = new(() => new NervousSystem());
        public static NervousSystem Instance => _inst.Value;
        private NervousSystem() { }

        public override string ModuleName => "NervousSystem";
        public override bool   IsEnabled  => Settings.Instance?.EnableWarlords ?? true;
        public override bool   IsCritical => false;
        public override int    Priority   => 98;   // NeuralCortex(99)'dan sonra, BanditBrain(95)'den önce

        // ── Gruplar ───────────────────────────────────────────────
        private GanglionGroup _sensory     = null!;
        private GanglionGroup _associative = null!;
        private GanglionGroup _motor       = null!;
        private GanglionGroup _autonomic   = null!;

        // ── İnhibitory Sinyal ─────────────────────────────────────
        private readonly InhibitorySignal _inhibitory = new InhibitorySignal();

        // ── Tanı ──────────────────────────────────────────────────
        private long _tickCount          = 0;
        private long _totalMilitiasServed = 0;
        private long _totalOverflows      = 0;
        private readonly Stopwatch _sw    = new Stopwatch();

        // ── Kalıcı Durum ──────────────────────────────────────────
        private long   _savedTickCount     = 0;
        private long   _savedMilitiasServed = 0;

        // ── Başlatma ──────────────────────────────────────────────
        public override void Initialize()
        {
            BuildGroups();
            WireOverflow();

            // ── Neural AI Başlatımı (geliştirici toggle, varsayılan KAPALI) ──
            try
            {
                bool neuralEnabled = Settings.Instance?.EnableNeuralAI ?? false;
                string weightsDir = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord", "Warlord_Logs", "BanditMilitias", "Neural");

                var advisor = NeuralAdvisor.CreateInstance();
                advisor.Initialize(weightsDir, neuralEnabled);

                // DevDataCollector için export dizinini ayarla
                NeuralDataExporter.SetExportDirectory(
                    System.IO.Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
                        "Mount and Blade II Bannerlord", "Warlord_Logs", "BanditMilitias", "AI", "exports"));

                DebugLogger.Info(ModuleName,
                    $"Neural AI: {(neuralEnabled ? "ACTIVE" : "PASSIVE (data collection only)")}");
            }
            catch (System.Exception ex)
            {
                DebugLogger.Warning(ModuleName, $"Neural AI init failed: {ex.Message}");
            }

            DebugLogger.Info(ModuleName,
                "Sinir sistemi kuruldu: 4 ganglion grubu, inhibitory sinyal aktif.");
        }

        private void BuildGroups()
        {
            // ── Sensory: Dünyayı algıla ──────────────────────────────
            // FearSystem, TerritorySystem, PlayerTracker'ın güncelleme
            // sorumluluğunu tek çatı altında toplar.
            // Sensory: SharedPercept güncelleme + öncelik AI işaretçilerini temizle
            _sensory = new GanglionGroup("Sensory", budget: 1) // GroupProcessor çalıştırır, milis işlemez; budget=1 diagnostics'te anlamlı görünsün
                .AddGroupProcessor(static (p) =>
                {
                    // SharedPercept zaten OnHourlyTick başında Refresh() edildi.
                    // Burada: AI scheduler'a yük bilgisi ilet.
                    // Yük yüksekse AI scheduler bütçeyi yarıya indirecek (zaten yapıyor).
                    // Ekstra: PlayerTracker tehdit bilgisini percept'te güncelle.
                    if (p.ThreatLevel > 0.7f)
                    {
                        // Yüksek tehdit: Tüm warlord'ların milis kuyruklarını urgent'a al
                        var scheduler = ModuleManager.Instance?.GetModule<AISchedulerSystem>();
                        if (scheduler == null) return;
                        foreach (var w in p.AllWarlords)
                        {
                            if (w?.CommandedMilitias == null) continue;
                            foreach (var m in w.CommandedMilitias)
                            {
                                if (m?.IsActive == true)
                                    scheduler.EnqueueDecision(m, urgent: true);
                            }
                        }
                    }
                });

            // ── Associative: Karar ver ───────────────────────────────
            // En ağır grup — BanditBrain + Swarm mantığını
            // milis başına çalıştırır. Kanallar aracılığıyla bölünür.
            _associative = new GanglionGroup("Associative", budget: 60)
                .AddProcessor(static (militia, percept, inhibitory) =>
                {
                    // BanditBrain: Yeni milis spawn oldu veya tehdit haritası güncellendi
                    // Burada hafif bir hint yeterli; ağır karar BanditBrain.OnHourlyTick'te.
                    // Bu satır gelecekte BanditBrain'e "bu milisi öncelikle ele al" sinyali verebilir.
                    var comp = militia.PartyComponent as MilitiaPartyComponent;
                    if (comp?.IsPriorityAIUpdate == true)
                    {
                        var scheduler = ModuleManager.Instance?.GetModule<AISchedulerSystem>();
                        scheduler?.EnqueueDecision(militia, urgent: true);
                        comp.IsPriorityAIUpdate = false;
                    }
                });

            // ── Motor: Harekete geç ──────────────────────────────────
            // Lojistik ve davranış sistemleri için: üretilen kararları uygula.
            // Associative'den taşan milisleri alabilir.
            _motor = new GanglionGroup("Motor", budget: 80)
                .AddProcessor(static (militia, percept, inhibitory) =>
                {
                    // LogisticsSystem'in her milise ProcessMilitiaLogistics çağrısını
                    // burada merkezi olarak yapmak — ama LogisticsSystem zaten
                    // OnHourlyTick'inde döngü yapıyor. Bu kanal, LogisticsSystem'in
                    // overflow'unu alacak şekilde ileride refactor edilebilir.
                    // Şimdilik: yüksek yükü algıla ve lojistiği ertele.
                    if (!percept.IsHighLoad) return;

                    var comp = militia.PartyComponent as MilitiaPartyComponent;
                    if (comp == null) return;

                    // Yüksek yük → sadece kritik lojistik (yiyecek < 10 birim)
                    if (militia.FoodChange < -5f && militia.ItemRoster != null)
                    {
                        var logistics = ModuleManager.Instance
                            ?.GetModule<WarlordLogisticsSystem>();
                        // Mark for priority restock — logistics sistemi bu bayrağı okur
                        comp.CurrentState = MilitiaPartyComponent.WarlordState.Restocking;
                    }
                });

            // ── Autonomic: Arka plan ─────────────────────────────────
            // Economy, Workshop, Seasonal, Crisis — bunlar milis başına değil,
            // warlord başına veya saf grup bazlı çalışır. Yük yüksekse ertelenir.
            _autonomic = new GanglionGroup("Autonomic", budget: 20)
                .AddGroupProcessor(static (p) =>
                {
                    // Yük yüksekse atla — otonom işler ertelenebilir
                    if (p.IsHighLoad) return;

                    // Gelecekteki otonom işleyiciler için yer tutucu
                });
        }

        private void WireOverflow()
        {
            // Associative aşırı yüklendiğinde → Motor alır
            _associative.SetOverflowTarget(_motor);
            // Motor aşırı yüklendiğinde → Autonomic alır (düşük öncelikli işler ertelenir)
            _motor.SetOverflowTarget(_autonomic);
        }

        // ── Tick Döngüsü ──────────────────────────────────────────

        public override void OnHourlyTick()
        {
            if (!IsEnabled || Campaign.Current == null) return;
            _sw.Restart();

            // ── FAZ 0: NeuralEventRouter — nöron bütçelerini yüke göre ayarla ──
            // SharedPercept.Refresh()'ten ÖNCE çağrılır; önceki tick'in yük bilgisini
            // kullanarak yeni tick bütçelerini belirler ve nöronları sıfırlar.
            NeuralEventRouter.Instance.OnHourlyTick();

            // ── Neural: Tick başında inference sayacını sıfırla ──
            NeuralAdvisor.Instance?.OnTickReset();

            // FIX: Warlord yokken NeuralAdvisor tamamen uykuya al — CPU sıfır tüketim.
            // Warlord varsa yük seviyesine göre bütçe ata.
            if (NeuralAdvisor.Instance != null)
            {
                bool hasWarlords = SharedPercept.Current?.AllWarlords?.Count > 0;
                if (!hasWarlords)
                {
                    // Tier 0–1: Warlord henüz yok, neural inference gereksiz — tam uyku
                    NeuralAdvisor.Instance.SetMaxInferencesPerTick(0);
                }
                else
                {
                    bool isHigh = DiagnosticsSystem.IsHighLoad;
                    int neuralBudget = isHigh ? 10 : 50;  // Yüksek yük: 10, Normal: 50
                    NeuralAdvisor.Instance.SetMaxInferencesPerTick(neuralBudget);
                }
            }

            // Kanal sayısını yüke göre belirle
            int channelCount = DetermineChannelCount();

            // ── FAZ 1: Algı — SharedPercept Güncelle ────────────────
            SharedPercept.Refresh();
            var percept = SharedPercept.Current;

            // ── FAZ 2–5: Kanallar Üzerinden Grupları Çalıştır ───────
            _inhibitory.Reset();

            // ── FAZ 2: Sensory Grup İşlemleri — SADECE BİR KEZ ──────
            // GroupProcessor'lar kanal sayısından bağımsız, tick başına tek çalışır.
            _sensory.TickGroupOnly(percept);
            _autonomic.TickGroupOnly(percept);

            // ── FAZ 3–4: Milis Dilimi İşleme — Her Kanal İçin ───────
            // Associative ve Motor, milisleri kanallara bölerek işler.
            for (int ch = 0; ch < channelCount; ch++)
            {
                _associative.TickMilitias(percept, _inhibitory, ch, channelCount);
                _motor.TickMilitias(percept, _inhibitory, ch, channelCount);
            }

            // ── Tanı Sayaçları ─────────────────────────────────────
            _tickCount++;
            int served = _inhibitory.ClaimedCount;
            _totalMilitiasServed += served;

            long overflows = (_associative.IsOverloaded ? 1L : 0L)
                           + (_motor.IsOverloaded        ? 1L : 0L);
            _totalOverflows += overflows;

            _sw.Stop();

            if (Settings.Instance?.TestingMode == true && _sw.ElapsedMilliseconds > 3)
            {
                DebugLogger.Info(ModuleName,
                    $"Tick #{_tickCount} | ch={channelCount} | " +
                    $"served={served} | {_sw.ElapsedMilliseconds}ms");
            }
        }

        public override void OnDailyTick()
        {
            // Günlük sıfırlama: Grup bütçelerini dinamik yenile
            var percept = SharedPercept.Current;
            int baseBudget = percept.ActiveMilitias.Count;

            _associative.TickBudget = Math.Max(20, baseBudget / 2);
            _motor.TickBudget       = Math.Max(30, baseBudget);
            _autonomic.TickBudget   = Math.Max(10, baseBudget / 4);

            _savedTickCount      = _tickCount;
            _savedMilitiasServed = _totalMilitiasServed;
        }

        // ── Kanal Sayısı Hesabı ───────────────────────────────────

        private static int DetermineChannelCount()
        {
            bool isHigh    = DiagnosticsSystem.IsHighLoad;
            int  partyCount = Campaign.Current?.MobileParties.Count ?? 0;

            if (isHigh && partyCount > 2000) return 4;
            if (isHigh || partyCount > 1500) return 3;
            return 2;
        }

        // ── SyncData ──────────────────────────────────────────────

        public override void SyncData(IDataStore dataStore)
        {
            try
            {
                _ = dataStore.SyncData("BM_NS_TickCount",    ref _savedTickCount);
                _ = dataStore.SyncData("BM_NS_MilsServed",   ref _savedMilitiasServed);
            }
            catch (Exception ex)
            {
                DebugLogger.Warning(ModuleName, $"SyncData: {ex.Message}");
            }
        }

        // ── Diagnostics ───────────────────────────────────────────

        public override string GetDiagnostics()
        {
            if (_sensory == null || _associative == null)
                return $"{ModuleName}: Initializing ganglion groups...";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== {ModuleName} (Tick #{_tickCount}) ===");
            sb.AppendLine($"SharedPercept: {SharedPercept.Current?.ActiveMilitias?.Count ?? 0} milis, " +
                          $"tehdit={SharedPercept.Current?.ThreatLevel ?? 0:P0}, " +
                          $"warlord={SharedPercept.Current?.AllWarlords?.Count ?? 0}");
            sb.AppendLine($"Toplam işlenen: {_totalMilitiasServed} milis, {_totalOverflows} overflow");
            sb.AppendLine(_sensory.GetDiagnostics());
            sb.AppendLine(_associative.GetDiagnostics());
            sb.AppendLine(_motor.GetDiagnostics());
            sb.AppendLine(_autonomic.GetDiagnostics());

            // Neural AI diagnostics
            var advisor = NeuralAdvisor.Instance;
            if (advisor != null)
            {
                sb.AppendLine($"Neural AI: {(advisor.IsEnabled ? "ACTIVE" : "PASSIVE")}");
                sb.AppendLine($"  Confidence: {advisor.GlobalConfidence:F2}");
                sb.AppendLine($"  Inferences: {advisor.TotalInferences}");
                sb.AppendLine($"  Training: {advisor.TotalTrainingBatches} batches");
            }

            // NeuralEventRouter diagnostics
            sb.AppendLine(NeuralEventRouter.Instance.GetDiagnostics());

            return sb.ToString();
        }

        // ── Dış Erişim: Diğer Sistemler SharedPercept Okuyabilir ──

        /// <summary>
        /// Mevcut sistemlerin SharedPercept'i okuyarak tekrar hesaplamaları
        /// önlemesi için statik kısa yol.
        ///
        /// Örnek kullanım (BanditBrain.cs içinde):
        ///   var allWarlords = NervousSystem.Percept.AllWarlords;
        ///   // yerine:
        ///   // var allWarlords = WarlordSystem.Instance.GetAllWarlords();
        /// </summary>
        public static SharedPercept Percept => SharedPercept.Current;

        /// <summary>
        /// Myelin kısa yolu: Pahalı bir float değer önbellekten al veya hesapla.
        /// </summary>
        public static float GetOrCompute(string key, Func<float> compute)
        {
            var p = SharedPercept.Current;
            if (p.TryGetCached(key, out float val)) return val;
            val = compute();
            p.Cache(key, val);
            return val;
        }
    }
}
