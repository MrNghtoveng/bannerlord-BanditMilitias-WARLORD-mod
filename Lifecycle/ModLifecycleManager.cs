using BanditMilitias.Boot;
using BanditMilitias.Debug;
using BanditMilitias.Diagnostics;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Neural;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BanditMilitias.Lifecycle
{
    public interface ISystemInitiable
    {
        void InitializeSystem();
        void CleanupSystem();
    }

    public sealed class ModLifecycleManager
    {
        private const int MaxInitAttempts = 3;
        private const int MaxDeferredInitFailures = 3;

        private readonly ModStateController _stateController = new();
        private readonly BootWatchdog _bootWatchdog = new();
        private readonly HarmonyBootstrapper _harmonyBootstrapper = new("mod.bandit.militias.warlord");
        private readonly ModuleRegistrar _moduleRegistrar = new();
        private readonly SystemInitCoordinator _systemInitCoordinator;
        private readonly Stopwatch _initTimer = new();
        
        private readonly List<ISystemInitiable> _systems = new();

        private int _initializationAttempts;
        private int _deferredInitFailureCount;
        private double _totalInitTime;
        private readonly object _deferredInitLock = new();
        private volatile bool _isLoadInProgress;
        private volatile bool _isGameStartInProgress;
        private volatile bool _isGameEndInProgress;
        private volatile bool _isLoadedSaveSession;

        // ── Degraded Self-Recovery ─────────────────────────────────────────────
        private string _degradedReason = string.Empty;
        private int _degradedRecoveryAttempts;
        private DateTime _lastDegradedRecoveryTime = DateTime.MinValue;
        private const int MaxDegradedRecoveryAttempts = 3;
        private const double DegradedRecoveryThrottleSeconds = 30.0;

        private ModLifecycleManager()
        {
            _systemInitCoordinator = new SystemInitCoordinator(_bootWatchdog);
            _systems.Add(new EventBusSystemWrapper());
            _systems.Add(new SurrenderFixSystemWrapper());
            _systems.Add(new CompatibilitySystemWrapper());
        }

        private class EventBusSystemWrapper : ISystemInitiable
        {
            public void InitializeSystem()
            {
                Core.Events.EventBus.Instance.ResetForSessionEnd();
                Core.Events.EventBus.Instance.CaptureMainThread();
                // Governor başlat — tüm oturum boyunca aktif kalır.
                Core.Events.EventBus.Instance.SetGovernor(new NeuralBusGovernor());
            }
            public void CleanupSystem()
            {
                // Governor'ı null'a çek — kirli state bir sonraki oturuma taşınmasın.
                Core.Events.EventBus.Instance.SetGovernor(null);
                Core.Events.EventBus.Instance.ResetForSessionEnd();
            }
        }

        private class SurrenderFixSystemWrapper : ISystemInitiable
        {
            public void InitializeSystem() => Patches.SurrenderFix.SurrenderCrashPatch.Initialize();
            public void CleanupSystem() {}
        }

        private class CompatibilitySystemWrapper : ISystemInitiable
        {
            public void InitializeSystem() => CompatibilityLayer.Reset();
            public void CleanupSystem() {}
        }

        public static ModLifecycleManager Instance { get; } = new ModLifecycleManager();

        public bool EventBusEnabled { get; private set; } = true;
        public bool AiSystemEnabled { get; private set; } = true;
        public bool WarlordSystemEnabled { get; private set; } = true;
        public bool BrainSystemEnabled { get; private set; } = true;
        public bool DeferredInitDone { get; private set; }
        public bool IsSandboxMode { get; private set; }
        public bool IsLoadedSaveSession => _isLoadedSaveSession;
        public ModState CurrentState => _stateController.CurrentState;

        public void SetStateDormant() => TransitionToState(ModState.Dormant, "API SetStateDormant");
        public void SetStateActive() => TransitionToState(ModState.Active, "API SetStateActive");

        public void OnSubModuleLoad(Assembly assembly)
        {
            if (_isLoadInProgress)
            {
                FileLogger.LogWarning("OnSubModuleLoad: re-entrant call blocked");
                return;
            }
            _isLoadInProgress = true;

            FileLogger.LogSection("SubModuleLoad");
            FileLogger.Log("OnSubModuleLoad: start");

            if (!_stateController.TryTransition(ModState.Loading, "OnSubModuleLoad"))
            {
                _isLoadInProgress = false;
                return;
            }

            _initTimer.Restart();

            try
            {
                Core.Registry.ModuleRegistry.Instance.Discover(assembly);
                FileLogger.Log("ModuleRegistry.Discover: done");

                foreach (var sys in _systems)
                {
                    try
                    {
                        sys.InitializeSystem();
                    }
                    catch (Exception ex)
                    {
                        FileLogger.LogWarning($"System init skipped: {ex.Message}");
                    }
                }
                FileLogger.Log("ISystemInitiable systems: initialized");
                
                EventBusEnabled = true;

                _moduleRegistrar.RegisterAll();
                FileLogger.Log("RegisterModules: done");

                bool harmonyOk = _harmonyBootstrapper.Apply(assembly);
                FileLogger.Log($"ApplyHarmonyPatches: {(harmonyOk ? "OK" : "FAILED")}");
                
                ValidateSettings();
                FileLogger.Log("ValidateSettings: done");

                if (harmonyOk)
                {
                    TransitionToState(ModState.Ready, "Submodule boot ready");
                }
                else
                {
                    TransitionToState(ModState.Degraded, "Harmony unavailable");
                }

                _initTimer.Stop();
                _totalInitTime = _initTimer.Elapsed.TotalMilliseconds;
                FileLogger.Log($"OnSubModuleLoad: done in {_totalInitTime:F2}ms");

                if (Settings.Instance?.TestingMode == true)
                {
                    DisplayInfo($"[BanditMilitias] Module loaded in {_totalInitTime:F2}ms", Colors.Green);
                }
            }
            catch (Exception ex)
            {
                HandleCriticalError("OnSubModuleLoad", ex);
                TransitionToState(ModState.Failed, "OnSubModuleLoad exception");
            }
            finally
            {
                _isLoadInProgress = false;
            }
        }

        public void OnGameStart(Game game, IGameStarter gameStarter)
        {
            if (_isGameStartInProgress)
            {
                FileLogger.LogWarning("OnGameStart: re-entrant call blocked");
                return;
            }
            _isGameStartInProgress = true;

            FileLogger.LogSection("OnGameStart");
            FileLogger.Log($"OnGameStart: game={GetGameModeName(game)} starter={gameStarter?.GetType().Name ?? "null"}");

            if (!IsCampaignCompatibleMode(game, gameStarter))
            {
                if (Settings.Instance?.TestingMode == true)
                {
                    DisplayInfo($"[BanditMilitias] '{GetGameModeName(game)}' modu desteklenmiyor - mod devre disi.");
                }
                _isGameStartInProgress = false;
                return;
            }

            IsSandboxMode = game.GameType != null && game.GameType.GetType().Name.IndexOf("SandBox", StringComparison.OrdinalIgnoreCase) >= 0;
            _isLoadedSaveSession = false;


            if (Settings.Instance?.TestingMode == true)
            {
                DisplayInfo($"[BanditMilitias] Baslatiliyor: {GetGameModeName(game)}", Colors.Cyan);
            }

            if (CurrentState == ModState.Failed)
            {
                DisplayError("Mod initialization failed - BanditMilitias is disabled");
                return;
            }

            _initializationAttempts++;
            if (_initializationAttempts > MaxInitAttempts)
            {
                DisplayError("Max initialization attempts exceeded - mod disabled");
                TransitionToState(ModState.Failed, "Max init attempts exceeded");
                return;
            }

            var timer = Stopwatch.StartNew();

            try
            {
                Settings.Instance?.ValidateAndClampSettings();
                ConfigureTestMode();

                FileLogger.Log("InitializeInfrastructure: begin");
                if (!_systemInitCoordinator.InitializeInfrastructure(out bool eventBusEnabled))
                {
                    throw new InvalidOperationException("Infrastructure initialization failed");
                }
                EventBusEnabled = eventBusEnabled;
                FileLogger.Log("InitializeInfrastructure: done");

                FileLogger.Log("ModuleManager.InitializeAll: begin");
                var bootWatch = Stopwatch.StartNew();
                ModuleManager.Instance.InitializeAll();
                bootWatch.Stop();
                _ = _bootWatchdog.WarnIfSlow("ModuleManager.InitializeAll", bootWatch);
                FileLogger.Log("ModuleManager.InitializeAll: done");

                try
                {
                    var registryAudit = Core.Registry.ModuleRegistry.Instance.Audit();
                    FileLogger.Log($"[ModuleRegistry] {registryAudit}");
                    if (registryAudit?.HasProblems == true)
                    {
                        string report = Core.Registry.ModuleRegistry.Instance.GenerateReport();
                        FileLogger.Log(report);

                        if (registryAudit.Unregistered.Count > 0)
                        {
                            string ghostList = string.Join(", ", registryAudit.Unregistered.Select(e => e.Name));
                            InformationManager.DisplayMessage(
                                new InformationMessage(
                                    $"[BanditMilitias] {registryAudit.Unregistered.Count} unregistered modules: {ghostList}",
                                    Colors.Red));
                        }
                        else
                        {
                            InformationManager.DisplayMessage(
                                new InformationMessage(
                                    $"[BanditMilitias] Module registry reported problems. Failed={registryAudit.Failed.Count}, Silent={registryAudit.SilentBroken.Count}, Stale={registryAudit.Stale.Count}, Dead={registryAudit.Dead.Count}.",
                                    Colors.Yellow));
                        }
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.LogWarning($"[ModuleRegistry] Audit skipped: {ex.GetType().Name}: {ex.Message}");
                    DebugLogger.Warning("SubModule", $"ModuleRegistry audit skipped: {ex.Message}");
                }

                FileLogger.Log("Heavy system init DEFERRED to OnSessionLaunched");
                DeferredInitDone = false;

                if (gameStarter == null)
                {
                    throw new InvalidOperationException("GameStarter is null");
                }

                FileLogger.Log("RegisterGameModels: begin");
                _systemInitCoordinator.RegisterGameModels(gameStarter);
                FileLogger.Log("RegisterGameModels: done");

                FileLogger.Log("RegisterCampaignBehaviors: begin");
                if (!_systemInitCoordinator.RegisterCampaignBehaviors(gameStarter))
                {
                    DebugLogger.Error("SubModule",
                        $"gameStarter '{gameStarter.GetType().Name}' does not support AddBehavior. Behaviors could not be registered.");
                    TransitionToState(ModState.Degraded, "Campaign behavior registration failed");
                    DisplayWarning("[BanditMilitias] Campaign behaviors could not be registered. Some features may not work.");
                }

                FileLogger.Log("RegisterCampaignBehaviors: done");

                TransitionToState(ModState.Dormant, "Waiting for activation delay");
                FileLogger.Log("OnGameStart: state dormant (waiting for activation)");

                _initializationAttempts = 0;
                timer.Stop();
                _totalInitTime += timer.Elapsed.TotalMilliseconds;
                FileLogger.Log($"OnGameStart: done in {timer.Elapsed.TotalMilliseconds:F2}ms (heavy init deferred)");

                if (Settings.Instance?.TestingMode == true)
                {
                    DisplayInfo($"[BanditMilitias] Core loading complete ({timer.Elapsed.TotalMilliseconds:F2}ms) - systems will start when map loads", Colors.Cyan);
                }
            }
            catch (Exception ex)
            {
                HandleCriticalError("OnGameStart", ex);
                if (_initializationAttempts < MaxInitAttempts)
                {
                    TransitionToState(ModState.Degraded, "OnGameStart degraded recovery");
                    DisplayWarning("Mod running in degraded mode - some features disabled");
                }
                else
                {
                    TransitionToState(ModState.Failed, "OnGameStart failed too many times");
                }
            }
            finally
            {
                _isGameStartInProgress = false;
            }
        }

        public void OnGameLoaded(Game game)
        {
            if (!IsCampaignCompatibleMode(game))
            {
                return;
            }

            _isLoadedSaveSession = true;

            try
            {
                Patches.SurrenderFix.SurrenderCrashPatch.ResetState();
                Systems.Combat.MilitiaVictorySystem.Reset();

                if (AiSystemEnabled)
                {
                    Intelligence.AI.CustomMilitiaAI.Initialize();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("SubModule", $"OnGameLoaded maintenance failed: {ex.Message}");
            }
        }

        public void OnGameEnd()
        {
            if (_isGameEndInProgress)
            {
                FileLogger.LogWarning("OnGameEnd: re-entrant call blocked");
                return;
            }
            _isGameEndInProgress = true;

            FileLogger.LogSection("OnGameEnd");

            try
            {
                SafeReset("CustomMilitiaAI.Cleanup", () => Intelligence.AI.CustomMilitiaAI.Cleanup());
                SafeReset("NeuralEventRouter.Reset", () => Core.Neural.NeuralEventRouter.Instance.Reset());
                SafeReset("MilitiaNameGenerator.ClearCache", () => Systems.Spawning.MilitiaNameGenerator.ClearCache());
                SafeReset("CampaignGridSystem.ResetCache", () => Systems.Grid.CampaignGridSystem.ResetCache());
                SafeReset("Globals.Reset", () => Core.Config.Globals.Reset());
                SafeReset("CompatibilityLayer.Reset", () => CompatibilityLayer.Reset());
                SafeReset("MilitiaVictorySystem.Reset", () => Systems.Combat.MilitiaVictorySystem.Reset());
                SafeReset("DebugPanel.Reset", () => Debug.DebugPanel.Reset());
                SafeReset("DiagnosticsSystem.OnSessionEnd", () => Systems.Diagnostics.DiagnosticsSystem.OnSessionEnd());
                SafeReset("ExceptionMonitor.Reset", () => ExceptionMonitor.Reset());

                foreach (var sys in _systems)
                {
                    try
                    {
                        sys.CleanupSystem();
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Warning("SubModule", $"System cleanup failed: {ex.Message}");
                    }
                }
                FileLogger.Log("ISystemInitiable systems: cleanup done");

                SafeReset("ModuleRegistry.ResetForSessionEnd", () => Core.Registry.ModuleRegistry.Instance.ResetForSessionEnd());

                try
                {
                    ModuleManager.Instance.ResetForSessionEnd();
                    FileLogger.Log("ModuleManager.OnSessionEnd: done");
                }
                catch (Exception ex) { DebugLogger.Warning("SubModule", $"ModuleManager cleanup failed: {ex.Message}"); }

                DeferredInitDone = false;
                AiSystemEnabled = true;
                WarlordSystemEnabled = true;
                BrainSystemEnabled = true;
                _isLoadedSaveSession = false;
                // Reset attempt counter so consecutive failed sessions don't permanently disable the mod.
                // (Counter only blocks init when it exceeds MaxInitAttempts *within a single boot*.)
                _initializationAttempts = 0;
                TransitionToState(ModState.Ready, "OnGameEnd session reset");
                FileLogger.Log("OnGameEnd: state reset to Ready");
            }
            finally
            {
                _isGameEndInProgress = false;
            }
        }

        public void OnSubModuleUnloaded()
        {
            FileLogger.LogSection("OnSubModuleUnloaded");

            try
            {
                _harmonyBootstrapper.Unapply();
            }
            catch (Exception ex)
            {
                FileLogger.LogError($"Harmony unpatching failed: {ex.Message}");
            }

            try
            {
                Core.Events.EventBus.Instance.ResetForModuleUnload();
            }
            catch (Exception ex)
            {
                FileLogger.LogWarning($"EventBus clear failed: {ex.Message}");
            }

            try
            {
                Core.Registry.ModuleRegistry.Instance.ResetForModuleUnload();
            }
            catch (Exception ex)
            {
                FileLogger.LogWarning($"ModuleRegistry reset failed: {ex.Message}");
            }

            try
            {
                ModuleManager.Instance.ResetForModuleUnload();
            }
            catch (Exception ex)
            {
                FileLogger.LogWarning($"ModuleManager reset failed: {ex.Message}");
            }

            try
            {
                CompatibilityLayer.Reset();
            }
            catch (Exception ex)
            {
                FileLogger.LogWarning($"CompatibilityLayer reset failed: {ex.Message}");
            }

            _stateController.ForceState(ModState.Uninitialized, "Submodule unloaded");
            DeferredInitDone = false;
            EventBusEnabled = true;
            AiSystemEnabled = true;
            WarlordSystemEnabled = true;
            BrainSystemEnabled = true;
            _isLoadedSaveSession = false;
            _initializationAttempts = 0;
            _deferredInitFailureCount = 0;
            _degradedReason = string.Empty;
            _degradedRecoveryAttempts = 0;
            _lastDegradedRecoveryTime = DateTime.MinValue;
            FileLogger.Log("OnSubModuleUnloaded: complete");
        }

        public bool RunDeferredSystemInit()
        {
            lock (_deferredInitLock)
            {
                if (DeferredInitDone)
                {
                    return true;
                }

                // Ensure ModuleManager is initialized before deferred init runs.
                // OnGameStart may have already called this, but guard in case it was skipped.
                if (!ModuleManager.Instance.IsSessionBootstrapComplete)
                {
                    try
                    {
                        ModuleManager.Instance.InitializeAll();
                    }
                    catch (Exception ex)
                    {
                        FileLogger.LogWarning($"RunDeferredSystemInit: ModuleManager pre-init failed: {ex.Message}");
                    }
                }

                bool aiEnabled = AiSystemEnabled;
                bool warlordEnabled = WarlordSystemEnabled;
                bool brainEnabled = BrainSystemEnabled;
                SystemInitResult result = _systemInitCoordinator.RunDeferredSystemInit(DisplayInfo, DisplaySystemStatus, ref aiEnabled, ref warlordEnabled, ref brainEnabled);
                AiSystemEnabled = aiEnabled;
                WarlordSystemEnabled = warlordEnabled;
                BrainSystemEnabled = brainEnabled;

                if (result == SystemInitResult.Success)
                {
                    DeferredInitDone = true;
                    _deferredInitFailureCount = 0;
                    return true;
                }

                if (result == SystemInitResult.Fatal)
                {
                    EnterHardStop("Critical system initialization failed. Check logs for details.");
                    return false;
                }

                _deferredInitFailureCount++;
                if (_deferredInitFailureCount >= MaxDeferredInitFailures)
                {
                    EnterHardStop($"Deferred system init failed {_deferredInitFailureCount} times.");
                }
                return false;
            }
        }

        public string GetDiagnostics()
        {
            return $"SubModule State: {CurrentState} | DeferredInit={DeferredInitDone} | " +
                   $"AI={AiSystemEnabled} | EventBus={EventBusEnabled} | Warlord={WarlordSystemEnabled} | Brain={BrainSystemEnabled}";
        }

        private void TransitionToState(ModState newState, string reason)
        {
            // Track the reason whenever entering Degraded so recovery knows what to fix
            if (newState == ModState.Degraded)
            {
                _degradedReason = reason;
                _degradedRecoveryAttempts = 0;
                _lastDegradedRecoveryTime = DateTime.MinValue;
                FileLogger.Log($"[DegradedTracker] Reason recorded: '{reason}'");
            }

            if (_stateController.TryTransition(newState, reason))
            {
                Core.Events.EventBus.Instance.SetLifecycleState(newState);
            }
        }

        /// <summary>
        /// Attempts to heal the mod out of Degraded mode automatically.
        /// Called from the tick loop at a throttled interval.
        /// Returns true if recovery succeeded (state changed to Dormant/Active).
        /// </summary>
        public bool TryRecoverFromDegraded()
        {
            if (CurrentState != ModState.Degraded) return true;

            if (_degradedRecoveryAttempts >= MaxDegradedRecoveryAttempts)
            {
                // Exhausted all recovery attempts — escalate to Failed
                if (CurrentState == ModState.Degraded)
                {
                    FileLogger.LogError($"[DegradedRecovery] Max recovery attempts ({MaxDegradedRecoveryAttempts}) exhausted. Escalating to Failed.");
                    TransitionToState(ModState.Failed, $"Degraded recovery exhausted: {_degradedReason}");
                    DisplayError("[BanditMilitias] Mod could not recover from degraded state — some features are permanently disabled this session.");
                }
                return false;
            }

            // Throttle: don't hammer the recovery every tick
            var now = DateTime.Now;
            if ((now - _lastDegradedRecoveryTime).TotalSeconds < DegradedRecoveryThrottleSeconds)
                return false;

            _lastDegradedRecoveryTime = now;
            _degradedRecoveryAttempts++;
            FileLogger.Log($"[DegradedRecovery] Attempt {_degradedRecoveryAttempts}/{MaxDegradedRecoveryAttempts} — Reason: '{_degradedReason}'");

            try
            {
                // ── Recovery path A: Harmony was the culprit ────────────────────
                bool harmonyRelated = _degradedReason.IndexOf("Harmony", StringComparison.OrdinalIgnoreCase) >= 0
                                   || _degradedReason.IndexOf("patch", StringComparison.OrdinalIgnoreCase) >= 0;

                if (harmonyRelated && !_harmonyBootstrapper.IsPatched)
                {
                    FileLogger.Log("[DegradedRecovery] Re-attempting Harmony patch application...");
                    bool harmonyOk = _harmonyBootstrapper.Apply(typeof(SubModule).Assembly);

                    if (harmonyOk)
                    {
                        FileLogger.Log("[DegradedRecovery] Harmony re-applied — transitioning to Dormant.");
                        DisplayWarning("[BanditMilitias] Recovery: Harmony patches re-applied successfully.");
                        TransitionToState(ModState.Dormant, "Degraded recovery: Harmony reapplied");
                        _degradedReason = string.Empty;
                        return true;
                    }

                    FileLogger.LogWarning($"[DegradedRecovery] Harmony re-apply failed (attempt {_degradedRecoveryAttempts})");
                    return false;
                }

                // ── Recovery path B: Campaign behavior registration issue ────────
                bool behaviorRelated = _degradedReason.IndexOf("behavior", StringComparison.OrdinalIgnoreCase) >= 0
                                    || _degradedReason.IndexOf("campaign", StringComparison.OrdinalIgnoreCase) >= 0
                                    || _degradedReason.IndexOf("registration", StringComparison.OrdinalIgnoreCase) >= 0;

                if (behaviorRelated)
                {
                    // Campaign behaviors cannot be re-registered after game start.
                    // Move to Dormant so the mod stays alive with reduced functionality.
                    FileLogger.Log("[DegradedRecovery] Behavior issue — moving to Dormant (limited mode, spawn system inactive).");
                    DisplayWarning("[BanditMilitias] Running in limited mode: spawn behaviors unavailable. Core features active.");
                    TransitionToState(ModState.Dormant, "Degraded recovery: limited mode — behaviors unavailable");
                    _degradedReason = string.Empty;
                    return true;
                }

                // ── Recovery path C: Generic — try to transition to Dormant ─────
                FileLogger.Log("[DegradedRecovery] Generic recovery — attempting Dormant transition.");
                if (_stateController.TryTransition(ModState.Dormant, "Degraded auto-recovery"))
                {
                    Core.Events.EventBus.Instance.SetLifecycleState(ModState.Dormant);
                    FileLogger.Log("[DegradedRecovery] Generic recovery succeeded — now Dormant.");
                    _degradedReason = string.Empty;
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogWarning($"[DegradedRecovery] Recovery attempt {_degradedRecoveryAttempts} threw: {ex.GetType().Name} — {ex.Message}");
            }

            return false;
        }

        private void HandleCriticalError(string context, Exception ex)
        {
            BootErrorSeverity severity = ErrorClassifier.Classify(context, ex);
            FileLogger.LogError($"[CRITICAL ERROR] {context}: {severity}: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
            DebugLogger.Error("SubModule", $"Critical Error in {context}: {ex.Message}");
        }

        private void EnterEmergencyStop(string reason, Exception? ex)
        {
            _stateController.ForceState(ModState.EmergencyStop, reason);
            Core.Events.EventBus.Instance.SetLifecycleState(ModState.EmergencyStop);
            FileLogger.LogError($"[EMERGENCY STOP] Reason: {reason}");
            if (ex != null)
            {
                FileLogger.LogError($"Exception: {ex}");
            }

            DisplayError($"[BanditMilitias] EMERGENCY STOP: {reason}. Mod disabled to prevent save corruption.");
        }

        private void EnterHardStop(string reason)
        {
            _stateController.ForceState(ModState.Failed, "HARD STOP: " + reason);
            Core.Events.EventBus.Instance.SetLifecycleState(ModState.Failed);
            
            FileLogger.LogError($"[HARD STOP] Reason: {reason}");

            // Show a blocking inquiry to the user
            InformationManager.ShowInquiry(new InquiryData(
                "Bandit Militias - CRITICAL ERROR",
                $"The mod encountered a critical initialization failure: {reason}\n\n" +
                "To prevent save corruption, the game session must be terminated. Please check your logs and report this issue.",
                true, false, "Exit to Main Menu", "", 
                () => {
                    MBGameManager.EndGame();
                }, null), true);
        }

        private void ValidateSettings()
        {
            try
            {
                if (Settings.Instance == null)
                {
                    FileLogger.LogWarning("Settings.Instance is null. Using defaults.");
                    return;
                }

                Settings.Instance.ValidateAndClampSettings();
            }
            catch (Exception ex)
            {
                FileLogger.LogError($"Settings validation failed: {ex.Message}");
            }
        }

        private void ConfigureTestMode()
        {
            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("SubModule", "TestingMode enabled - enhanced diagnostics active");
            }
        }

        private static bool IsCampaignCompatibleMode(Game game, IGameStarter? gameStarter = null)
        {
            if (game?.GameType == null)
            {
                return false;
            }

            if (gameStarter != null)
            {
                if (gameStarter is CampaignGameStarter)
                {
                    return true;
                }

                string starterTypeName = gameStarter.GetType().Name;
                return starterTypeName.Contains("Campaign") || starterTypeName.Contains("Sandbox");
            }

            if (Campaign.Current != null)
            {
                return true;
            }

            string typeName = game.GameType.GetType().Name;
            return typeName is "Campaign"
                or "SandBoxGameType"
                or "CampaignGameType"
                or "SandboxGameType";
        }

        private static string GetGameModeName(Game game)
        {
            if (game?.GameType == null)
            {
                return "Unknown";
            }

            string typeName = game.GameType.GetType().Name;
            return typeName switch
            {
                "Campaign" => "Story Mode",
                "SandBoxGameType" => "Sandbox Mode",
                "CustomGame" => "Multiplayer",
                "Tutorial" => "Tutorial",
                _ => typeName
            };
        }

        private static void SafeReset(string label, Action action)
        {
            try
            {
                action();
                FileLogger.Log($"{label}: done");
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("SubModule", $"{label} cleanup failed: {ex.Message}");
            }
        }

        private static void DisplayInfo(string message, Color? color = null)
        {
            UiNotifier.TryShow(message, color ?? Colors.White, "ModLifecycle");
        }

        private static void DisplayWarning(string message)
        {
            UiNotifier.TryShow(message, Colors.Yellow, "ModLifecycle");
        }

        private static void DisplayError(string message)
        {
            UiNotifier.TryShow(message, Colors.Red, "ModLifecycle");
        }

        private void DisplaySystemStatus()
        {
            string status = $"[BanditMilitias] Status: {CurrentState} | AI:{(AiSystemEnabled ? "ON" : "OFF")} | Warlord:{(WarlordSystemEnabled ? "ON" : "OFF")}";
            DisplayInfo(status, CurrentState == ModState.Active ? Colors.Green : Colors.Yellow);
        }
    }
}
