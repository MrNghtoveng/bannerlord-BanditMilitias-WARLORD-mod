using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Registry;
using BanditMilitias.Infrastructure;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Boot
{
    /// <summary>
    /// WARLORD: Universal Power Grid Controller
    /// TR: Modu deterministik kademelerle başlatan merkezi güç kontrolörü.
    /// </summary>
    public sealed class WarlordPowerController
    {
        private static readonly WarlordPowerController _instance = new();
        public static WarlordPowerController Instance => _instance;

        private readonly List<IMilitiaModule> _gridModules = new();
        private bool _isStaticWiringDone;
        private bool _isLoadConnected;
        private bool _isEnergized;

        private readonly Stopwatch _gridTimer = new();

        private WarlordPowerController() { }

        // --- STAGE 1: STATIC WIRING (SubModule Load) ---
        public bool PowerOn(Assembly modAssembly)
        {
            if (_isStaticWiringDone) return true;

            _gridTimer.Restart();
            FileLogger.LogSection("POWER GRID: STAGE 1 (STATIC WIRING)");

            try
            {
                // 1. Core System Wiring (Grounding)
                Core.Events.EventBus.Instance.ResetForSessionEnd();
                Core.Events.EventBus.Instance.CaptureMainThread();
                CompatibilityLayer.Reset();
                Patches.SurrenderFix.SurrenderCrashPatch.Initialize();

                // 2. Discover all modules and add to the deterministic bus
                var discovered = modAssembly.GetTypes()
                    .Where(t => typeof(IMilitiaModule).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                    .OrderByDescending(t => ModulePriorityResolver.Resolve(t, 50))
                    .ToList();

                foreach (var type in discovered)
                {
                    var attr = type.GetCustomAttribute<AutoRegisterAttribute>();
                    if (attr == null) continue;

                    var module = InstantiateModule(type, attr);
                    if (module != null)
                    {
                        _gridModules.Add(module);
                        FileLogger.Log($"[Grid] Bolted Module: {module.ModuleName} (P:{ModulePriorityResolver.Resolve(module)})");
                    }
                }

                _isStaticWiringDone = true;
                _gridTimer.Stop();
                FileLogger.Log($"[Grid] Stage 1 Complete: {_gridModules.Count} modules wired in {_gridTimer.ElapsedMilliseconds}ms");
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.LogError($"[Grid] FATAL: Stage 1 Short Circuit: {ex.Message}");
                return false;
            }
        }

        // --- STAGE 2: ENVIRONMENT GROUNDING (Game Start) ---
        public void ConnectLoad(Game game, IGameStarter gameStarter)
        {
            if (!_isStaticWiringDone || _isLoadConnected) return;

            FileLogger.LogSection("POWER GRID: STAGE 2 (ENVIRONMENT GROUNDING)");
            _gridTimer.Restart();

            try
            {
                // Ground the compatibility layers
                CompatibilityLayer.ForceInitializeAll();
                
                // Initialize all wired modules
                foreach (var module in _gridModules)
                {
                    try
                    {
                        module.Initialize();
                        module.RegisterCampaignEvents();
                        FileLogger.Log($"[Grid] Energized Component: {module.ModuleName}");
                    }
                    catch (Exception ex)
                    {
                        FileLogger.LogWarning($"[Grid] Component Leak: {module.ModuleName} failed to initialize: {ex.Message}");
                        if (module.IsCritical) throw new InvalidOperationException($"Critical component failed: {module.ModuleName}");
                    }
                }

                _isLoadConnected = true;
                _gridTimer.Stop();
                FileLogger.Log($"[Grid] Stage 2 Complete in {_gridTimer.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                FileLogger.LogError($"[Grid] FATAL: Stage 2 Grounding Failure: {ex.Message}");
                throw;
            }
        }

        // --- STAGE 3: FULL ENERGIZATION (Session Launch) ---
        public void Energize()
        {
            if (!_isLoadConnected || _isEnergized) return;

            FileLogger.LogSection("POWER GRID: STAGE 3 (FULL ENERGIZATION)");
            
            foreach (var module in _gridModules)
            {
                try
                {
                    module.OnSessionStart();
                }
                catch (Exception ex)
                {
                    FileLogger.LogWarning($"[Grid] Energy Spike: {module.ModuleName} session start failed: {ex.Message}");
                }
            }

            // Asset Integrity Check (The Final Load Test)
            try { AssetRegistry.PerformFullIntegrityCheck(); }
            catch (Exception ex) { FileLogger.LogWarning($"[Grid] Asset Check Arc: {ex.Message}"); }

            _isEnergized = true;
            FileLogger.Log("[Grid] System FULLY ENERGIZED. All engines nominal.");
        }

        public void Shutdown()
        {
            if (!_isStaticWiringDone) return;

            FileLogger.LogSection("POWER GRID: SHUTDOWN");
            foreach (var module in _gridModules.AsEnumerable().Reverse())
            {
                try { module.Cleanup(); }
                catch (Exception ex) { FileLogger.LogWarning($"[Grid] Cleanup Spike: {module.ModuleName}: {ex.Message}"); }
            }

            // Core System Cleanup
            Core.Events.EventBus.Instance.SetGovernor(null);
            Core.Events.EventBus.Instance.ResetForSessionEnd();
            CompatibilityLayer.Reset();

            _gridModules.Clear();
            _isStaticWiringDone = false;
            _isLoadConnected = false;
            _isEnergized = false;
            FileLogger.Log("[Grid] Power Grid Offline.");
        }

        private IMilitiaModule? InstantiateModule(Type type, AutoRegisterAttribute attr)
        {
            IMilitiaModule? instance = null;

            // 1. Try Singleton Instance if requested
            if (attr.IsSingleton)
            {
                try
                {
                    var prop = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (prop != null)
                    {
                        instance = prop.GetValue(null) as IMilitiaModule;
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Grid] Singleton access failed for {type.Name}: {ex.InnerException?.Message ?? ex.Message}. Falling back to Activator.");
                }
            }

            // 2. Fallback to Activator if singleton failed or was null
            if (instance == null)
            {
                try
                {
                    instance = Activator.CreateInstance(type, true) as IMilitiaModule;
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Grid] Activator failed for {type.Name}: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            if (instance == null)
            {
                FileLogger.Log($"[Grid] CRITICAL: Could not resolve instance for {type.Name}");
            }

            return instance;
        }

        public string GetGridDiagnostics()
        {
            return $"Grid Status: {(_isEnergized ? "Energized" : "Off")} | Load: {_gridModules.Count} Modules | Voltage: {(_isLoadConnected ? "Stable" : "Unstable")}";
        }

        public bool IsEnergized => _isEnergized;
    }
}
