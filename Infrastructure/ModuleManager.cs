using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Debug;
using BanditMilitias.Lifecycle;
using BanditMilitias.Systems.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using ModuleDependencyAttribute = BanditMilitias.Core.Components.ModuleDependencyAttribute;

namespace BanditMilitias.Infrastructure
{

    public class ModuleManager
    {

        private static readonly Lazy<ModuleManager> _instance =
            new Lazy<ModuleManager>(() => new ModuleManager(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static ModuleManager Instance => _instance.Value;

        private static readonly StringComparer ModuleNameComparer = StringComparer.OrdinalIgnoreCase;
        private readonly List<IMilitiaModule> _modules = new List<IMilitiaModule>();
        private readonly Dictionary<Type, IMilitiaModule> _modulesByType = new Dictionary<Type, IMilitiaModule>();
        private readonly Dictionary<string, Type> _moduleTypesByName = new Dictionary<string, Type>(ModuleNameComparer);
        private readonly HashSet<string> _failedModules = new HashSet<string>(ModuleNameComparer);
        private readonly Dictionary<string, DateTime> _lastErrorTime = new Dictionary<string, DateTime>(ModuleNameComparer);
        private readonly Dictionary<string, ModuleFailureRecord> _failureRecords = new Dictionary<string, ModuleFailureRecord>(ModuleNameComparer);
        private readonly Dictionary<string, int> _moduleTickFailureStreaks = new Dictionary<string, int>(ModuleNameComparer);
        private IMilitiaModule[] _sortedModuleSnapshot = Array.Empty<IMilitiaModule>();
        private bool _modulesDirty = true;
        private bool _isInitialized = false;
        private bool _campaignEventsRegistered = false;
        private bool _sessionBootstrapComplete = false;
        private readonly HashSet<Type> _sessionStartedModules = new HashSet<Type>();
        private readonly object _initLock = new object();

        private readonly HashSet<MobileParty> _registeredMilitias = new HashSet<MobileParty>();

        private volatile MobileParty[] _militiaSnapshot = Array.Empty<MobileParty>();

        private readonly object _registryLock = new object();

        private volatile bool _snapshotDirty = false;

        private readonly List<Settlement> _hideoutCache = new List<Settlement>();
        private readonly List<Settlement> _villageCache = new List<Settlement>();
        private readonly List<Settlement> _townCache = new List<Settlement>();

        public int CachedTotalParties { get; set; }

        private readonly List<Settlement> _castleCache = new List<Settlement>();
        private readonly Dictionary<long, List<Settlement>> _spatialIndex = new Dictionary<long, List<Settlement>>();
        private const float SPATIAL_GRID_SIZE = 50f;

        private readonly ModuleTickScheduler _tickScheduler = new ModuleTickScheduler();

        private DateTime _lastCacheRebuild = DateTime.MinValue;
        private const double CACHE_REBUILD_INTERVAL_HOURS = 1.0;

        private long _registryOperations = 0;
        private long _cacheHits = 0;
        private long _cacheMisses = 0;
        private int _snapshotRebuilds = 0;

        private readonly Dictionary<string, float> _moduleLastTickMs = new Dictionary<string, float>(ModuleNameComparer);
        private readonly Dictionary<string, float> _moduleAvgTickMs = new Dictionary<string, float>(ModuleNameComparer);


        private readonly Dictionary<IMilitiaModule, string> _moduleNameCache = new();
        private readonly Dictionary<(IMilitiaModule, string), string> _scopeNameCache = new();

        private readonly object _perfLock = new object();
        private const float PerfEmaAlpha = 0.2f;
        private const int MaxConsecutiveTickFailures = 3;


        private readonly System.Diagnostics.Stopwatch _reusableStopwatch = new System.Diagnostics.Stopwatch();

        private ModuleManager()
        {

        }

        private static string ResolveModuleName(IMilitiaModule module)
        {
            if (module == null)
            {
                return string.Empty;
            }

            try
            {
                string rawName = module.ModuleName ?? string.Empty;
                return string.IsNullOrWhiteSpace(rawName) ? module.GetType().Name : rawName.Trim();
            }
            catch
            {
                return module.GetType().Name;
            }
        }

        private sealed class ModuleFailureRecord
        {
            public string ModuleName { get; }
            public int FailureCount { get; private set; }
            public string LastStage { get; private set; } = "Unknown";
            public string LastErrorMessage { get; private set; } = "n/a";
            public string LastExceptionType { get; private set; } = "n/a";
            public string LastStackTrace { get; private set; } = "n/a";
            public DateTime FirstFailureAt { get; private set; } = DateTime.MinValue;
            public DateTime LastFailureAt { get; private set; } = DateTime.MinValue;

            public ModuleFailureRecord(string moduleName)
            {
                ModuleName = moduleName ?? string.Empty;
            }

            public void Register(string stage, Exception ex)
            {
                DateTime now = DateTime.Now;
                if (FailureCount == 0)
                {
                    FirstFailureAt = now;
                }

                FailureCount++;
                LastFailureAt = now;
                LastStage = string.IsNullOrWhiteSpace(stage) ? "Unknown" : stage.Trim();
                LastExceptionType = ex?.GetType().Name ?? "Exception";
                LastErrorMessage = CompactError(ex?.Message);
                LastStackTrace = ex?.ToString() ?? "n/a";
            }
        }

        public void OnSessionEnd()
        {
            lock (_initLock)
            {
                DebugLogger.Info("ModuleManager", "Session end detected. Cleaning up all modules.");

                var modulesToCleanup = GetSortedModuleSnapshot();

                foreach (var module in modulesToCleanup)
                {
                    try
                    {
                        module.Cleanup();
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Error("ModuleManager", $"Error cleaning up module {module.ModuleName}: {ex.Message}");
                    }
                }

                lock (_registryLock)
                {
                    _registeredMilitias.Clear();
                    _militiaSnapshot = Array.Empty<MobileParty>();
                }

                _hideoutCache.Clear();
                _villageCache.Clear();
                _townCache.Clear();
                _castleCache.Clear();
                _spatialIndex.Clear();
                _tickScheduler.ResetForSessionEnd();

                _isInitialized = false;
                _sessionBootstrapComplete = false;
                _campaignEventsRegistered = false;
                _sessionStartedModules.Clear();
                _moduleTickFailureStreaks.Clear();

                lock (_perfLock)
                {
                    _moduleLastTickMs.Clear();
                    _moduleAvgTickMs.Clear();
                }

                DebugLogger.Info("ModuleManager", "Session cleanup complete.");
            }
        }

        public void ResetForSessionEnd()
        {
            OnSessionEnd();
        }

        public void ResetForModuleUnload()
        {
            CleanupAll();
            _tickScheduler.ResetForSessionEnd();
        }

        private static string CompactError(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "n/a";
            }

            string compact = (message ?? string.Empty).Replace(Environment.NewLine, " ").Trim();
            return compact.Length <= 220 ? compact : compact.Substring(0, 220) + "...";
        }

        private static string CompactStack(string? stack)
        {
            if (string.IsNullOrWhiteSpace(stack))
            {
                return "n/a";
            }

            string compact = (stack ?? string.Empty).Replace(Environment.NewLine, " ").Trim();
            return compact.Length <= 1200 ? compact : compact.Substring(0, 1200) + "...";
        }

        private void RecordModuleFailure(string moduleName, string stage, Exception ex, bool notifyUser)
        {
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                moduleName = "UnknownModule";
            }

            bool shouldNotify;

            lock (_initLock)
            {
                _ = _failedModules.Add(moduleName);

                if (!_failureRecords.TryGetValue(moduleName, out ModuleFailureRecord? record))
                {
                    record = new ModuleFailureRecord(moduleName);
                    _failureRecords[moduleName] = record;
                }

                record.Register(stage, ex);

                shouldNotify = !_lastErrorTime.ContainsKey(moduleName) ||
                               (DateTime.Now - _lastErrorTime[moduleName]).TotalHours > 24;

                if (shouldNotify)
                {
                    _lastErrorTime[moduleName] = DateTime.Now;
                }
            }

            DebugLogger.Error("ModuleManager", $"{moduleName} {stage} failed: {ex.Message}");
            try
            {
                FileLogger.LogError($"{moduleName} {stage} failed: {ex}");
            }
            catch
            {
            }

            if (notifyUser && shouldNotify)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[BanditMilitias] {moduleName} disabled due to error. Command: militia.failed_modules",
                    Colors.Red));
            }
        }

        private int _dayCount = 0;

        private int ResetFailedModulesForRetry()
        {
            lock (_initLock)
            {
                _dayCount++;

                if (_failedModules.Count == 0)
                    return 0;

                var toRetry = new List<string>();

                foreach (var moduleName in _failedModules)
                {
                    if (!_failureRecords.TryGetValue(moduleName, out var record))
                    {

                        toRetry.Add(moduleName);
                        continue;
                    }

                    int fc = record.FailureCount;
                    bool shouldRetry =
                        fc <= 2
                        || (fc <= 5 && _dayCount % 7 == 0)
                        || (_dayCount % 30 == 0);

                    if (shouldRetry)
                        toRetry.Add(moduleName);
                }

                foreach (var name in toRetry)
                {
                    _ = _failedModules.Remove(name);
                    _ = _moduleTickFailureStreaks.Remove(name);
                }

                if (toRetry.Count > 0)
                {
                    DebugLogger.Info("ModuleManager",
                        $"Retry window: re-enabling {toRetry.Count} module(s): {string.Join(", ", toRetry)}");
                }

                return toRetry.Count;
            }
        }

        private bool IsModuleFailed(string moduleName)
        {
            lock (_initLock)
            {
                return _failedModules.Contains(moduleName);
            }
        }

        private void ClearTickFailureStreak(string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return;
            }

            lock (_initLock)
            {
                _ = _moduleTickFailureStreaks.Remove(moduleName);
            }
        }

        private int IncrementTickFailureStreak(string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                moduleName = "UnknownModule";
            }

            lock (_initLock)
            {
                int streak = 1;
                if (_moduleTickFailureStreaks.TryGetValue(moduleName, out int current))
                {
                    streak = current + 1;
                }

                _moduleTickFailureStreaks[moduleName] = streak;
                return streak;
            }
        }

        public bool HasFailedModules
        {
            get
            {
                lock (_initLock)
                {
                    return _failedModules.Count > 0;
                }
            }
        }

        public string GetFailedModuleSummary(int maxNames = 5)
        {
            lock (_initLock)
            {
                if (_failedModules.Count == 0)
                {
                    return "none";
                }

                string[] names = _failedModules
                    .OrderBy(n => n)
                    .Take(Math.Max(1, maxNames))
                    .ToArray();

                int remaining = _failedModules.Count - names.Length;
                return remaining > 0
                    ? $"{string.Join(", ", names)} (+{remaining} more)"
                    : string.Join(", ", names);
            }
        }

        public string GetFailedModulesReport(int maxEntries = 25)
        {
            lock (_initLock)
            {
                if (_failureRecords.Count == 0)
                {
                    return "No module failures recorded in this session.";
                }

                var sb = new System.Text.StringBuilder();
                _ = sb.AppendLine("=== FAILED MODULES (BanditMilitias) ===");

                var records = _failureRecords.Values
                    .OrderByDescending(r => r.LastFailureAt)
                    .ThenBy(r => r.ModuleName, ModuleNameComparer)
                    .Take(Math.Max(1, maxEntries));

                foreach (var record in records)
                {
                    bool disabled = _failedModules.Contains(record.ModuleName);
                    _ = sb.AppendLine(
                        $"- {record.ModuleName} | State={(disabled ? "Disabled" : "RetryPending")} | " +
                        $"Count={record.FailureCount} | LastStage={record.LastStage} | LastAt={record.LastFailureAt:yyyy-MM-dd HH:mm:ss}");
                    _ = sb.AppendLine($"  {record.LastExceptionType}: {record.LastErrorMessage}");
                    _ = sb.AppendLine($"  Stack: {CompactStack(record.LastStackTrace)}");
                }

                _ = sb.AppendLine("=======================================");
                return sb.ToString();
            }
        }

        public void RegisterModule(IMilitiaModule module)
        {
            if (module == null)
            {
                DebugLogger.Warning("ModuleManager", "Attempted to register null module");
                return;
            }

            lock (_initLock)
            {
                string moduleName = ResolveModuleName(module);
                Type moduleType = module.GetType();

                if (_modules.Contains(module))
                {
                    DebugLogger.Warning("ModuleManager", $"Module {moduleName} already registered");
                    return;
                }

                if (_modulesByType.ContainsKey(moduleType))
                {
                    DebugLogger.Warning("ModuleManager", $"Module type already registered: {moduleType.Name}");
                    return;
                }

                if (_moduleTypesByName.TryGetValue(moduleName, out Type? existingType))
                {
                    DebugLogger.Warning("ModuleManager", $"Module name already registered: {moduleName} ({existingType.Name})");
                    return;
                }

                _modules.Add(module);
                _modulesByType[moduleType] = module;
                _moduleTypesByName[moduleName] = moduleType;
                _modulesDirty = true;


                BanditMilitias.Core.Registry.ModuleRegistry.Instance.Confirm(module);

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Info("ModuleManager", $"Registered module: {moduleName} (Priority: {module.Priority})");
                }
            }
        }

        public bool IsModuleRegistered(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName)) return false;
            lock (_initLock)
            {
                return _moduleTypesByName.ContainsKey(moduleName.Trim());
            }
        }

        public void InitializeAll()
        {
            lock (_initLock)
            {
                if (_isInitialized)
                {
                    DebugLogger.Warning("ModuleManager", "Already initialized");
                    return;
                }

                var timer = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    _campaignEventsRegistered = false;
                    _sessionBootstrapComplete = false;
                    _sessionStartedModules.Clear();

                    var enabledModules = GetSortedModuleSnapshot();

                    foreach (var module in enabledModules)
                    {
                        try
                        {
                            string moduleName = ResolveModuleName(module);
                            var moduleTimer = System.Diagnostics.Stopwatch.StartNew();
                            FileLogger.Log($"Initialize module: {moduleName} (Priority {module.Priority})");
                            module.Initialize();
                            BanditMilitias.Core.Registry.ModuleRegistry.Instance.MarkHealthy(module, "Initialize");
                            moduleTimer.Stop();
                            FileLogger.Log($"Initialized module: {moduleName} in {moduleTimer.Elapsed.TotalMilliseconds:F2}ms");

                            if (Settings.Instance?.TestingMode == true)
                            {
                                DebugLogger.Info("ModuleManager", $"Initialized: {module.ModuleName}");
                            }
                        }
                        catch (Exception ex)
                        {
                            string moduleName = ResolveModuleName(module);
                            RecordModuleFailure(moduleName, "Initialize", ex, notifyUser: true);
                            BanditMilitias.Core.Registry.ModuleRegistry.Instance
                                .MarkFailed(module, ex.Message);
                        }
                    }

                    _isInitialized = true;

                    timer.Stop();

                    if (Settings.Instance?.TestingMode == true)
                    {
                        DebugLogger.Info("ModuleManager",
                            $"Initialization complete in {timer.Elapsed.TotalMilliseconds:F2}ms " +
                            $"({enabledModules.Length} modules, {_failedModules.Count} failed)");
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Error("ModuleManager", $"Critical initialization error: {ex.Message}");
                    InformationManager.DisplayMessage(new InformationMessage(
                        "[BanditMilitias] ModuleManager partial init failure - continuing in degraded mode",
                        Colors.Yellow));
                    _isInitialized = true;
                }
            }
        }

        public void RegisterModuleCampaignEvents()
        {
            lock (_initLock)
            {
                if (_campaignEventsRegistered)
                {
                    return;
                }

                if (!RegisterModuleCampaignEventsCore())
                {
                    throw new InvalidOperationException("One or more modules failed during RegisterCampaignEvents.");
                }
                _campaignEventsRegistered = true;
            }
        }

        public void CompleteSessionBootstrap()
        {
            lock (_initLock)
            {
                if (!_isInitialized)
                {
                    throw new InvalidOperationException("ModuleManager must be initialized before session bootstrap completes.");
                }

                if (_sessionBootstrapComplete)
                {
                    return;
                }

                if (!_campaignEventsRegistered)
                {
                    if (!RegisterModuleCampaignEventsCore())
                    {
                        throw new InvalidOperationException("One or more modules failed during RegisterCampaignEvents.");
                    }
                    _campaignEventsRegistered = true;
                }

                if (!RunModuleSessionStartCore())
                {
                    throw new InvalidOperationException("One or more modules failed during OnSessionStart.");
                }
                _sessionBootstrapComplete = true;
            }
        }

        private bool RegisterModuleCampaignEventsCore()
        {
            var moduleSnapshot = GetSortedModuleSnapshot();
            bool allSucceeded = true;

            foreach (var module in moduleSnapshot)
            {
                if (!module.IsEnabled) continue;
                try
                {
                    module.RegisterCampaignEvents();
                    BanditMilitias.Core.Registry.ModuleRegistry.Instance.MarkHealthy(module, "RegisterCampaignEvents");
                }
                catch (Exception ex)
                {
                    allSucceeded = false;
                    DebugLogger.Error("ModuleManager",
                        $"[RegisterCampaignEvents] {ResolveModuleName(module)}: {ex.Message}");
                    BanditMilitias.Core.Registry.ModuleRegistry.Instance.MarkFailed(module, ex.Message);
                }
            }

            return allSucceeded;
        }

        private bool RunModuleSessionStartCore()
        {
            var moduleSnapshot = GetSortedModuleSnapshot();
            bool allSucceeded = true;

            foreach (var module in moduleSnapshot)
            {
                if (!module.IsEnabled) continue;
                Type moduleType = module.GetType();
                if (_sessionStartedModules.Contains(moduleType)) continue;
                try
                {
                    module.OnSessionStart();
                    _ = _sessionStartedModules.Add(moduleType);
                    BanditMilitias.Core.Registry.ModuleRegistry.Instance.MarkHealthy(module, "OnSessionStart");
                }
                catch (Exception ex)
                {
                    allSucceeded = false;
                    DebugLogger.Error("ModuleManager",
                        $"[OnSessionStart] {ResolveModuleName(module)}: {ex.Message}");
                    BanditMilitias.Core.Registry.ModuleRegistry.Instance.MarkFailed(module, ex.Message);
                }
            }

            return allSucceeded;
        }

        public void RegisterMilitia(MobileParty party)
        {
            if (party == null) return;
            var cleanup = GetModule<BanditMilitias.Systems.Cleanup.PartyCleanupSystem>();

            lock (_registryLock)
            {
                if (_registeredMilitias.Add(party))
                {
                    _snapshotDirty = true;
                    _registryOperations++;
                    try
                    {
                        cleanup?.RegisterNewParty(party);
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Warning("ModuleManager", $"Cleanup registration failed for {party.StringId}: {ex.Message}");
                    }
                }
            }
        }

        public void UnregisterMilitia(MobileParty party)
        {
            if (party == null) return;

            lock (_registryLock)
            {
                if (_registeredMilitias.Remove(party))
                {
                    _snapshotDirty = true;
                    _registryOperations++;
                }
            }
        }

        public IReadOnlyList<MobileParty> ActiveMilitias
        {
            get
            {
                if (!_sessionBootstrapComplete)
                {
                    return _militiaSnapshot;
                }

                lock (_registryLock)
                {
                    if (_snapshotDirty)
                    {
                        _cacheMisses++;
                        RebuildMilitiaSnapshotUnsafe();
                    }

                    else
                    {
                        _cacheHits++;
                    }

                    return _militiaSnapshot;
                }
            }
        }

        public int GetMilitiaCount()
        {
            if (!_sessionBootstrapComplete)
            {
                return _militiaSnapshot.Length;
            }

            lock (_registryLock)
            {
                if (_snapshotDirty)
                {
                    _cacheMisses++;
                    RebuildMilitiaSnapshotUnsafe();
                }
                else
                {
                    _cacheHits++;
                }

                return _militiaSnapshot.Length;
            }
        }

        public void ValidateRegistry()
        {
            lock (_registryLock)
            {

                int removed = PruneRegisteredMilitiasSafely();

                if (removed > 0)
                {
                    _snapshotDirty = true;
                    if (Settings.Instance?.TestingMode == true)
                    {
                        DebugLogger.TestLog($"[ModuleManager] Cleaned {removed} phantom parties from registry.", Colors.Yellow);
                    }
                }
            }
        }

        private void RebuildMilitiaSnapshot()
        {
            lock (_registryLock)
            {
                RebuildMilitiaSnapshotUnsafe();
            }
        }

        private void RebuildMilitiaSnapshotUnsafe()
        {
            if (!_snapshotDirty) return;

            _militiaSnapshot = _registeredMilitias.ToArray();
            _snapshotDirty = false;
            _snapshotRebuilds++;
        }

        public void PopulateSnapshot(List<MobileParty> targetList)
        {
            if (targetList == null) return;
            targetList.AddRange(ActiveMilitias);
        }

        public IReadOnlyList<Settlement> HideoutCache => _hideoutCache;
        public IReadOnlyList<Settlement> VillageCache => _villageCache;
        public IReadOnlyList<Settlement> TownCache => _townCache;
        public IReadOnlyList<Settlement> CastleCache => _castleCache;
        public bool IsSessionBootstrapComplete => _sessionBootstrapComplete;

        public void RebuildCaches()
        {
            lock (_registryLock)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();

                if (Settings.Instance?.TestingMode == true)
                {
                    RevealAllHideouts();
                }

                _ = PruneRegisteredMilitiasSafely();
                _hideoutCache.Clear();
                _villageCache.Clear();
                _townCache.Clear();
                _castleCache.Clear();

                try
                {
                    foreach (var party in CompatibilityLayer.GetSafeMobileParties())
                    {
                        if (party == null) continue;

                        if (party.IsMilitia())
                        {
                            _ = _registeredMilitias.Add(party);
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("ModuleManager", $"MobileParty scan failed during cache rebuild: {ex.Message}");
                }

                try
                {
                    foreach (var settlement in CompatibilityLayer.GetSafeSettlements())
                    {
                        if (settlement == null) continue;

                        if (settlement.IsVillage && settlement.IsActive)
                        {
                            _villageCache.Add(settlement);
                            IndexSettlement(settlement);
                        }
                        else if (settlement.IsTown && settlement.IsActive)
                        {
                            _townCache.Add(settlement);
                            IndexSettlement(settlement);
                        }
                        else if (settlement.IsCastle && settlement.IsActive)
                        {
                            _castleCache.Add(settlement);
                            IndexSettlement(settlement);
                        }
                        else if (settlement.IsHideout || settlement.SettlementComponent is Hideout)
                        {
                            _hideoutCache.Add(settlement);
                            IndexSettlement(settlement);


                            if (Settings.Instance?.TestingMode == true)
                            {
                                settlement.IsVisible = true;
                                if (settlement.Hideout != null)
                                {
                                    settlement.Hideout.IsSpotted = true;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("ModuleManager", $"Settlement scan failed during cache rebuild: {ex.Message}");
                }

                Systems.Grid.SpatialGridSystem.Instance.RebuildSettlementGrid(_hideoutCache, _villageCache, _townCache, _castleCache);
                _snapshotDirty = true;
                _lastCacheRebuild = DateTime.Now;

                timer.Stop();

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Info("ModuleManager",
                        $"Cache rebuild: {_registeredMilitias.Count} militias, " +
                        $"{_hideoutCache.Count} hideouts, {_villageCache.Count} villages, " +
                        $"{_townCache.Count} towns ({timer.Elapsed.TotalMilliseconds:F2}ms)");
                }
            }
        }

        public void RefreshHideoutCache()
        {

            lock (_registryLock)
            {
                _hideoutCache.Clear();
                _villageCache.Clear();
                _townCache.Clear();
                _castleCache.Clear();
                _spatialIndex.Clear();

                try
                {
                    foreach (var settlement in CompatibilityLayer.GetSafeSettlements())
                    {
                        if (settlement == null) continue;

                        if (settlement.IsVillage && settlement.IsActive)
                        {
                            _villageCache.Add(settlement);
                            IndexSettlement(settlement);
                        }
                        else if (settlement.IsTown && settlement.IsActive)
                        {
                            _townCache.Add(settlement);
                            IndexSettlement(settlement);
                        }
                        else if (settlement.IsCastle && settlement.IsActive)
                        {
                            _castleCache.Add(settlement);
                            IndexSettlement(settlement);
                        }
                        else if (settlement.IsHideout || settlement.SettlementComponent is Hideout)
                        {
                            _hideoutCache.Add(settlement);
                            IndexSettlement(settlement);
                        }
                    }
                    Systems.Grid.SpatialGridSystem.Instance.RebuildSettlementGrid(_hideoutCache, _villageCache, _townCache, _castleCache);
                    _lastCacheRebuild = DateTime.Now;
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("ModuleManager", $"RefreshHideoutCache failed: {ex.Message}");
                }
            }
        }

        private void IndexSettlement(Settlement settlement)
        {
            if (settlement == null) return;

            Vec2 pos;
            try
            {
                pos = CompatibilityLayer.GetSettlementPosition(settlement);
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("ModuleManager", $"Settlement position resolve failed for {settlement.StringId}: {ex.Message}");
                return;
            }

            if (!pos.IsValid || float.IsNaN(pos.X) || float.IsNaN(pos.Y) || float.IsInfinity(pos.X) || float.IsInfinity(pos.Y))
            {
                return;
            }

            long cellKey = GetSpatialCell(pos);
            if (!_spatialIndex.TryGetValue(cellKey, out var cell))
            {
                cell = new List<Settlement>();
                _spatialIndex[cellKey] = cell;
            }

            cell.Add(settlement);
        }

        private int PruneRegisteredMilitiasSafely()
        {
            if (_registeredMilitias.Count == 0) return 0;

            List<MobileParty>? staleParties = null;
            bool hasNullEntries = false;

            foreach (var party in _registeredMilitias)
            {
                if (party == null)
                {
                    hasNullEntries = true;
                    continue;
                }

                bool shouldRemove = false;
                try
                {
                    shouldRemove = !party.IsActive || party.Party == null;
                }
                catch
                {
                    shouldRemove = true;
                }

                if (!shouldRemove) continue;

                staleParties ??= new List<MobileParty>();
                staleParties.Add(party);
            }

            int removed = 0;
            if (hasNullEntries)
            {
                removed += _registeredMilitias.RemoveWhere(p => p == null);
            }

            if (staleParties == null) return removed;

            foreach (var party in staleParties)
            {
                if (_registeredMilitias.Remove(party))
                {
                    removed++;
                }
            }
            return removed;
        }

        private long GetSpatialCell(Vec2 position)
        {
            int x = (int)(position.X / SPATIAL_GRID_SIZE);
            int y = (int)(position.Y / SPATIAL_GRID_SIZE);
            return ((long)x << 32) | (uint)y;
        }

        public List<Settlement> GetNearbySettlements(Vec2 position, float radius, SettlementType type = SettlementType.Any)
        {
            var results = new List<Settlement>();
            if (!position.IsValid || radius <= 0f)
            {
                return results;
            }


            List<Settlement> candidates;
            lock (_registryLock)
            {
                candidates = new List<Settlement>();
                int cellRadius = (int)Math.Ceiling(radius / SPATIAL_GRID_SIZE);
                long centerCell = GetSpatialCell(position);
                int cx = (int)(centerCell >> 32);
                int cy = (int)(centerCell & 0xFFFFFFFF);

                for (int dx = -cellRadius; dx <= cellRadius; dx++)
                {
                    for (int dy = -cellRadius; dy <= cellRadius; dy++)
                    {
                        long cellKey = ((long)(cx + dx) << 32) | (uint)(cy + dy);

                        if (_spatialIndex.TryGetValue(cellKey, out var cell))
                        {
                            foreach (var settlement in cell)
                            {
                                if (type != SettlementType.Any)
                                {
                                    bool matches = type switch
                                    {
                                        SettlementType.Hideout => settlement.IsHideout,
                                        SettlementType.Village => settlement.IsVillage,
                                        SettlementType.Town => settlement.IsTown,
                                        _ => true
                                    };

                                    if (!matches) continue;
                                }
                                candidates.Add(settlement);
                            }
                        }
                    }
                }
            }


            foreach (var settlement in candidates)
            {
                Vec2 settPos = CompatibilityLayer.GetSettlementPosition(settlement);
                if (!settPos.IsValid) continue;
                if (position.Distance(settPos) <= radius)
                {
                    results.Add(settlement);
                }
            }

            return results;
        }


        internal void RevealAllHideouts()
        {
            if (Settings.Instance?.TestingMode != true)
            {
                DebugLogger.Warning("ModuleManager", "RevealAllHideouts requires TestingMode");
                return;
            }

            lock (_registryLock)
            {
                int revealed = 0;

                foreach (var settlement in Settlement.All)
                {
                    if (settlement?.IsHideout != true || settlement.IsActive) continue;

                    try
                    {

                        var isActiveField = typeof(Settlement).GetField("_isVisible",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                        if (isActiveField != null)
                        {
                            isActiveField.SetValue(settlement, true);
                            settlement.IsVisible = true;
                            settlement.Party?.SetVisualAsDirty();
                            revealed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Warning("ModuleManager", $"Failed to reveal {settlement.Name}: {ex.Message}");
                    }
                }

                if (revealed > 0)
                {
                    RefreshHideoutCache();
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[BanditMilitias] Revealed {revealed} hidden hideouts",
                        Colors.Cyan));
                }
            }
        }

        public void OnDailyTick()
        {
            if (!_sessionBootstrapComplete)
            {
                return;
            }

            ModActivationManager.InvalidateGameInitializationCache();

            if ((DateTime.Now - _lastCacheRebuild).TotalHours > CACHE_REBUILD_INTERVAL_HOURS)
            {

                RebuildCaches();
            }

            ValidateRegistry();

            int recoveredModules = ResetFailedModulesForRetry();
            if (recoveredModules > 0)
            {
                DebugLogger.Info("ModuleManager",
                    $"Daily reset: Re-enabling {recoveredModules} failed modules for retry.");
            }

            Intelligence.AI.ScoringFunctions.CleanStaleCaches();
            Intelligence.AI.Components.MilitiaSmartCache.Instance.Clear();

            ProcessModuleTicks(m => m.OnDailyTick(), "Daily");
        }

        public void OnHourlyTick()
        {
            if (!_sessionBootstrapComplete)
            {
                return;
            }

            ProcessModuleTicks(m => m.OnHourlyTick(), "Hourly");
        }

        public void OnApplicationTick(float dt)
        {
            if (!_sessionBootstrapComplete)
            {
                return;
            }

            ProcessModuleTicks(m => m.OnTick(dt), "Application", dt);
        }

        private void ProcessModuleTicks(Action<IMilitiaModule> tickAction, string tickType, float dt = 0f)
        {
            if (!ModActivationManager.IsGameplayActivationSwitchClosed())
                return;

            IMilitiaModule[] moduleSnapshot = GetSortedModuleSnapshot();
            foreach (var module in moduleSnapshot)
            {
                if (!module.IsEnabled) continue;


                if (!_moduleNameCache.TryGetValue(module, out string? moduleName))
                {
                    moduleName = ResolveModuleName(module);
                    _moduleNameCache[module] = moduleName;
                }

                if (!_scopeNameCache.TryGetValue((module, tickType), out string? scopeName))
                {
                    scopeName = $"Tick.{tickType}.{moduleName}";
                    _scopeNameCache[(module, tickType)] = scopeName;
                }

                if (!module.IsCritical && IsModuleFailed(moduleName)) continue;
                if (tickType == "Application" &&
                    !_tickScheduler.ShouldRun(module, moduleName, dt, ModLifecycleManager.Instance.CurrentState))
                {
                    continue;
                }


                bool trackPerf = Settings.Instance?.TestingMode == true;
                if (trackPerf) _reusableStopwatch.Restart();

                DiagnosticsSystem.StartScope(scopeName);
                try
                {
                    tickAction(module);


                    if (tickType != "Application")
                    {
                        BanditMilitias.Core.Registry.ModuleRegistry.Instance.MarkHealthy(module, $"{tickType}Tick");
                    }

                    if (!module.IsCritical)
                    {
                        ClearTickFailureStreak(moduleName);
                    }

                    DiagnosticsSystem.IncrementMetric($"Tick.{tickType}.Success");
                }
                catch (Exception ex)
                {
                    DiagnosticsSystem.IncrementMetric($"Tick.{tickType}.Failure");

                    string fullError = $"[{tickType}Tick] {moduleName}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
                    DebugLogger.Error("ModuleManager", fullError);
                    try { BanditMilitias.Infrastructure.FileLogger.LogError(fullError); } catch { }

                    BanditMilitias.Core.Registry.ModuleRegistry.Instance.MarkFailed(module, ex.Message);

                    if (!module.IsCritical)
                    {
                        int failureStreak = IncrementTickFailureStreak(moduleName);
                        if (failureStreak >= MaxConsecutiveTickFailures)
                        {
                            RecordModuleFailure(moduleName, $"{tickType}Tick", ex, notifyUser: true);
                        }
                        else
                        {
                            DebugLogger.Warning("ModuleManager",
                                $"{moduleName} {tickType} tick failed ({failureStreak}/{MaxConsecutiveTickFailures}) before disable threshold: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    if (trackPerf)
                    {
                        _reusableStopwatch.Stop();
                        RecordModuleTick(moduleName, _reusableStopwatch.Elapsed.TotalMilliseconds);
                    }
                    DiagnosticsSystem.EndScope(scopeName);
                }
            }
        }

        private void RecordModuleTick(string moduleName, double elapsedMs)
        {
            if (string.IsNullOrWhiteSpace(moduleName)) return;
            float ms = (float)Math.Max(0.0, elapsedMs);
            lock (_perfLock)
            {
                _moduleLastTickMs[moduleName] = ms;
                if (_moduleAvgTickMs.TryGetValue(moduleName, out var avg))
                {
                    _moduleAvgTickMs[moduleName] = (avg * (1f - PerfEmaAlpha)) + (ms * PerfEmaAlpha);
                }
                else
                {
                    _moduleAvgTickMs[moduleName] = ms;
                }
            }
        }

        public string GetSlowModuleReport(int maxEntries = 10)
        {
            lock (_perfLock)
            {
                if (_moduleAvgTickMs.Count == 0)
                {
                    return "No perf data. Enable TestingMode to collect module timings.";
                }

                var top = _moduleAvgTickMs
                    .OrderByDescending(kvp => kvp.Value)
                    .Take(Math.Max(1, maxEntries))
                    .Select(kvp =>
                    {
                        float last = _moduleLastTickMs.TryGetValue(kvp.Key, out var lm) ? lm : 0f;
                        return $"{kvp.Key}: avg={kvp.Value:F2}ms, last={last:F2}ms";
                    })
                    .ToArray();

                return "=== SLOW MODULES (BanditMilitias) ===\n"
                       + string.Join("\n", top)
                       + "\n====================================";
            }
        }

        private IMilitiaModule[] GetSortedModuleSnapshot()
        {
            lock (_initLock)
            {
                if (_modulesDirty)
                {
                    _sortedModuleSnapshot = BuildDependencySortedSnapshot();
                    _modulesDirty = false;
                }

                return _sortedModuleSnapshot;
            }
        }

        private IMilitiaModule[] BuildDependencySortedSnapshot()
        {
            var modules = _modules
                .OrderByDescending(m => m.Priority)
                .ThenBy(m => ResolveModuleName(m), ModuleNameComparer)
                .ToArray();

            if (modules.Length <= 1)
            {
                return modules;
            }

            var moduleByType = new Dictionary<Type, IMilitiaModule>();
            var indegree = new Dictionary<IMilitiaModule, int>();
            var edges = new Dictionary<IMilitiaModule, List<IMilitiaModule>>();

            foreach (var module in modules)
            {
                moduleByType[module.GetType()] = module;
                indegree[module] = 0;
                edges[module] = new List<IMilitiaModule>();
            }

            foreach (var module in modules)
            {
                var dependencyAttr = (ModuleDependencyAttribute?)Attribute.GetCustomAttribute(
                    module.GetType(),
                    typeof(ModuleDependencyAttribute),
                    inherit: false);

                if (dependencyAttr?.Dependencies == null || dependencyAttr.Dependencies.Length == 0)
                {
                    continue;
                }

                foreach (var dependencyType in dependencyAttr.Dependencies)
                {
                    if (dependencyType == null)
                    {
                        continue;
                    }

                    IMilitiaModule? dependencyModule = null;
                    if (!moduleByType.TryGetValue(dependencyType, out dependencyModule))
                    {
                        dependencyModule = modules.FirstOrDefault(m => dependencyType.IsAssignableFrom(m.GetType()));
                    }

                    if (dependencyModule == null || ReferenceEquals(dependencyModule, module))
                    {
                        continue;
                    }

                    edges[dependencyModule].Add(module);
                    indegree[module] = indegree[module] + 1;
                }
            }

            var ready = new List<IMilitiaModule>(
                indegree.Where(kvp => kvp.Value == 0).Select(kvp => kvp.Key));
            var ordered = new List<IMilitiaModule>(modules.Length);

            while (ready.Count > 0)
            {
                ready.Sort((a, b) =>
                {
                    int byPriority = b.Priority.CompareTo(a.Priority);
                    return byPriority != 0
                        ? byPriority
                        : ModuleNameComparer.Compare(ResolveModuleName(a), ResolveModuleName(b));
                });

                var current = ready[0];
                ready.RemoveAt(0);
                ordered.Add(current);

                foreach (var next in edges[current])
                {
                    indegree[next] = indegree[next] - 1;
                    if (indegree[next] == 0)
                    {
                        ready.Add(next);
                    }
                }
            }

            if (ordered.Count == modules.Length)
            {
                return ordered.ToArray();
            }

            FileLogger.LogWarning("[ModuleManager] Dependency cycle detected during module sort. Falling back to priority order.");
            return modules;
        }

        public bool SyncData(IDataStore dataStore)
        {
            int failureCount = 0;
            var failedModuleNames = new List<string>();

            foreach (var module in GetSortedModuleSnapshot())
            {
                if (!module.IsEnabled) continue;

                string moduleName = ResolveModuleName(module);

                try
                {
                    module.SyncData(dataStore);
                    BanditMilitias.Core.Registry.ModuleRegistry.Instance.MarkHealthy(
                        module,
                        dataStore.IsLoading ? "SyncDataLoad" : "SyncDataSave");
                }
                catch (Exception ex)
                {
                    failureCount++;
                    failedModuleNames.Add(moduleName);
                    RecordModuleFailure(moduleName, "SyncData", ex, notifyUser: false);
                    BanditMilitias.Core.Registry.ModuleRegistry.Instance.MarkFailed(module, ex.Message);
                }
            }

            if (dataStore.IsLoading)
            {
                RebuildCaches();
            }

            if (failureCount > 0)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[BanditMilitias] Save/Load issue: {string.Join(", ", failedModuleNames.Distinct())}",
                    Colors.Red));
                return false;
            }

            return true;
        }

        public void CleanupAll()
        {
            lock (_initLock)
            {

                var modulesToClean = _modules.ToList();
                modulesToClean.Reverse();

                foreach (var module in modulesToClean)
                {
                    try
                    {
                        module.Cleanup();
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Error("ModuleManager",
                            $"{module.ModuleName} cleanup failed: {ex.Message}");
                    }
                    finally
                    {
                        BanditMilitias.Core.Registry.ModuleRegistry.Instance.MarkRemoved(module);
                    }
                }

                _modules.Clear();
                _modulesByType.Clear();
                _moduleTypesByName.Clear();
                _failedModules.Clear();
                _lastErrorTime.Clear();
                _failureRecords.Clear();
                _moduleTickFailureStreaks.Clear();
                _sortedModuleSnapshot = Array.Empty<IMilitiaModule>();
                _modulesDirty = true;


                _moduleNameCache.Clear();
                _scopeNameCache.Clear();

                lock (_perfLock)
                {
                    _moduleLastTickMs.Clear();
                    _moduleAvgTickMs.Clear();
                }

                lock (_registryLock)
                {
                    _registeredMilitias.Clear();
                    _militiaSnapshot = Array.Empty<MobileParty>();
                    _hideoutCache.Clear();
                    _villageCache.Clear();
                    _townCache.Clear();
                    _castleCache.Clear();
                    _spatialIndex.Clear();
                }

                ClanCache.Reset();

                _isInitialized = false;
                _campaignEventsRegistered = false;
                _sessionBootstrapComplete = false;
                _sessionStartedModules.Clear();
                _cacheHits = 0;
                _cacheMisses = 0;
                _snapshotRebuilds = 0;
                _registryOperations = 0;
                _lastCacheRebuild = DateTime.MinValue;
            }
        }

        public T? GetModule<T>() where T : class, IMilitiaModule
        {
            return GetModule(typeof(T)) as T;
        }

        public IMilitiaModule? GetModule(Type type)
        {
            lock (_initLock)
            {
                if (_modulesByType.TryGetValue(type, out var module))
                {
                    return module;
                }

                foreach (var m in _modules)
                {
                    if (type.IsAssignableFrom(m.GetType()))
                    {


                        _modulesByType[type] = m;
                        return m;
                    }
                }

                return null;
            }
        }

        public void DumpDiagnostics()
        {
            var sb = new System.Text.StringBuilder();
            _ = sb.AppendLine("=== BANDIT MILITIAS DIAGNOSTICS ===");
            _ = sb.AppendLine(GetDiagnostics());
            _ = sb.AppendLine();
            _ = sb.AppendLine(GetFailedModulesReport());
            _ = sb.AppendLine();

            foreach (var module in _modules)
            {
                string info = module.GetDiagnostics();
                _ = sb.AppendLine($"[{module.ModuleName}] {info}");
            }

            _ = sb.AppendLine("===================================");

            string output = sb.ToString();
            TaleWorlds.Library.Debug.Print(output);
            InformationManager.DisplayMessage(new InformationMessage(output, Colors.White));
        }

        public string GetDiagnostics()
        {
            lock (_initLock)
            {
                int enabledModules = GetSortedModuleSnapshot().Count(m => m.IsEnabled);
                string failedSummary = GetFailedModuleSummary();

                return $"ModuleManager:\n" +
                       $"  Modules: {enabledModules}/{_modules.Count} enabled, {_failedModules.Count} failed\n" +
                       $"  Failed Summary: {failedSummary}\n" +
                       $"  Failure Records: {_failureRecords.Count} (command: militia.failed_modules)\n" +
                       $"  Militias: {_registeredMilitias.Count} active\n" +
                       $"  Settlements: {_hideoutCache.Count} hideouts, {_villageCache.Count} villages, {_townCache.Count} towns\n" +
                       $"  Spatial Cells: {_spatialIndex.Count}\n" +
                       $"  Operations: {_registryOperations} registry, {_snapshotRebuilds} rebuilds\n" +
                       $"  Cache: {_cacheHits} hits, {_cacheMisses} misses\n" +
                       $"  Last Rebuild: {(DateTime.Now - _lastCacheRebuild).TotalMinutes:F1}m ago";
            }
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("system_status", "militia")]
        public static string DebugSystemStatus(List<string> args)
        {
            var sb = new System.Text.StringBuilder();
            _ = sb.AppendLine("=== BANDIT MILITIAS SYSTEM STATUS ===");
            _ = sb.AppendLine(Instance.GetDiagnostics());
            _ = sb.AppendLine();

            foreach (var module in Instance.GetSortedModuleSnapshot())
            {
                if (!module.IsEnabled) continue;
                string info = module.GetDiagnostics();
                _ = sb.AppendLine($"[{module.ModuleName}] {info}");
            }

            _ = sb.AppendLine("=====================================");
            return sb.ToString();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("slow_modules", "militia")]
        public static string DebugSlowModules(List<string> args)
        {
            int maxEntries = 10;
            if (args != null && args.Count > 0 && int.TryParse(args[0], out int parsed))
            {
                maxEntries = Math.Max(1, parsed);
            }

            return Instance.GetSlowModuleReport(maxEntries);
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("failed_modules", "militia")]
        public static string DebugFailedModules(List<string> args)
        {
            int maxEntries = 25;
            if (args != null && args.Count > 0 && int.TryParse(args[0], out int parsed))
            {
                maxEntries = Math.Max(1, parsed);
            }

            return Instance.GetFailedModulesReport(maxEntries);
        }
    }

    public enum SettlementType
    {
        Any,
        Hideout,
        Village,
        Town
    }
}
