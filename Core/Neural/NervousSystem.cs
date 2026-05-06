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


            _sensory = new GanglionGroup("Sensory", budget: 1)

                .AddGroupProcessor(static (p) =>
                {


                    if (p.ThreatLevel > 0.7f)
                    {


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


            _associative = new GanglionGroup("Associative", budget: 60)
                .AddProcessor(static (militia, percept, inhibitory) =>
                {


                    var comp = militia.PartyComponent as MilitiaPartyComponent;
                    if (comp?.IsPriorityAIUpdate == true)
                    {
                        var scheduler = ModuleManager.Instance?.GetModule<AISchedulerSystem>();
                        scheduler?.EnqueueDecision(militia, urgent: true);
                        comp.IsPriorityAIUpdate = false;
                    }
                });


            _motor = new GanglionGroup("Motor", budget: 80)
                .AddProcessor(static (militia, percept, inhibitory) =>
                {


                    if (!percept.IsHighLoad) return;

                    var comp = militia.PartyComponent as MilitiaPartyComponent;
                    if (comp == null) return;


                    if (militia.FoodChange < -5f && militia.ItemRoster != null)
                    {
                        var logistics = ModuleManager.Instance
                            ?.GetModule<WarlordLogisticsSystem>();


                        comp.CurrentState = MilitiaPartyComponent.WarlordState.Restocking;
                    }
                });


            _autonomic = new GanglionGroup("Autonomic", budget: 20)
                .AddGroupProcessor(static (p) =>
                {


                    if (p.IsHighLoad) return;


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


