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
using BanditMilitias.Systems.Cleanup;
using BanditMilitias.Intelligence.Swarm;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Core.Neural
{


    public sealed class SharedPercept
    {


        public IReadOnlyList<Warlord>      AllWarlords      { get; private set; } = Array.Empty<Warlord>();
        public IReadOnlyList<MobileParty>  ActiveMilitias   { get; private set; } = Array.Empty<MobileParty>();
        public float                       ThreatLevel      { get; private set; }
        public bool                        IsHighLoad       { get; private set; }
        public CampaignTime                SnapshotTime     { get; private set; }
        public int                         TotalPartyCount  { get; private set; }


        public IReadOnlyDictionary<string, float> WarlordFearIndex { get; private set; }
            = new Dictionary<string, float>();


        private readonly Dictionary<string, float>  _myelinFloat  = new();
        private readonly Dictionary<string, object> _myelinObject = new();

        public bool IsStale =>
            Campaign.Current == null ||
            (CampaignTime.Now - SnapshotTime).ToHours > 1.5;


        private static SharedPercept _current = new SharedPercept();
        private static SharedPercept _back    = new SharedPercept();
        public  static SharedPercept Current  => _current;

        /// <summary>Clears mutable state for reuse in the double-buffer swap.</summary>
        private void PrepareForReuse()
        {
            AllWarlords      = Array.Empty<Warlord>();
            ActiveMilitias   = Array.Empty<MobileParty>();
            ThreatLevel      = 0f;
            IsHighLoad       = false;
            TotalPartyCount  = 0;
            _myelinFloat.Clear();
            _myelinObject.Clear();
            // WarlordFearIndex dictionary is reused in-place below
        }


        internal static void Refresh()
        {
            if (Campaign.Current == null) return;

            // Double-buffer: reuse the back buffer instead of allocating new
            var next = _back;
            next.PrepareForReuse();

            next.SnapshotTime    = CampaignTime.Now;
            next.IsHighLoad      = DiagnosticsSystem.IsHighLoad;
            next.TotalPartyCount = Campaign.Current.MobileParties.Count;


            var ws = WarlordSystem.Instance;
            next.AllWarlords = ws != null
                ? (IReadOnlyList<Warlord>)ws.GetAllWarlords()

                : Array.Empty<Warlord>();


            var mm = ModuleManager.Instance;
            next.ActiveMilitias = mm?.ActiveMilitias ?? Array.Empty<MobileParty>();


            var tracker = PlayerTracker.Instance;
            next.ThreatLevel = tracker != null ? tracker.GetThreatLevel() : 0f;


            var fear = FearSystem.Instance;
            if (fear != null && next.AllWarlords.Count > 0)
            {
                // Reuse dictionary if it's already a mutable Dictionary
                Dictionary<string, float> fearIdx;
                if (next.WarlordFearIndex is Dictionary<string, float> existing)
                {
                    existing.Clear();
                    fearIdx = existing;
                }
                else
                {
                    fearIdx = new Dictionary<string, float>(next.AllWarlords.Count);
                }
                foreach (var w in next.AllWarlords)
                    fearIdx[w.StringId] = fear.GetAverageFearForWarlord(w.StringId);
                next.WarlordFearIndex = fearIdx;
            }

            // Atomic swap: old current becomes back buffer for next cycle
            var old = System.Threading.Interlocked.Exchange(ref _current, next);
            _back = old;
        }


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


    public static class DendriticPartitioner
    {


        public static int GetChannel(MobileParty militia, int channelCount)
        {
            if (channelCount <= 1) return 0;
            int hash = Math.Abs(militia.StringId?.GetHashCode() ?? militia.GetHashCode());
            return hash % channelCount;
        }


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


    public sealed class InhibitorySignal
    {
        private readonly HashSet<string> _claimed = new HashSet<string>();


        public bool TryClaim(MobileParty militia)
        {
            if (militia?.StringId == null) return false;
            return _claimed.Add(militia.StringId);
        }


        public void Reset() => _claimed.Clear();

        public int ClaimedCount => _claimed.Count;
    }


    public sealed class GanglionGroup
    {
        public string  Name         { get; }
        public int     TickBudget   { get; set; }

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


        public GanglionGroup SetOverflowTarget(GanglionGroup target)
        { _overflowTarget = target; return this; }


        public GanglionGroup AddProcessor(Action<MobileParty, SharedPercept, InhibitorySignal> fn)
        { _processors.Add(fn); return this; }


        public GanglionGroup AddGroupProcessor(Action<SharedPercept> fn)
        { _groupProcessors.Add(fn); return this; }


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


        public void TickGroupOnly(SharedPercept percept)
        {
            foreach (var gp in _groupProcessors)
            {
                try { gp(percept); }
                catch (Exception ex) { DebugLogger.Warning(Name, $"GroupProcessor: {ex.Message}"); }
            }
        }


        public void TickMilitias(SharedPercept percept, InhibitorySignal inhibitory, int channel, int channelCount)
        {
            var slice = DendriticPartitioner.GetSlice(
                percept.ActiveMilitias, channel, channelCount);

            foreach (var militia in slice)
            {


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


    [BanditMilitias.Core.Components.AutoRegister(Priority = 440, IsCritical = false)]
    public class NervousSystem : MilitiaModuleBase
    {


        private static readonly Lazy<NervousSystem> _inst = new(() => new NervousSystem());
        public static NervousSystem Instance => _inst.Value;
        private NervousSystem() { }

        public override string ModuleName => "NervousSystem";
        public override bool   IsEnabled  => Settings.Instance?.EnableWarlords ?? true;
        public override bool   IsCritical => false;
        public override int    Priority   => 98;


        private GanglionGroup _sensory     = null!;
        private GanglionGroup _associative = null!;
        private GanglionGroup _motor       = null!;
        private GanglionGroup _autonomic   = null!;


        private readonly InhibitorySignal _inhibitory = new InhibitorySignal();


        private long _tickCount          = 0;
        private long _totalMilitiasServed = 0;
        private long _totalOverflows      = 0;
        private readonly Stopwatch _sw    = new Stopwatch();


        private long   _savedTickCount     = 0;
        private long   _savedMilitiasServed = 0;

        // ── YARDIMCI ALANLAR (TRAINING) ──────────────────────────────────────
        private int _trainingDayCounter = 0;
        private const int TRAINING_INTERVAL_DAYS = 3;   // kaç günde bir eğitim
        private const int TRAINING_BATCHES = 10;         // her seferinde kaç batch
        private const int MIN_BUFFER_FOR_TRAINING = 32;  // en az kaç deneyim olmalı


        public override void Initialize()
        {
            BuildGroups();
            WireOverflow();


            try
            {
                bool neuralEnabled = Settings.Instance?.EnableNeuralAI ?? false;
                string weightsDir = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord", "Warlord_Logs", "BanditMilitias", "Neural");

                var advisor = NeuralAdvisor.CreateInstance();
                advisor.Initialize(weightsDir, neuralEnabled);


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
            // ── SENSORY ──────────────────────────────────────────────────────────
            // GroupProcessor: küresel tehdit durumunu değerlendirir.
            // Tehdit > 0.7 → tüm warlord milisleri urgent kuyruğuna alınır.
            // Tehdit > 0.85 → kritik eşik, warlord korku indeksi güncellenir.
            // MilitiaBehavior veya LogisticsSystem ile çakışma yok:
            // Bunlar AiHourlyTickEvent / OnHourlyTick üzerinden çalışır,
            // Sensory sadece scheduler kuyruğuna öncelik ekler.
            _sensory = new GanglionGroup("Sensory", budget: 1)
                .AddGroupProcessor(static (p) =>
                {
                    if (p.ThreatLevel <= 0.7f) return;

                    var scheduler = ModuleManager.Instance?.GetModule<AISchedulerSystem>();
                    if (scheduler == null) return;

                    bool isCritical = p.ThreatLevel > 0.85f;

                    foreach (var w in p.AllWarlords)
                    {
                        if (w?.CommandedMilitias == null) continue;

                        // Kritik tehdit: warlord'un korku indeksine göre ek uyarı
                        float fearScore = 0f;
                        if (isCritical)
                            p.WarlordFearIndex.TryGetValue(w.StringId, out fearScore);

                        foreach (var m in w.CommandedMilitias)
                        {
                            if (m?.IsActive != true) continue;

                            // Kritik tehdit + yüksek korku → önce güçlü milisler uyansın
                            if (isCritical && fearScore > 60f)
                            {
                                float str = CompatibilityLayer.GetTotalStrength(m);
                                if (str < 20f) continue; // çok zayıf, bu durumda kaçsın
                            }

                            scheduler.EnqueueDecision(m, urgent: true);
                        }
                    }
                });

            // ── ASSOCIATIVE ──────────────────────────────────────────────────────
            // Milis bazında çalışır. MilitiaBehavior.OnAiHourlyTick ile
            // IsPriorityAIUpdate çakışmasını önlemek için bu flag'e DOKUNMUYORUZ.
            // Bunun yerine: warlord'suz, swarm'sız ve uzun süredir uyuyan militisleri
            // tespit edip uyandırır. Bu görevi başka hiçbir sistem yapmıyor.
            _associative = new GanglionGroup("Associative", budget: 60)
                .AddProcessor(static (militia, percept, inhibitory) =>
                {
                    var comp = militia.PartyComponent as MilitiaPartyComponent;
                    if (comp == null) return;

                    // Warlord'a bağlı milis → kendi kararını verir, dokunma
                    var warlordSystem = WarlordSystem.Instance;
                    if (warlordSystem != null && warlordSystem.GetWarlordForParty(militia) != null)
                        return;

                    // Swarm grubundaysa → SwarmCoordinator yönetir, dokunma
                    var swarm = Intelligence.Swarm.SwarmCoordinator.Instance;
                    if (swarm?.IsInSwarm(militia) == true) return;

                    // Aktif map eventi varsa → dokunma
                    if (militia.MapEvent != null) return;

                    // Uzun süredir uyuyan bağımsız milis → uyandır
                    // "Uzun" = NextThinkTime 4 saatten fazla geçmiş olması
                    float overdueHours = comp.GetSleepOverdueHours();
                    if (overdueHours >= 4f)
                    {
                        comp.WakeUp();
                        // Scheduler'a ekle ama urgent değil — düşük öncelik yeterli
                        var scheduler = ModuleManager.Instance?.GetModule<AISchedulerSystem>();
                        scheduler?.EnqueueDecision(militia, urgent: false);
                    }
                });

            // ── MOTOR ────────────────────────────────────────────────────────────
            // Yük yönetimi. LogisticsSystem zaten yiyecek/restocking halleder.
            // Motor'un görevi: yük yüksekken ÖNCELIKLENDIRME — zayıf/hareketsiz
            // militisleri yavaşlatarak CPU'yu güçlü/aktif militislere bırakmak.
            // Bu görevi hiçbir sistem yapmıyor.
            _motor = new GanglionGroup("Motor", budget: 80)
                .AddProcessor(static (militia, percept, inhibitory) =>
                {
                    // Yük normal → önceliklendirmeye gerek yok
                    if (!percept.IsHighLoad) return;

                    var comp = militia.PartyComponent as MilitiaPartyComponent;
                    if (comp == null) return;

                    // Aktif savaş veya görev varsa kesinlikle dokunma
                    if (militia.MapEvent != null || militia.SiegeEvent != null) return;

                    // Priority güncelleme bekleyen → dokunma
                    if (comp.IsPriorityAIUpdate) return;

                    // Zaten uyuyorsa → dokunma
                    if (comp.NextThinkTime > CampaignTime.Now) return;

                    float strength = CompatibilityLayer.GetTotalStrength(militia);
                    int   troops   = militia.MemberRoster?.TotalManCount ?? 0;

                    // Zayıf milis (< 15 er, < 10 güç) → yük yüksekse 3 saat uyu
                    // Bu militisler büyük etkiye sahip değil, ertelenebilir
                    if (troops < 15 && strength < 10f)
                    {
                        comp.SleepFor(3f);
                        return;
                    }

                    // Warlord'a bağlı değil + orta güç → 2 saat uyu
                    var warlordSystem = WarlordSystem.Instance;
                    bool hasWarlord = warlordSystem?.GetWarlordForParty(militia) != null;
                    if (!hasWarlord && strength < 25f)
                    {
                        comp.SleepFor(2f);
                    }
                });

            // ── AUTONOMIC ────────────────────────────────────────────────────────
            // Küresel arka plan sağlık kontrolü. Düşük frekanslı, yük yüksekse atlanır.
            // İki görev:
            // 1) Yüksek korku altındaki warlord'ların militislerini uyandır
            //    (FearSystem bunu reaktif yapmıyor, polling gerekiyor)
            // 2) Zombie milis tespiti: aktif görünen ama geçersiz component'e sahip
            //    militisleri işaretle (PartyCleanupSystem sonraki tick'te temizler)
            _autonomic = new GanglionGroup("Autonomic", budget: 20)
                .AddGroupProcessor(static (p) =>
                {
                    // Yük yüksekse bu arka plan kontrolünü atla — kritik değil
                    if (p.IsHighLoad) return;
                    if (Campaign.Current == null) return;

                    var fear = FearSystem.Instance;
                    if (fear == null) return;

                    var scheduler = ModuleManager.Instance?.GetModule<AISchedulerSystem>();

                    // Görev 1: Yüksek korku altındaki warlord'ların militislerini uyandır
                    // Korku > 75 olan bir warlord, militislerinin reaktif davranmasını gerektirir
                    foreach (var w in p.AllWarlords)
                    {
                        if (w?.CommandedMilitias == null || !w.IsAlive) continue;

                        if (!p.WarlordFearIndex.TryGetValue(w.StringId, out float fearScore))
                            continue;

                        if (fearScore <= 75f) continue;

                        foreach (var m in w.CommandedMilitias)
                        {
                            if (m?.IsActive != true) continue;
                            if (m.MapEvent != null) continue;

                            var comp = m.PartyComponent as MilitiaPartyComponent;
                            if (comp == null) continue;

                            // Sadece derin uyku modundakileri uyandır (> 2 saat kalan)
                            float remaining = (float)(comp.NextThinkTime - CampaignTime.Now).ToHours;
                            if (remaining > 2f)
                            {
                                comp.WakeUp();
                                scheduler?.EnqueueDecision(m, urgent: false);
                            }
                        }
                    }

                    // Görev 2: Zombie milis tespiti
                    // IsActive = true ama component null veya bozuk olan militisler
                    // Bu durumu tespit edip CleanupSystem'in kuyruğuna gönder
                    var cleanup = ModuleManager.Instance
                        ?.GetModule<PartyCleanupSystem>();
                    if (cleanup == null) return;

                    foreach (var militia in p.ActiveMilitias)
                    {
                        if (militia == null) continue;

                        // Component yoksa veya HomeSettlement kaybolduysa → zombie
                        var comp = militia.PartyComponent as MilitiaPartyComponent;
                        if (comp == null)
                        {
                            // PartyCleanupSystem'in public API'si üzerinden işaretle
                            // (kuyruğa ekle — doğrudan silme, campaign thread güvenliği)
                            cleanup.MarkForDeletion(militia);
                        }
                    }
                });
        }

        private void WireOverflow()
        {


            _associative.SetOverflowTarget(_motor);


            _motor.SetOverflowTarget(_autonomic);
        }


        public override void OnHourlyTick()
        {
            if (!IsEnabled || Campaign.Current == null) return;
            _sw.Restart();


            NeuralEventRouter.Instance.OnHourlyTick();

            // Governor'ı NeuralEventRouter ile senkronize et —
            // flood eşiği high-load'da otomatik düşsün.
            BanditMilitias.Core.Events.EventBus.Instance
                .GetGovernor()
                ?.SetHighLoad(SharedPercept.Current?.IsHighLoad ?? false);


            NeuralAdvisor.Instance?.OnTickReset();


            if (NeuralAdvisor.Instance != null)
            {
                bool hasWarlords = SharedPercept.Current?.AllWarlords?.Count > 0;
                if (!hasWarlords)
                {


                    NeuralAdvisor.Instance.SetMaxInferencesPerTick(0);
                }
                else
                {
                    bool isHigh = DiagnosticsSystem.IsHighLoad;
                    int neuralBudget = isHigh ? 10 : 50;

                    NeuralAdvisor.Instance.SetMaxInferencesPerTick(neuralBudget);
                }
            }


            int channelCount = DetermineChannelCount();


            SharedPercept.Refresh();
            var percept = SharedPercept.Current;
            if (percept == null) return;


            _inhibitory.Reset();


            _sensory.TickGroupOnly(percept);
            _autonomic.TickGroupOnly(percept);


            for (int ch = 0; ch < channelCount; ch++)
            {
                _associative.TickMilitias(percept, _inhibitory, ch, channelCount);
                _motor.TickMilitias(percept, _inhibitory, ch, channelCount);
            }


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


            var percept = SharedPercept.Current;
            int baseBudget = percept.ActiveMilitias.Count;

            _associative.TickBudget = Math.Max(20, baseBudget / 2);
            _motor.TickBudget       = Math.Max(30, baseBudget);
            _autonomic.TickBudget   = Math.Max(10, baseBudget / 4);

            _savedTickCount      = _tickCount;
            _savedMilitiasServed = _totalMilitiasServed;

            // ── YENİ: Günlük otomatik eğitim döngüsü ──────────────────────────────
            TryRunDailyTraining();
        }

        /// <summary>
        /// Her TRAINING_INTERVAL_DAYS günde bir NeuralAdvisor'ı ExperienceBuffer'daki
        /// birikmiş verilerle eğitir. Oyuncu makinesine yük bindirmemek için
        /// batch sayısı kasıtlı olarak düşük tutulmuştur (10 batch = ~320 sample).
        /// Headless test modunda bu değer settings üzerinden artırılabilir.
        /// </summary>
        private void TryRunDailyTraining()
        {
            try
            {
                var advisor = NeuralAdvisor.Instance;
                if (advisor == null || !advisor.IsEnabled || !advisor.IsOperational) return;

                _trainingDayCounter++;

                // Kaç günde bir eğiteceğimizi settings'ten al, yoksa sabit kullan
                int intervalDays = Settings.Instance?.NeuralTrainingIntervalDays ?? TRAINING_INTERVAL_DAYS;

                if (_trainingDayCounter < intervalDays) return;
                _trainingDayCounter = 0;

                // Yeterli veri var mı?
                var buffer = advisor.GetExperienceBuffer();
                if (buffer == null || buffer.Count < MIN_BUFFER_FOR_TRAINING) return;

                // Headless/test modunda daha fazla batch kullan
                bool isHeadless = Settings.Instance?.TestingMode == true;
                int batches = isHeadless
                    ? (Settings.Instance?.NeuralHeadlessTrainingBatches ?? 50)
                    : TRAINING_BATCHES;

                float learningRate = Settings.Instance?.NeuralLearningRate ?? 0.01f;

                string result = advisor.TrainOffline(batches, batchSize: 32, learningRate: learningRate);

                // Ağırlıkları kaydet (NeuralDataExporter üzerinden)
                if (advisor.TotalTrainingBatches % 100 == 0)
                {
                    advisor.TrySaveWeights();
                }

                if (Settings.Instance?.TestingMode == true || isHeadless)
                {
                    DebugLogger.Info("NervousSystem.Training",
                        $"Auto-train complete | batches={batches} | buffer={buffer.Count} | " +
                        $"totalBatches={advisor.TotalTrainingBatches} | " +
                        $"confidence={advisor.GlobalConfidence:F3} | {result}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("NervousSystem", $"TryRunDailyTraining failed: {ex.Message}");
            }
        }


        private static int DetermineChannelCount()
        {
            bool isHigh    = DiagnosticsSystem.IsHighLoad;
            int  partyCount = Campaign.Current?.MobileParties.Count ?? 0;

            if (isHigh && partyCount > 2000) return 4;
            if (isHigh || partyCount > 1500) return 3;
            return 2;
        }


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


            var advisor = NeuralAdvisor.Instance;
            if (advisor != null)
            {
                sb.AppendLine($"Neural AI: {(advisor.IsEnabled ? "ACTIVE" : "PASSIVE")}");
                sb.AppendLine($"  Confidence: {advisor.GlobalConfidence:F2}");
                sb.AppendLine($"  Inferences: {advisor.TotalInferences}");
                sb.AppendLine($"  Training: {advisor.TotalTrainingBatches} batches");
            }


            sb.AppendLine(NeuralEventRouter.Instance.GetDiagnostics());

            return sb.ToString();
        }


        public static SharedPercept Percept => SharedPercept.Current;


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
