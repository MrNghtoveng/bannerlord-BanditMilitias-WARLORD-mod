using BanditMilitias.Core.Components;
using BanditMilitias.Debug;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Fear;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Systems.Spawning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace BanditMilitias.Infrastructure
{
    // ── ModuleAccess ─────────────────────────────────────────

    public static class ModuleAccess
    {
        public static bool IsEnabled<T>() where T : class, IMilitiaModule
        {
            return TryGetEnabled<T>(out _);
        }

        public static T? GetEnabled<T>() where T : class, IMilitiaModule
        {
            return TryGetEnabled<T>(out T module) ? module : null;
        }

        public static bool TryGetEnabled<T>(out T module) where T : class, IMilitiaModule
        {
            T? candidate = ModuleManager.Instance.GetModule<T>();
            if (candidate == null || !candidate.IsEnabled)
            {
                module = null!;
                return false;
            }

            module = candidate;
            return true;
        }
    }

    // ── ReleaseGate ─────────────────────────────────────────

    public static class ReleaseGate
    {
        private static readonly Type[] RequiredModules =
        {
            typeof(MilitiaSpawningSystem),
            typeof(WarlordSystem),
            typeof(BanditBrain),
            typeof(FearSystem),
            typeof(WarlordLegitimacySystem)
        };

        public static bool Validate(ModuleManager manager, out string message)
        {
            var missing = new List<string>();

            for (int i = 0; i < RequiredModules.Length; i++)
            {
                var type = RequiredModules[i];
                if (manager.GetModule(type) == null)
                {
                    missing.Add(type.Name);
                }
            }

            if (missing.Count == 0)
            {
                message = "ReleaseGate: OK";
                return true;
            }

            message = $"ReleaseGate missing modules: {string.Join(", ", missing)}";
            return false;
        }
    }

    // ── ModuleValidator ─────────────────────────────────────────

    public static class ModuleValidator
    {
        public static void ValidateRegistrations()
        {
            try
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();

                var assembly = Assembly.GetExecutingAssembly();
                var moduleTypes = assembly.GetTypes()
                    .Where(t => typeof(IMilitiaModule).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                    .ToList();

                var mm = ModuleManager.Instance;
                var registeredTypes = new HashSet<Type>();
                foreach (var type in moduleTypes)
                {
                    if (mm.GetModule(type) != null)
                    {
                        _ = registeredTypes.Add(type);
                    }
                }

                int missingCount = 0;
                foreach (var type in moduleTypes)
                {
                    if (!registeredTypes.Contains(type))
                    {
                        string warnMsg = $"[Validator] UNCONNECTED MODULE DETECTED: {type.Name} is not registered in ModuleManager!";
                        DebugLogger.Warning("ModuleValidator", warnMsg);

                        if (Settings.Instance?.TestingMode == true)
                        {
                            InformationManager.DisplayMessage(new InformationMessage(warnMsg, Colors.Red));
                        }

                        missingCount++;
                    }
                }

                timer.Stop();

                if (missingCount == 0)
                {
                    if (Settings.Instance?.TestingMode == true)
                    {
                        DebugLogger.Info("ModuleValidator",
                            $"Registration validation passed. All {moduleTypes.Count} modules are connected. " +
                            $"({timer.Elapsed.TotalMilliseconds:F2}ms)");
                    }
                }
                else
                {
                    DebugLogger.Error("ModuleValidator",
                        $"CRITICAL: {missingCount} modules are missing registration!");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error("ModuleValidator", $"Validation failed: {ex.Message}");
            }
        }
    }

    // ── MbEventExtensions ─────────────────────────────────────────
    public static class MbEventExtensions
    {
        public static void RemoveListenerSafe(object ev, object owner, Delegate listener)
        {
            TryInvokeRemove(ev, owner, listener);
        }

        public static void RemoveNonSerializedListener(this object ev, object owner, Action listener)
        {
            TryInvokeRemove(ev, owner, listener);
        }

        public static void RemoveNonSerializedListener<T>(this object ev, object owner, Action<T> listener)
        {
            TryInvokeRemove(ev, owner, listener);
        }

        public static void RemoveNonSerializedListener<T1, T2>(this object ev, object owner, Action<T1, T2> listener)
        {
            TryInvokeRemove(ev, owner, listener);
        }

        public static void RemoveNonSerializedListener<T1, T2, T3>(this object ev, object owner, Action<T1, T2, T3> listener)
        {
            TryInvokeRemove(ev, owner, listener);
        }

        public static void RemoveNonSerializedListener<T1, T2, T3, T4>(this object ev, object owner, Action<T1, T2, T3, T4> listener)
        {
            TryInvokeRemove(ev, owner, listener);
        }

        public static void RemoveNonSerializedListener<T1, T2, T3, T4, T5, T6>(
            this object ev,
            object owner,
            Action<T1, T2, T3, T4, T5, T6> listener)
        {
            TryInvokeRemove(ev, owner, listener);
        }

        private static void TryInvokeRemove(object ev, object owner, Delegate listener)
        {
            if (ev == null || owner == null || listener == null) return;

            try
            {
                var type = ev.GetType();
                var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                var method = methods.FirstOrDefault(m => m.Name == "RemoveNonSerializedListener" && ParamsMatch(m, owner, listener))
                             ?? methods.FirstOrDefault(m => m.Name == "RemoveListener" && ParamsMatch(m, owner, listener));

                _ = (method?.Invoke(ev, new object[] { owner, listener }));
            }
            catch
            {
                // Swallow exceptions: on older APIs removal may not be supported.
            }
        }

        private static bool ParamsMatch(MethodInfo method, object owner, Delegate listener)
        {
            var ps = method.GetParameters();
            if (ps.Length != 2) return false;

            var ownerType = owner.GetType();
            var listenerType = listener.GetType();

            bool ownerOk = ps[0].ParameterType == typeof(object)
                           || ps[0].ParameterType.IsAssignableFrom(ownerType);
            bool listenerOk = ps[1].ParameterType.IsAssignableFrom(listenerType);

            return ownerOk && listenerOk;
        }
    }

    // ── TelemetryBridge ─────────────────────────────────────────
    /// <summary>
    /// Oyun eventi log'larını CSV formatında diske yazar.
    /// FileLogger'dan farklı olarak yapılandırılmış veri kaydeder.
    /// EnableFileLogging=true ve TestingMode=true iken aktif.
    /// </summary>
    public static class TelemetryBridge
    {
        private static string? _path;
        private static readonly object _lock = new();
        private static readonly Queue<string> _buffer = new();
        private static int _flushCount;
        private const int FLUSH_EVERY = 20;

        public static void Init()
        {
            if (Settings.Instance?.EnableFileLogging != true) return;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord", "Warlord_Logs", "BanditMilitias", "Telemetry");
                _ = Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "BanditMilitias_events.csv");
                if (!File.Exists(_path) || new FileInfo(_path).Length == 0)
                {
                    File.WriteAllText(_path, "Day,Event,Data" + Environment.NewLine);
                }
            }
            catch { _path = null; }
        }

        /// <summary>
        /// Bir olayı logla: "RaidResult", "WarlordDeath", "DuelOutcome" vb.
        /// data = "key=value;key=value" formatında.
        /// </summary>
        public static void Log(string eventType, string data)
        {
            if (_path == null) return;
            int day = Campaign.Current != null ? (int)CampaignTime.Now.ToDays : -1;
            lock (_lock) _buffer.Enqueue(SafeTelemetry.CsvRow(day, eventType, data));

            _flushCount++;
            if (_flushCount >= FLUSH_EVERY) Flush();
        }

        public static void LogEvent(string eventType, object payload)
        {
            Log(eventType, SafeTelemetry.ToJson(payload));
        }

        public static void LogEvent(string eventType, string data)
        {
            Log(eventType, data);
        }

        public static void LogBattle(string battleId, float x, float y, float str1, float str2, int cas1, int cas2, int winner, string battleType = "")
        {
            // Placeholder for telemetry
        }

        public static void Flush()
        {
            if (_path == null) return;
            string[] lines;
            lock (_lock)
            {
                lines = _buffer.ToArray();
                _buffer.Clear();
                _flushCount = 0;
            }
            if (lines.Length == 0) return;
            try { File.AppendAllLines(_path, lines); }
            catch { }
        }

        public static void Reset()
        {
            lock (_lock) { _buffer.Clear(); _flushCount = 0; _path = null; }
        }
    }

}
