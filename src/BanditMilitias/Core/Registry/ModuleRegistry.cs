using BanditMilitias.Core.Components;
using BanditMilitias.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using TaleWorlds.Library;

namespace BanditMilitias.Core.Registry
{
    public enum ModuleStatus
    {
        Discovered,
        Registered,
        Disabled,
        Failed,
        Removed,
    }

    public sealed class AuditOptions
    {
        public DateTime? UtcNow { get; set; }
        public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(30);
        public TimeSpan DeadAfter { get; set; } = TimeSpan.FromMinutes(10);
        public bool RefreshLiveDiagnostics { get; set; } = true;

        public DateTime ResolveUtcNow()
        {
            DateTime now = UtcNow ?? DateTime.UtcNow;
            return now.Kind == DateTimeKind.Utc ? now : now.ToUniversalTime();
        }
    }

    public sealed class ModuleEntry
    {
        public Type Type { get; set; } = typeof(object);
        public string Name { get; set; } = string.Empty;
        public string ModuleName { get; set; } = string.Empty;
        public bool ModuleNameResolved { get; set; }
        public string DisplayName => string.IsNullOrWhiteSpace(ModuleName) ? Name : ModuleName;
        public ModuleStatus Status { get; set; } = ModuleStatus.Discovered;
        public bool IsCritical { get; set; }
        public int Priority { get; set; }
        public bool IsEnabled { get; set; }
        public string? FailReason { get; set; }
        public DateTime LastChanged { get; set; } = DateTime.UtcNow;
        public DateTime LastChangedUtc =>
            LastChanged.Kind == DateTimeKind.Utc ? LastChanged : LastChanged.ToUniversalTime();
        public DateTime? LastConfirmedUtc { get; set; }
        public DateTime? LastHealthyUtc { get; set; }
        public DateTime? LastActivityUtc { get; set; }
        public string? LastActivity { get; set; }
        public DateTime? LastDiagnosticsUtc { get; set; }
        public string? LastDiagnostics { get; set; }
        public string? DiagnosticIssue { get; set; }
        public int SuccessfulOperations { get; set; }
        public int FailureCount { get; set; }

        public int SubscribeCount { get; set; }
        public int UnsubscribeCount { get; set; }

        public bool HasEventLeak => SubscribeCount > UnsubscribeCount;
        public bool HasRuntimeActivity => SuccessfulOperations > 0;
        public bool HasSilentFailureSignal => !string.IsNullOrWhiteSpace(DiagnosticIssue);

        public ModuleEntry Clone()
        {
            return new ModuleEntry
            {
                Type = Type,
                Name = Name,
                ModuleName = ModuleName,
                ModuleNameResolved = ModuleNameResolved,
                Status = Status,
                IsCritical = IsCritical,
                Priority = Priority,
                IsEnabled = IsEnabled,
                FailReason = FailReason,
                LastChanged = LastChanged,
                LastConfirmedUtc = LastConfirmedUtc,
                LastHealthyUtc = LastHealthyUtc,
                LastActivityUtc = LastActivityUtc,
                LastActivity = LastActivity,
                LastDiagnosticsUtc = LastDiagnosticsUtc,
                LastDiagnostics = LastDiagnostics,
                DiagnosticIssue = DiagnosticIssue,
                SuccessfulOperations = SuccessfulOperations,
                FailureCount = FailureCount,
                SubscribeCount = SubscribeCount,
                UnsubscribeCount = UnsubscribeCount,
            };
        }

        public override string ToString()
        {
            string label = DisplayName == Name ? Name : $"{DisplayName} ({Name})";
            return $"[{Status,-10}] {label,-40} P={Priority,3} Crit={IsCritical}"
                 + (FailReason != null ? $" FAIL={FailReason}" : string.Empty);
        }
    }

    public sealed class ModuleRegistry
    {
        private static readonly Lazy<ModuleRegistry> _lazy = new(() => new ModuleRegistry());
        private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
        private static readonly string[] SuspiciousDiagnosticTokens =
        {
            "error",
            "exception",
            "failed",
            "not found",
            " is null",
            " null ",
            "invalid",
            "uninitialized",
            "missing",
        };

        private readonly object _syncRoot = new object();
        private readonly Dictionary<Type, ModuleEntry> _entries = new Dictionary<Type, ModuleEntry>();
        private readonly Dictionary<string, Type> _aliases = new Dictionary<string, Type>(NameComparer);
        private readonly Dictionary<Type, WeakReference<IMilitiaModule>> _instances =
            new Dictionary<Type, WeakReference<IMilitiaModule>>();

        public static ModuleRegistry Instance => _lazy.Value;

        private ModuleRegistry()
        {
        }

        public void Discover(Assembly? assembly = null)
        {
            Assembly sourceAssembly = assembly ?? typeof(ModuleRegistry).Assembly;
            Type[] moduleTypes = GetLoadableTypes(sourceAssembly)
                .Where(IsDiscoverableModuleType)
                .OrderBy(t => t.Name, NameComparer)
                .ToArray();

            int added = 0;
            lock (_syncRoot)
            {
                foreach (Type moduleType in moduleTypes)
                {
                    bool existed = _entries.ContainsKey(moduleType);
                    _ = EnsureEntryNoLock(moduleType, moduleName: null, logUnknown: false);
                    if (!existed)
                    {
                        added++;
                    }
                }
            }

            SafeLog($"[ModuleRegistry] Discover({sourceAssembly.GetName().Name}) added={added}, total={moduleTypes.Length}");
        }

        public void Confirm(IMilitiaModule module)
        {
            if (module == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                ModuleEntry entry = EnsureEntryNoLock(module.GetType(), moduleName: null, logUnknown: true);
                ApplyModuleStateNoLock(
                    entry,
                    module,
                    module.IsEnabled ? ModuleStatus.Registered : ModuleStatus.Disabled,
                    failReason: null,
                    resetEventCounters: true,
                    markConfirmed: true,
                    countSuccessfulOperation: false,
                    activityLabel: "Confirm");
            }
        }

        public void MarkHealthy(IMilitiaModule? module, string activityLabel = "Healthy")
        {
            if (module == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                ModuleEntry entry = EnsureEntryNoLock(module.GetType(), moduleName: null, logUnknown: true);
                ApplyModuleStateNoLock(
                    entry,
                    module,
                    module.IsEnabled ? ModuleStatus.Registered : ModuleStatus.Disabled,
                    failReason: null,
                    resetEventCounters: false,
                    markConfirmed: false,
                    countSuccessfulOperation: true,
                    activityLabel: activityLabel);
            }
        }

        public void MarkFailed(IMilitiaModule? module, string? reason)
        {
            if (module == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                ModuleEntry entry = EnsureEntryNoLock(module.GetType(), moduleName: null, logUnknown: true);
                ApplyModuleStateNoLock(
                    entry,
                    module,
                    ModuleStatus.Failed,
                    NormalizeReason(reason),
                    resetEventCounters: false,
                    markConfirmed: false,
                    countSuccessfulOperation: false,
                    activityLabel: "Failed");
            }
        }

        public void MarkRemoved(IMilitiaModule? module)
        {
            if (module == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                ModuleEntry entry = EnsureEntryNoLock(module.GetType(), moduleName: null, logUnknown: true);
                ApplyModuleStateNoLock(
                    entry,
                    module,
                    ModuleStatus.Removed,
                    failReason: null,
                    resetEventCounters: false,
                    markConfirmed: false,
                    countSuccessfulOperation: false,
                    activityLabel: "Removed");
            }
        }

        public void RecordEventSubscription(Delegate? handler, bool subscribed)
        {
            if (handler == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                ModuleEntry? entry = ResolveEventOwnerEntryNoLock(handler);
                if (entry == null)
                {
                    return;
                }

                ApplyEventCounterNoLock(entry, subscribed);
            }
        }

        public void RecordEventSubscription(Type? ownerType, bool subscribed)
        {
            if (ownerType == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                ModuleEntry? entry = TryResolveEntryFromTypeNoLock(ownerType);
                if (entry == null)
                {
                    return;
                }

                ApplyEventCounterNoLock(entry, subscribed);
            }
        }

        public void RecordActivity(Delegate? handler, string activityLabel = "Runtime")
        {
            if (handler == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                ModuleEntry? entry = ResolveEventOwnerEntryNoLock(handler);
                if (entry == null)
                {
                    return;
                }

                RecordActivityNoLock(entry, activityLabel, DateTime.UtcNow, countSuccessfulOperation: true);
                TouchNoLock(entry);
            }
        }

        public void RecordFailure(Delegate? handler, string? reason)
        {
            if (handler == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                ModuleEntry? entry = ResolveEventOwnerEntryNoLock(handler);
                if (entry == null)
                {
                    return;
                }

                ApplyFailureNoLock(entry, NormalizeReason(reason), "EventHandlerFailed", DateTime.UtcNow);
                TouchNoLock(entry);
            }
        }

        public void ResetEventSubscriptions()
        {
            lock (_syncRoot)
            {
                foreach (ModuleEntry entry in _entries.Values)
                {
                    if (entry.SubscribeCount == 0 && entry.UnsubscribeCount == 0)
                    {
                        continue;
                    }

                    entry.SubscribeCount = 0;
                    entry.UnsubscribeCount = 0;
                    TouchNoLock(entry);
                }
            }
        }

        public void Reset()
        {
            lock (_syncRoot)
            {
                _entries.Clear();
                _aliases.Clear();
                _instances.Clear();
            }
        }

        public AuditResult Audit(AuditOptions? options = null)
        {
            AuditOptions auditOptions = options ?? new AuditOptions();
            DateTime now = auditOptions.ResolveUtcNow();

            if (auditOptions.RefreshLiveDiagnostics)
            {
                RefreshTrackedDiagnostics(now);
            }

            ModuleEntry[] snapshot = SnapshotEntries();
            var result = new AuditResult
            {
                GeneratedAtUtc = now,
                TotalDiscovered = snapshot.Length,
                TotalRegistered = snapshot.Count(e => e.Status != ModuleStatus.Discovered),
            };

            foreach (ModuleEntry entry in snapshot)
            {
                switch (entry.Status)
                {
                    case ModuleStatus.Discovered:
                        result.Unregistered.Add(entry);
                        break;
                    case ModuleStatus.Registered:
                        result.Healthy.Add(entry);
                        break;
                    case ModuleStatus.Disabled:
                        result.Disabled.Add(entry);
                        break;
                    case ModuleStatus.Failed:
                        result.Failed.Add(entry);
                        break;
                    case ModuleStatus.Removed:
                        result.Removed.Add(entry);
                        break;
                }

                if (entry.HasEventLeak)
                {
                    result.EventLeaks.Add(entry);
                }

                if (entry.Status != ModuleStatus.Registered)
                {
                    continue;
                }

                if (entry.HasSilentFailureSignal)
                {
                    result.SilentBroken.Add(entry);
                }

                if (IsDead(entry, now, auditOptions))
                {
                    result.Dead.Add(entry);
                    continue;
                }

                if (IsStale(entry, now, auditOptions))
                {
                    result.Stale.Add(entry);
                }
            }

            return result;
        }

        public ModuleEntry? Get(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            lock (_syncRoot)
            {
                if (!_aliases.TryGetValue(name.Trim(), out Type? type))
                {
                    return null;
                }

                return _entries.TryGetValue(type, out ModuleEntry? entry) ? entry.Clone() : null;
            }
        }

        public ModuleEntry? Get(Type type)
        {
            if (type == null)
            {
                return null;
            }

            lock (_syncRoot)
            {
                return TryResolveEntryFromTypeNoLock(type)?.Clone();
            }
        }

        public IReadOnlyCollection<ModuleEntry> All => SnapshotEntries();

        public IEnumerable<ModuleEntry> Unregistered => SnapshotByStatus(ModuleStatus.Discovered);

        public IEnumerable<ModuleEntry> Failed => SnapshotByStatus(ModuleStatus.Failed);

        public string GenerateReport(AuditOptions? options = null)
        {
            AuditResult audit = Audit(options);
            var sb = new StringBuilder();

            _ = sb.AppendLine("Module Registry Report");
            _ = sb.AppendLine($"Discovered : {audit.TotalDiscovered}");
            _ = sb.AppendLine($"Registered : {audit.TotalRegistered}");
            _ = sb.AppendLine($"Healthy    : {audit.Healthy.Count}");
            _ = sb.AppendLine($"Disabled   : {audit.Disabled.Count}");
            _ = sb.AppendLine($"Ghost      : {audit.Unregistered.Count}");
            _ = sb.AppendLine($"Failed     : {audit.Failed.Count}");
            _ = sb.AppendLine($"Silent     : {audit.SilentBroken.Count}");
            _ = sb.AppendLine($"Stale      : {audit.Stale.Count}");
            _ = sb.AppendLine($"Dead       : {audit.Dead.Count}");
            _ = sb.AppendLine($"EventLeak  : {audit.EventLeaks.Count}");
            _ = sb.AppendLine($"Removed    : {audit.Removed.Count}");

            AppendSection(
                sb,
                "Ghost Modules",
                audit.Unregistered.OrderBy(x => x.DisplayName, NameComparer),
                entry => entry.DisplayName);

            AppendSection(
                sb,
                "Failed Modules",
                audit.Failed.OrderBy(x => x.DisplayName, NameComparer),
                entry => string.IsNullOrWhiteSpace(entry.FailReason)
                    ? entry.DisplayName
                    : $"{entry.DisplayName}: {entry.FailReason}");

            AppendSection(
                sb,
                "Silent Broken Modules",
                audit.SilentBroken.OrderBy(x => x.DisplayName, NameComparer),
                entry => $"{entry.DisplayName}: {entry.DiagnosticIssue ?? entry.LastDiagnostics ?? "Diagnostics returned a suspicious signal"}");

            AppendSection(
                sb,
                "Stale Modules",
                audit.Stale.OrderBy(x => x.DisplayName, NameComparer),
                entry => $"{entry.DisplayName}: last activity {FormatAge(entry.LastActivityUtc ?? entry.LastHealthyUtc ?? entry.LastChangedUtc, audit.GeneratedAtUtc)} ago"
                    + (string.IsNullOrWhiteSpace(entry.LastActivity) ? string.Empty : $" via {entry.LastActivity}"));

            AppendSection(
                sb,
                "Dead Modules",
                audit.Dead.OrderBy(x => x.DisplayName, NameComparer),
                entry => $"{entry.DisplayName}: no successful runtime activity since {FormatUtc(entry.LastConfirmedUtc ?? entry.LastChangedUtc)}");

            AppendSection(
                sb,
                "Event Leak Findings",
                audit.EventLeaks.OrderBy(x => x.DisplayName, NameComparer),
                entry => $"{entry.DisplayName}: Subscribe={entry.SubscribeCount}, Unsubscribe={entry.UnsubscribeCount}");

            AppendSection(
                sb,
                "Removed Modules",
                audit.Removed.OrderBy(x => x.DisplayName, NameComparer),
                entry => entry.DisplayName);

            AppendSection(
                sb,
                "Healthy Modules",
                audit.Healthy.OrderByDescending(x => x.Priority).ThenBy(x => x.DisplayName, NameComparer),
                entry => $"P{entry.Priority:000} {entry.DisplayName}");

            return sb.ToString();
        }

        private static string ResolveModuleName(IMilitiaModule module)
        {
            if (module == null)
            {
                return nameof(IMilitiaModule);
            }

            try
            {
                string rawName = module.ModuleName ?? string.Empty;
                return string.IsNullOrWhiteSpace(rawName) ? module.GetType().Name : rawName.Trim();
            }
            catch (Exception ex)
            {
                string fallbackName;
                try
                {
                    fallbackName = module.GetType().Name;
                }
                catch
                {
                    fallbackName = nameof(IMilitiaModule);
                }

                SafeLog($"[ModuleRegistry] ResolveModuleName fallback for {fallbackName}: {ex.GetType().Name}: {ex.Message}");
                return fallbackName;
            }
        }

        private static string? NormalizeReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return null;
            }

            return reason!.Trim();
        }

        private static bool IsDiscoverableModuleType(Type? type)
        {
            return type != null
                && !type.IsAbstract
                && !type.IsInterface
                && typeof(IMilitiaModule).IsAssignableFrom(type)
                && type != typeof(MilitiaModuleBase);
        }

        private static Type[] GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                SafeLog($"[ModuleRegistry] Partial type load for {assembly.GetName().Name}: {ex.LoaderExceptions?.Length ?? 0}");
                return ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            }
        }

        private ModuleEntry EnsureEntryNoLock(Type type, string? moduleName, bool logUnknown)
        {
            if (!_entries.TryGetValue(type, out ModuleEntry? entry))
            {
                entry = new ModuleEntry
                {
                    Type = type,
                    Name = type.Name,
                    ModuleName = type.Name,
                    Status = ModuleStatus.Discovered,
                    LastChanged = DateTime.UtcNow,
                };
                _entries[type] = entry;

                if (logUnknown)
                {
                    SafeLog($"[ModuleRegistry] Added module outside discover path: {type.FullName}");
                }
            }

            RefreshIdentityNoLock(entry, moduleName);
            return entry;
        }

        private void TrackInstanceNoLock(IMilitiaModule module)
        {
            _instances[module.GetType()] = new WeakReference<IMilitiaModule>(module);
        }

        private void RefreshIdentityNoLock(ModuleEntry entry, string? moduleName)
        {
            entry.Name = entry.Type.Name;
            if (!string.IsNullOrWhiteSpace(moduleName))
            {
                entry.ModuleName = moduleName!.Trim();
                entry.ModuleNameResolved = true;
            }
            else if (string.IsNullOrWhiteSpace(entry.ModuleName))
            {
                entry.ModuleName = entry.Name;
            }

            RegisterAliasNoLock(entry.Name, entry.Type);
            RegisterAliasNoLock(entry.ModuleName, entry.Type);
        }

        private void ApplyModuleStateNoLock(
            ModuleEntry entry,
            IMilitiaModule module,
            ModuleStatus status,
            string? failReason,
            bool resetEventCounters,
            bool markConfirmed,
            bool countSuccessfulOperation,
            string activityLabel)
        {
            DateTime now = DateTime.UtcNow;
            RefreshIdentityNoLock(entry, ResolveModuleNameCachedNoLock(entry, module));
            TrackInstanceNoLock(module);
            entry.Status = status;
            entry.IsCritical = module.IsCritical;
            entry.Priority = module.Priority;
            entry.IsEnabled = module.IsEnabled;
            entry.FailReason = failReason;
            UpdateDiagnosticsNoLock(entry, module, now);

            if (resetEventCounters)
            {
                entry.SubscribeCount = 0;
                entry.UnsubscribeCount = 0;
            }

            if (markConfirmed)
            {
                entry.LastConfirmedUtc = now;
            }

            if (status == ModuleStatus.Failed)
            {
                entry.FailureCount++;
            }

            RecordActivityNoLock(entry, activityLabel, now, countSuccessfulOperation);
            TouchNoLock(entry, now);
        }

        private void ApplyEventCounterNoLock(ModuleEntry entry, bool subscribed)
        {
            DateTime now = DateTime.UtcNow;
            if (subscribed)
            {
                entry.SubscribeCount++;
            }
            else
            {
                entry.UnsubscribeCount++;
            }

            RecordActivityNoLock(entry, subscribed ? "Subscribe" : "Unsubscribe", now, countSuccessfulOperation: false);
            TouchNoLock(entry, now);
        }

        private void RegisterAliasNoLock(string? alias, Type type)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                return;
            }

            string normalized = alias!.Trim();
            if (_aliases.TryGetValue(normalized, out Type? existingType))
            {
                if (existingType != type)
                {
                    SafeLog($"[ModuleRegistry] Alias collision ignored: '{normalized}' already maps to {existingType.Name}");
                }

                return;
            }

            _aliases[normalized] = type;
        }

        private ModuleEntry? ResolveEventOwnerEntryNoLock(Delegate handler)
        {
            object? target = handler.Target;
            if (target is IMilitiaModule targetModule)
            {
                return EnsureEntryForModuleNoLock(targetModule, logUnknown: false);
            }

            if (TryResolveEntryFromTypeNoLock(target?.GetType()) is ModuleEntry targetEntry)
            {
                return targetEntry;
            }

            if (TryResolveEntryFromTypeNoLock(handler.Method?.DeclaringType) is ModuleEntry declaringEntry)
            {
                return declaringEntry;
            }

            if (target == null)
            {
                return null;
            }

            foreach (FieldInfo field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object? value;
                try
                {
                    value = field.GetValue(target);
                }
                catch
                {
                    continue;
                }

                if (value is IMilitiaModule capturedModule)
                {
                    return EnsureEntryForModuleNoLock(capturedModule, logUnknown: false);
                }

                if (TryResolveEntryFromTypeNoLock(value?.GetType()) is ModuleEntry capturedEntry)
                {
                    return capturedEntry;
                }
            }

            return null;
        }

        private void UpdateDiagnosticsNoLock(ModuleEntry entry, IMilitiaModule module, DateTime now)
        {
            string diagnostics;
            string? issue;

            try
            {
                diagnostics = NormalizeDiagnostics(module.GetDiagnostics());
                issue = AnalyzeDiagnosticsIssue(diagnostics);
            }
            catch (Exception ex)
            {
                diagnostics = $"Diagnostics exception: {ex.GetType().Name}: {ex.Message}";
                issue = diagnostics;
            }

            entry.LastDiagnostics = TruncateText(diagnostics, 600);
            entry.DiagnosticIssue = issue == null ? null : TruncateText(issue, 600);
            entry.LastDiagnosticsUtc = now;
        }

        private static string NormalizeDiagnostics(string? diagnostics)
        {
            if (string.IsNullOrWhiteSpace(diagnostics))
            {
                return "Diagnostics returned empty output.";
            }

            return diagnostics!.Trim();
        }

        private static string? AnalyzeDiagnosticsIssue(string diagnostics)
        {
            string text = diagnostics.Trim();
            if (text.Length == 0)
            {
                return "Diagnostics returned empty output.";
            }

            string lower = $" {text.ToLowerInvariant()} ";
            foreach (string token in SuspiciousDiagnosticTokens)
            {
                if (lower.Contains(token))
                {
                    return text;
                }
            }

            return null;
        }

        private static string TruncateText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text;
            }

            return text.Substring(0, maxLength - 3) + "...";
        }

        private void RecordActivityNoLock(ModuleEntry entry, string activityLabel, DateTime now, bool countSuccessfulOperation)
        {
            entry.LastActivity = NormalizeActivityLabel(activityLabel);
            entry.LastActivityUtc = now;

            if (countSuccessfulOperation)
            {
                entry.SuccessfulOperations++;
                entry.LastHealthyUtc = now;
            }
        }

        private void ApplyFailureNoLock(ModuleEntry entry, string? reason, string activityLabel, DateTime now)
        {
            entry.Status = ModuleStatus.Failed;
            entry.FailReason = reason;
            entry.FailureCount++;
            RecordActivityNoLock(entry, activityLabel, now, countSuccessfulOperation: false);
        }

        private static string NormalizeActivityLabel(string? activityLabel)
        {
            return string.IsNullOrWhiteSpace(activityLabel) ? "Runtime" : activityLabel!.Trim();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool IsGameAvailableSafe()
        {
            try
            {
                return CompatibilityLayer.IsGameFullyInitialized();
            }
            catch
            {
                return false;
            }
        }

        private void RefreshTrackedDiagnostics(DateTime now)
        {
            try
            {
                if (!IsGameAvailableSafe())
                {
                    return;
                }
            }
            catch
            {
                // Standalone test ortamında veya DLL eksikliğinde diagnostiği atla.
                return;
            }

            IMilitiaModule[] liveModules = SnapshotTrackedModules();
            foreach (IMilitiaModule module in liveModules)
            {
                try
                {
                    lock (_syncRoot)
                    {
                        ModuleEntry entry = EnsureEntryNoLock(module.GetType(), moduleName: null, logUnknown: false);
                        if (!entry.ModuleNameResolved)
                        {
                            RefreshIdentityNoLock(entry, ResolveModuleNameCachedNoLock(entry, module));
                        }
                        TrackInstanceNoLock(module);
                        UpdateDiagnosticsNoLock(entry, module, now);
                    }
                }
                catch (Exception ex)
                {
                    string moduleLabel;
                    try
                    {
                        moduleLabel = module?.GetType().Name ?? nameof(IMilitiaModule);
                    }
                    catch
                    {
                        moduleLabel = nameof(IMilitiaModule);
                    }

                    SafeLog($"[ModuleRegistry] RefreshTrackedDiagnostics skipped {moduleLabel}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private IMilitiaModule[] SnapshotTrackedModules()
        {
            lock (_syncRoot)
            {
                var liveModules = new List<IMilitiaModule>(_instances.Count);
                var staleTypes = new List<Type>();

                foreach (KeyValuePair<Type, WeakReference<IMilitiaModule>> pair in _instances)
                {
                    if (pair.Value.TryGetTarget(out IMilitiaModule? module) && module != null)
                    {
                        liveModules.Add(module);
                    }
                    else
                    {
                        staleTypes.Add(pair.Key);
                    }
                }

                foreach (Type type in staleTypes)
                {
                    _ = _instances.Remove(type);
                }

                return liveModules
                    .GroupBy(module => module.GetType())
                    .Select(group => group.First())
                    .ToArray();
            }
        }

        private static bool IsDead(ModuleEntry entry, DateTime now, AuditOptions options)
        {
            if (entry.HasRuntimeActivity)
            {
                return false;
            }

            DateTime observedSince = entry.LastConfirmedUtc ?? entry.LastChangedUtc;
            return now - observedSince >= options.DeadAfter;
        }

        private static bool IsStale(ModuleEntry entry, DateTime now, AuditOptions options)
        {
            if (!entry.HasRuntimeActivity)
            {
                return false;
            }

            DateTime observedSince = entry.LastActivityUtc ?? entry.LastHealthyUtc ?? entry.LastChangedUtc;
            return now - observedSince >= options.StaleAfter;
        }

        private ModuleEntry? TryResolveEntryFromTypeNoLock(Type? ownerType)
        {
            foreach (Type candidate in EnumerateRelatedTypes(ownerType))
            {
                if (_entries.TryGetValue(candidate, out ModuleEntry? entry))
                {
                    return entry;
                }

                if (IsDiscoverableModuleType(candidate))
                {
                    return EnsureEntryNoLock(candidate, moduleName: null, logUnknown: false);
                }
            }

            return null;
        }

        private ModuleEntry EnsureEntryForModuleNoLock(IMilitiaModule module, bool logUnknown)
        {
            ModuleEntry entry = EnsureEntryNoLock(module.GetType(), moduleName: null, logUnknown: logUnknown);
            if (!entry.ModuleNameResolved)
            {
                RefreshIdentityNoLock(entry, ResolveModuleNameCachedNoLock(entry, module));
            }

            return entry;
        }

        private string ResolveModuleNameCachedNoLock(ModuleEntry entry, IMilitiaModule module)
        {
            if (entry.ModuleNameResolved)
            {
                return string.IsNullOrWhiteSpace(entry.ModuleName) ? entry.Name : entry.ModuleName;
            }

            string resolved = ResolveModuleName(module);
            entry.ModuleNameResolved = true;
            return string.IsNullOrWhiteSpace(resolved) ? entry.Name : resolved;
        }

        private static IEnumerable<Type> EnumerateRelatedTypes(Type? type)
        {
            if (type == null)
            {
                yield break;
            }

            var seen = new HashSet<Type>();
            var pending = new Stack<Type>();
            pending.Push(type);

            while (pending.Count > 0)
            {
                Type current = pending.Pop();
                if (!seen.Add(current))
                {
                    continue;
                }

                yield return current;

                if (current.BaseType != null)
                {
                    pending.Push(current.BaseType);
                }

                if (current.DeclaringType != null)
                {
                    pending.Push(current.DeclaringType);
                }
            }
        }

        private ModuleEntry[] SnapshotEntries()
        {
            lock (_syncRoot)
            {
                return _entries.Values
                    .OrderBy(e => e.Name, NameComparer)
                    .Select(e => e.Clone())
                    .ToArray();
            }
        }

        private ModuleEntry[] SnapshotByStatus(ModuleStatus status)
        {
            lock (_syncRoot)
            {
                return _entries.Values
                    .Where(e => e.Status == status)
                    .OrderBy(e => e.Name, NameComparer)
                    .Select(e => e.Clone())
                    .ToArray();
            }
        }

        private static void AppendSection(
            StringBuilder sb,
            string title,
            IEnumerable<ModuleEntry> entries,
            Func<ModuleEntry, string> formatter)
        {
            List<ModuleEntry> materialized = entries.ToList();
            if (materialized.Count == 0)
            {
                return;
            }

            _ = sb.AppendLine(title + ":");
            foreach (ModuleEntry entry in materialized)
            {
                _ = sb.AppendLine("  - " + formatter(entry));
            }
        }

        private static string FormatAge(DateTime value, DateTime now)
        {
            TimeSpan age = now >= value ? now - value : TimeSpan.Zero;
            if (age.TotalDays >= 1)
            {
                return $"{age.TotalDays:F1}d";
            }

            if (age.TotalHours >= 1)
            {
                return $"{age.TotalHours:F1}h";
            }

            if (age.TotalMinutes >= 1)
            {
                return $"{age.TotalMinutes:F1}m";
            }

            return $"{Math.Max(0d, age.TotalSeconds):F0}s";
        }

        private static string FormatUtc(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            return utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
        }

        private static void TouchNoLock(ModuleEntry entry)
        {
            TouchNoLock(entry, DateTime.UtcNow);
        }

        private static void TouchNoLock(ModuleEntry entry, DateTime now)
        {
            entry.LastChanged = now;
        }

        private static void SafeLog(string message)
        {
            try
            {
                FileLogger.Log(message);
            }
            catch
            {
            }
        }
    }

    public sealed class AuditResult
    {
        public DateTime GeneratedAtUtc { get; set; }
        public int TotalDiscovered { get; set; }
        public int TotalRegistered { get; set; }
        public List<ModuleEntry> Healthy = new List<ModuleEntry>();
        public List<ModuleEntry> Disabled = new List<ModuleEntry>();
        public List<ModuleEntry> Unregistered = new List<ModuleEntry>();
        public List<ModuleEntry> Failed = new List<ModuleEntry>();
        public List<ModuleEntry> SilentBroken = new List<ModuleEntry>();
        public List<ModuleEntry> Stale = new List<ModuleEntry>();
        public List<ModuleEntry> Dead = new List<ModuleEntry>();
        public List<ModuleEntry> EventLeaks = new List<ModuleEntry>();
        public List<ModuleEntry> Removed = new List<ModuleEntry>();

        public bool HasProblems =>
            Unregistered.Count > 0
            || Failed.Count > 0
            || SilentBroken.Count > 0
            || Stale.Count > 0
            || Dead.Count > 0
            || EventLeaks.Count > 0;

        public override string ToString()
        {
            return $"Discovered={TotalDiscovered} Registered={TotalRegistered} Ghost={Unregistered.Count} Failed={Failed.Count} Silent={SilentBroken.Count} Stale={Stale.Count} Dead={Dead.Count}";
        }
    }
}

namespace BanditMilitias.Core.Registry
{
    public static class RegistryCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("registry", "militia")]
        public static string CommandRegistry(List<string> args)
        {
            return ModuleRegistry.Instance.GenerateReport();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("registry_ghosts", "militia")]
        public static string CommandGhosts(List<string> args)
        {
            List<ModuleEntry> ghosts = ModuleRegistry.Instance.Unregistered.ToList();
            if (ghosts.Count == 0)
            {
                return "No ghost modules. All modules are registered.";
            }

            return "Ghost modules:\n" + string.Join("\n", ghosts.Select(e => $"  - {e.DisplayName}"));
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("registry_failed", "militia")]
        public static string CommandFailed(List<string> args)
        {
            List<ModuleEntry> failed = ModuleRegistry.Instance.Failed.ToList();
            if (failed.Count == 0)
            {
                return "No failed modules.";
            }

            return "Failed modules:\n"
                 + string.Join("\n", failed.Select(e => $"  - {e.DisplayName}: {e.FailReason ?? "n/a"}"));
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("registry_silent", "militia")]
        public static string CommandSilent(List<string> args)
        {
            List<ModuleEntry> silent = ModuleRegistry.Instance.Audit().SilentBroken.ToList();
            if (silent.Count == 0)
            {
                return "No silent broken modules.";
            }

            return "Silent broken modules:\n"
                 + string.Join("\n", silent.Select(e => $"  - {e.DisplayName}: {e.DiagnosticIssue ?? e.LastDiagnostics ?? "n/a"}"));
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("registry_stale", "militia")]
        public static string CommandStale(List<string> args)
        {
            List<ModuleEntry> stale = ModuleRegistry.Instance.Audit().Stale.ToList();
            if (stale.Count == 0)
            {
                return "No stale modules.";
            }

            return "Stale modules:\n"
                 + string.Join("\n", stale.Select(e => $"  - {e.DisplayName}: {e.LastActivity ?? "unknown"} @ {e.LastActivityUtc?.ToString("u") ?? "n/a"}"));
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("registry_dead", "militia")]
        public static string CommandDead(List<string> args)
        {
            List<ModuleEntry> dead = ModuleRegistry.Instance.Audit().Dead.ToList();
            if (dead.Count == 0)
            {
                return "No dead modules.";
            }

            return "Dead modules:\n"
                 + string.Join("\n", dead.Select(e => $"  - {e.DisplayName}: confirmed {e.LastConfirmedUtc?.ToString("u") ?? "n/a"}"));
        }
    }
}
