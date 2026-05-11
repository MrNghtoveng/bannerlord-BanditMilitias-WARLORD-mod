using BanditMilitias;
using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Neural;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Intelligence.Swarm;
using BanditMilitias.Systems.Cleanup;
using BanditMilitias.Systems.Diagnostics;
using BanditMilitias.Systems.Fear;
using BanditMilitias.Systems.Logistics;
using BanditMilitias.Systems.Scheduling;
using BanditMilitias.Systems.Tracking;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Core.Neural
{
    /// <summary>
    /// SharedPercept – thread‑safe snapshot of the strategic state.
    /// Double‑buffered, refreshed via Refresh() on the main thread.
    /// </summary>
    public sealed class SharedPercept
    {
        public IReadOnlyList<Warlord> AllWarlords { get; private set; } = Array.Empty<Warlord>();
        public IReadOnlyList<MobileParty> ActiveMilitias { get; private set; } = Array.Empty<MobileParty>();
        public float ThreatLevel { get; private set; }
        public bool IsHighLoad { get;   private set; }
        public CampaignTime SnapshotTime { get; private set; }
        public int TotalPartyCount { get; private set; }

        public IReadOnlyDictionary<string, float> WarlordFearIndex { get; private set; }
            = new Dictionary<string, float>();

        private readonly Dictionary<string, float> _myelinFloat = new();
        private readonly Dictionary<string, object> _myelinObject = new();

        public bool IsStale =>
            Campaign.Current == null ||
            (CampaignTime.Now - SnapshotTime).ToHours > 1.5;

        // Double‑buffering: two buffers, atomic swap with Interlocked.Exchange
        private static SharedPercept _current = new SharedPercept();
        private static SharedPercept _back = new SharedPercept();

        /// <summary>Returns the latest snapshot (lock‑free, full memory barrier via Volatile.Read).</summary>
        public static SharedPercept Current => Volatile.Read(ref _current);

        private void PrepareForReuse()
        {
            AllWarlords = Array.Empty<Warlord>();
            ActiveMilitias = Array.Empty<MobileParty>();
            ThreatLevel = 0f;
            IsHighLoad = false;
            TotalPartyCount = 0;
            _myelinFloat.Clear();
            _myelinObject.Clear();

            // WarlordFearIndex will be reassigned later, no need to clear
        }

        /// <summary>
        /// Refreshes the snapshot from the current game state.
        /// Must be called from the main thread (e.g., NervousSystem.OnHourlyTick).
        /// Uses double‑buffering to avoid blocking readers.
        /// </summary>
        internal static void Refresh()
        {
            if (Campaign.Current == null) return;

            // Take the back buffer and fill it
            var next = _back;
            next.PrepareForReuse();

            next.SnapshotTime = CampaignTime.Now;
            next.IsHighLoad = DiagnosticsSystem.IsHighLoad;
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
                // Reuse dictionary if possible
                if (next.WarlordFearIndex is Dictionary<string, float> existing)
                {
                    existing.Clear();
                    foreach (var w in next.AllWarlords)
                        existing[w.StringId] = fear.GetAverageFearForWarlord(w.StringId);
                    // No need to reassign next.WarlordFearIndex, it's the same instance
                }
                else
                {
                    var newDict = new Dictionary<string, float>(next.AllWarlords.Count);
                    foreach (var w in next.AllWarlords)
                        newDict[w.StringId] = fear.GetAverageFearForWarlord(w.StringId);
                    next.WarlordFearIndex = newDict;
                }
            }

            // Atomic swap: publish the new buffer
            var old = Interlocked.Exchange(ref _current, next);
            _back = old; // the previously current buffer becomes the new back
        }

        public bool TryGetCached(string key, out float value) => _myelinFloat.TryGetValue(key, out value);
        public void Cache(string key, float value) => _myelinFloat[key] = value;
        public bool TryGetCachedObject<T>(string key, out T? value) where T : class
        {
            if (_myelinObject.TryGetValue(key, out var raw) && raw is T typed)
            {
                value = typed;
                return true;
            }
            value = default;
            return false;
        }
        public void CacheObject(string key, object value) => _myelinObject[key] = value;
    }

    /// <summary>
    /// Divides militia parties into channels for parallel (or staggered) processing.
    /// </summary>
    public static class DendriticPartitioner
    {
        public static int GetChannel(MobileParty militia, int channelCount)
        {
            if (channelCount <= 1) return 0;
            int hash = Math.Abs(militia.StringId?.GetHashCode() ?? militia.GetHashCode());
            return hash % channelCount;
        }

        public static IEnumerable<MobileParty> GetSlice(IReadOnlyList<MobileParty> militias, int channel, int channelCount)
        {
            foreach (var m in militias)
            {
                if (m == null || !m.IsActive) continue;
                if (GetChannel(m, channelCount) == channel)
                    yield return m;
            }
        }
    }

    /// <summary>
    /// Prevents the same militia from being processed twice in a single tick.
    /// </summary>
    public sealed class InhibitorySignal
    {
        private readonly HashSet<string> _claimed = new();

        public bool TryClaim(MobileParty militia)
        {
            if (militia?.StringId == null) return false;
            return _claimed.Add(militia.StringId);
        }

        public void Reset() => _claimed.Clear();
        public int ClaimedCount => _claimed.Count;
    }

    /// <summary>
    /// A group of processors (neurons) that run on a subset of militias each tick.
    /// </summary>
    public sealed class GanglionGroup
    {
        public string Name { get; }
        public int TickBudget { get; set; }

        public int Processed { get; private set; }
        public float LoadRatio => TickBudget > 0 ? (float)Processed / TickBudget : 0f;
        public bool IsOverloaded => LoadRatio > 0.85f;

        private GanglionGroup? _overflowTarget;
        private readonly List<Action<MobileParty, SharedPercept, InhibitorySignal>> _processors = new();
        private readonly List<Action<SharedPercept>> _groupProcessors = new();

        public GanglionGroup(string name, int budget)
        {
            Name = name;
            TickBudget = budget;
        }

        public GanglionGroup SetOverflowTarget(GanglionGroup target) { _overflowTarget = target; return this; }
        public GanglionGroup AddProcessor(Action<MobileParty, SharedPercept, InhibitorySignal> fn) { _processors.Add(fn); return this; }
        public GanglionGroup AddGroupProcessor(Action<SharedPercept> fn) { _groupProcessors.Add(fn); return this; }

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
            var slice = DendriticPartitioner.GetSlice(percept.ActiveMilitias, channel, channelCount);
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
                    catch (Exception ex) { DebugLogger.Warning(Name, $"Processor[{militia.Name}]: {ex.Message}"); }
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
                catch (Exception ex) { DebugLogger.Warning($"{Name}[overflow]", $"{militia.Name}: {ex.Message}"); }
            }
            Processed++;
        }

        public void Reset() => Processed = 0;
        public string GetDiagnostics() =>
            $"[{Name,-14}] budget={TickBudget,3} done={Processed,3} load={LoadRatio:P0} overloaded={IsOverloaded}";
    }

    /// <summary>
    /// The central nervous system – coordinates all AI processing with load‑aware scheduling.
    /// </summary>
    [BanditMilitias.Core.Components.ModuleDependency(
        typeof(BanditMilitias.Intelligence.Strategic.WarlordSystem),
        typeof(BanditMilitias.Systems.Scheduling.AISchedulerSystem),
        typeof(BanditMilitias.Systems.Fear.FearSystem),
        typeof(BanditMilitias.Systems.Cleanup.PartyCleanupSystem),
        typeof(BanditMilitias.Systems.Tracking.PlayerTracker),
        typeof(BanditMilitias.Intelligence.Swarm.SwarmCoordinator))]
    [BanditMilitias.Core.Components.AutoRegister(Priority = 98, IsCritical = false)]
    public class NervousSystem : MilitiaModuleBase
    {
        private static readonly Lazy<NervousSystem> _inst = new(() => new NervousSystem());
        public static NervousSystem Instance => _inst.Value;
        private NervousSystem() { }

        public override string ModuleName => "NervousSystem";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override bool IsCritical => false;
        public override int Priority => 98;

        private GanglionGroup _sensory = null!;
        private GanglionGroup _associative = null!;
        private GanglionGroup _motor = null!;
        private GanglionGroup _autonomic = null!;

        private readonly InhibitorySignal _inhibitory = new();

        private long _tickCount = 0;
        private long _totalMilitiasServed = 0;
        private long _totalOverflows = 0;
        private readonly Stopwatch _sw = new();

        private long _savedTickCount = 0;
        private long _savedMilitiasServed = 0;

        private int _trainingDayCounter = 0;
        private const int TRAINING_INTERVAL_DAYS = 3;
        private const int TRAINING_BATCHES = 10;
        private const int MIN_BUFFER_FOR_TRAINING = 32;

        public override void Initialize()
        {
            BuildGroups();
            WireOverflow();

            try
            {
                bool neuralEnabled = Settings.Instance?.EnableNeuralAI ?? false;
                string weightsDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord", "Warlord_Logs", "BanditMilitias", "Neural");

                var advisor = NeuralAdvisor.CreateInstance();
                advisor.Initialize(weightsDir, neuralEnabled);

                NeuralDataExporter.SetExportDirectory(
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "Mount and Blade II Bannerlord", "Warlord_Logs", "BanditMilitias", "AI", "exports"));

                DebugLogger.Info(ModuleName, $"Neural AI: {(neuralEnabled ? "ACTIVE" : "PASSIVE (data collection only)")}");
            }
            catch (Exception ex)
            {
                DebugLogger.Warning(ModuleName, $"Neural AI init failed: {ex.Message}");
            }

            DebugLogger.Info(ModuleName, "Nervous system ready: 4 ganglion groups, inhibitory signal active.");
        }

        private void BuildGroups()
        {
            // SENSORY – global threat detection
            _sensory = new GanglionGroup("Sensory", budget: 1)
                .AddGroupProcessor(static p =>
                {
                    if (p.ThreatLevel <= 0.7f) return;

                    var scheduler = ModuleManager.Instance?.GetModule<AISchedulerSystem>();
                    if (scheduler == null) return;

                    bool isCritical = p.ThreatLevel > 0.85f;

                    foreach (var w in p.AllWarlords)
                    {
                        if (w?.CommandedMilitias == null) continue;

                        float fearScore = 0f;
                        if (isCritical)
                            p.WarlordFearIndex.TryGetValue(w.StringId, out fearScore);

                        foreach (var m in w.CommandedMilitias)
                        {
                            if (m?.IsActive != true) continue;

                            if (isCritical && fearScore > 60f)
                            {
                                if (CompatibilityLayer.GetTotalStrength(m) < 20f) continue;
                            }
                            scheduler.EnqueueDecision(m, urgent: true);
                        }
                    }
                });

            // ASSOCIATIVE – wake up long‑sleeping independent militias
            _associative = new GanglionGroup("Associative", budget: 60)
                .AddProcessor(static (militia, percept, inhibitory) =>
                {
                    var comp = militia.PartyComponent as MilitiaPartyComponent;
                    if (comp == null) return;

                    if (WarlordSystem.Instance?.GetWarlordForParty(militia) != null) return;
                    if (SwarmCoordinator.Instance?.IsInSwarm(militia) == true) return;
                    if (militia.MapEvent != null) return;

                    if (comp.GetSleepOverdueHours() >= 4f)
                    {
                        comp.WakeUp();
                        var scheduler = ModuleManager.Instance?.GetModule<AISchedulerSystem>();
                        scheduler?.EnqueueDecision(militia, urgent: false);
                    }
                });

            // MOTOR – load shedding under high CPU pressure
            _motor = new GanglionGroup("Motor", budget: 80)
                .AddProcessor(static (militia, percept, inhibitory) =>
                {
                    if (!percept.IsHighLoad) return;
                    var comp = militia.PartyComponent as MilitiaPartyComponent;
                    if (comp == null) return;
                    if (militia.MapEvent != null || militia.SiegeEvent != null) return;
                    if (comp.IsPriorityAIUpdate) return;
                    if (comp.NextThinkTime > CampaignTime.Now) return;

                    float strength = CompatibilityLayer.GetTotalStrength(militia);
                    int troops = militia.MemberRoster?.TotalManCount ?? 0;

                    if (troops < 15 && strength < 10f)
                    {
                        comp.SleepFor(3f);
                        return;
                    }

                    bool hasWarlord = WarlordSystem.Instance?.GetWarlordForParty(militia) != null;
                    if (!hasWarlord && strength < 25f)
                        comp.SleepFor(2f);
                });

            // AUTONOMIC – background health checks
            _autonomic = new GanglionGroup("Autonomic", budget: 20)
                .AddGroupProcessor(static p =>
                {
                    if (p.IsHighLoad) return;
                    if (Campaign.Current == null) return;

                    var fear = FearSystem.Instance;
                    if (fear == null) return;

                    var scheduler = ModuleManager.Instance?.GetModule<AISchedulerSystem>();
                    var cleanup = ModuleManager.Instance?.GetModule<PartyCleanupSystem>();

                    foreach (var w in p.AllWarlords)
                    {
                        if (w?.CommandedMilitias == null || !w.IsAlive) continue;
                        if (!p.WarlordFearIndex.TryGetValue(w.StringId, out float fearScore)) continue;
                        if (fearScore <= 75f) continue;

                        foreach (var m in w.CommandedMilitias)
                        {
                            if (m?.IsActive != true || m.MapEvent != null) continue;
                            var comp = m.PartyComponent as MilitiaPartyComponent;
                            if (comp == null) continue;

                            float remaining = (float)(comp.NextThinkTime - CampaignTime.Now).ToHours;
                            if (remaining > 2f)
                            {
                                comp.WakeUp();
                                scheduler?.EnqueueDecision(m, urgent: false);
                            }
                        }
                    }

                    if (cleanup != null)
                    {
                        foreach (var militia in p.ActiveMilitias)
                        {
                            if (militia == null) continue;
                            if (militia.PartyComponent as MilitiaPartyComponent == null)
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
            EventBus.Instance.GetGovernor()?.SetHighLoad(SharedPercept.Current?.IsHighLoad ?? false);

            NeuralAdvisor.Instance?.OnTickReset();

            if (NeuralAdvisor.Instance != null)
            {
                bool hasWarlords = SharedPercept.Current?.AllWarlords?.Count > 0;
                if (!hasWarlords)
                    NeuralAdvisor.Instance.SetMaxInferencesPerTick(0);
                else
                {
                    bool isHigh = DiagnosticsSystem.IsHighLoad;
                    NeuralAdvisor.Instance.SetMaxInferencesPerTick(isHigh ? 10 : 50);
                }
            }

            int channelCount = DetermineChannelCount();

            // CRITICAL: Refresh the shared percept once per tick
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
                           + (_motor.IsOverloaded ? 1L : 0L);
            _totalOverflows += overflows;

            _sw.Stop();

            if (Settings.Instance?.TestingMode == true && _sw.ElapsedMilliseconds > 3)
            {
                DebugLogger.Info(ModuleName,
                    $"Tick #{_tickCount} | ch={channelCount} | served={served} | {_sw.ElapsedMilliseconds}ms");
            }
        }

        public override void OnDailyTick()
        {
            var percept = SharedPercept.Current;
            int baseBudget = percept.ActiveMilitias.Count;

            _associative.TickBudget = Math.Max(20, baseBudget / 2);
            _motor.TickBudget = Math.Max(30, baseBudget);
            _autonomic.TickBudget = Math.Max(10, baseBudget / 4);

            _savedTickCount = _tickCount;
            _savedMilitiasServed = _totalMilitiasServed;

            TryRunDailyTraining();
        }

        private void TryRunDailyTraining()
        {
            try
            {
                var advisor = NeuralAdvisor.Instance;
                if (advisor == null || !advisor.IsEnabled || !advisor.IsOperational) return;

                _trainingDayCounter++;
                int intervalDays = Settings.Instance?.NeuralTrainingIntervalDays ?? TRAINING_INTERVAL_DAYS;
                if (_trainingDayCounter < intervalDays) return;
                _trainingDayCounter = 0;

                var buffer = advisor.GetExperienceBuffer();
                if (buffer == null || buffer.Count < MIN_BUFFER_FOR_TRAINING) return;

                bool isHeadless = Settings.Instance?.TestingMode == true;
                int batches = isHeadless
                    ? (Settings.Instance?.NeuralHeadlessTrainingBatches ?? 50)
                    : TRAINING_BATCHES;

                float learningRate = Settings.Instance?.NeuralLearningRate ?? 0.01f;
                string result = advisor.TrainOffline(batches, batchSize: 32, learningRate: learningRate);

                if (advisor.TotalTrainingBatches % 100 == 0)
                    advisor.TrySaveWeights();

                if (Settings.Instance?.TestingMode == true || isHeadless)
                {
                    DebugLogger.Info("NervousSystem.Training",
                        $"Auto-train complete | batches={batches} | buffer={buffer.Count} | " +
                        $"totalBatches={advisor.TotalTrainingBatches} | confidence={advisor.GlobalConfidence:F3} | {result}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("NervousSystem", $"TryRunDailyTraining failed: {ex.Message}");
            }
        }

        private static int DetermineChannelCount()
        {
            bool isHigh = DiagnosticsSystem.IsHighLoad;
            int partyCount = Campaign.Current?.MobileParties.Count ?? 0;

            if (isHigh && partyCount > 2000) return 4;
            if (isHigh || partyCount > 1500) return 3;
            return 2;
        }

        public override void SyncData(IDataStore dataStore)
        {
            try
            {
                dataStore.SyncData("BM_NS_TickCount", ref _savedTickCount);
                dataStore.SyncData("BM_NS_MilsServed", ref _savedMilitiasServed);
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
            sb.AppendLine($"SharedPercept: {SharedPercept.Current?.ActiveMilitias?.Count ?? 0} militias, " +
                          $"threat={SharedPercept.Current?.ThreatLevel ?? 0:P0}, " +
                          $"warlords={SharedPercept.Current?.AllWarlords?.Count ?? 0}");
            sb.AppendLine($"Total processed: {_totalMilitiasServed} militias, {_totalOverflows} overflows");
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
                sb.AppendLine($"  Training batches: {advisor.TotalTrainingBatches}");
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
