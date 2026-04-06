using BanditMilitias.Debug;
using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BanditMilitias
{
    public class SubModule : MBSubModuleBase
    {
        public static readonly string ModuleId = "BanditMilitias";
        public static readonly Version ModVersion = new(5, 1, 0);

        private enum ModState
        {
            Uninitialized,
            Loading,
            Ready,
            Dormant,    // Haritaya girildi, aktivasyon bekleniyor
            Active,     // Tamamen aktif
            Degraded,
            Failed,
            EmergencyStop
        }

        private static ModState _currentState = ModState.Uninitialized;
        private static readonly object _stateLock = new();

        private const string HarmonyId = "mod.bandit.militias.warlord";
        private static Harmony? _harmony;
        private static bool _harmonyPatched = false;

        private static int _initializationAttempts = 0;
        private static int _tickErrorCount = 0;
        private static DateTime _lastTickError = DateTime.MinValue;
        private const int MAX_INIT_ATTEMPTS = 3;
        private const int MAX_TICK_ERRORS_PER_MINUTE = 10;
        private const int MAX_TICK_ERRORS_FOR_EMERGENCY = 25;
        private const int MAX_DEFERRED_INIT_FAILURES = 3;
        private static int _deferredInitFailureCount = 0;

        private static readonly System.Diagnostics.Stopwatch _initTimer = new();
        private static double _totalInitTime = 0.0;

        private static bool _eventBusEnabled = true;
        private static bool _aiSystemEnabled = true;
        private static bool _warlordSystemEnabled = true;
        private static bool _brainSystemEnabled = true;
        private static bool _deferredInitDone = false;

        // Public API: MilitiaBehavior'ın reflection yerine doğrudan kullanması için
        public static bool IsDeferredInitDone => _deferredInitDone;
        public static void SetStateDormant() => TransitionToState(ModState.Dormant);
        public static void SetStateActive() => TransitionToState(ModState.Active);

        public static bool IsSandboxMode { get; private set; } = false;

        private static bool IsCampaignCompatibleMode(Game game, IGameStarter? gameStarter = null)
        {
            if (game?.GameType == null)
            {
                return false;
            }

            if (gameStarter != null)
            {

                if (gameStarter is CampaignGameStarter) return true;

                string starterTypeName = gameStarter.GetType().Name;
                if (starterTypeName.Contains("Campaign") || starterTypeName.Contains("Sandbox"))
                    return true;

                return false;
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
            if (game?.GameType == null) return "Unknown";
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

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();

            Infrastructure.FileLogger.LogSection("SubModuleLoad");
            Infrastructure.FileLogger.Log("OnSubModuleLoad: start");

            lock (_stateLock)
            {
                if (_currentState != ModState.Uninitialized)
                {
                    return;
                }
                _currentState = ModState.Loading;
            }

            _initTimer.Start();

            try
            {
                BanditMilitias.Core.Registry.ModuleRegistry.Instance.Discover(typeof(SubModule).Assembly);
                Infrastructure.FileLogger.Log("ModuleRegistry.Discover: done");
                
                // MİMARİ DÜZELTME: Modülleri SyncData'dan önce kayıt etmeliyiz.
                Core.Events.EventBus.Instance.Clear();
                RegisterModules();
                Infrastructure.FileLogger.Log("RegisterModules: done");

                bool harmonyOk = ApplyHarmonyPatches();
                Infrastructure.FileLogger.Log($"ApplyHarmonyPatches: {(harmonyOk ? "OK" : "FAILED")}");
                if (!harmonyOk)
                {
                    TransitionToState(ModState.Degraded);
                }

                try
                {
                    Patches.SurrenderFix.SurrenderCrashPatch.Initialize();
                    Infrastructure.FileLogger.Log("SurrenderCrashPatch: initialized");
                }
                catch (Exception ex)
                {
                    Infrastructure.FileLogger.LogWarning($"SurrenderCrashPatch init skipped: {ex.Message}");
                    DebugLogger.Warning("SubModule", $"SurrenderCrashPatch init skipped: {ex.Message}");
                }

                ValidateSettings();
                Infrastructure.FileLogger.Log("ValidateSettings: done");

                if (harmonyOk) TransitionToState(ModState.Ready);
                else TransitionToState(ModState.Degraded);

                // FIX Bug 4: Ensure static state is clean for the first game session
                Infrastructure.CompatibilityLayer.ResetCampaignStartTimeCache();
                Infrastructure.CompatibilityLayer.ResetActivationDelayState();

                _initTimer.Stop();
                _totalInitTime = _initTimer.Elapsed.TotalMilliseconds;
                Infrastructure.FileLogger.Log($"OnSubModuleLoad: done in {_totalInitTime:F2}ms");

                if (Settings.Instance?.TestingMode == true)
                {
                    DisplayInfo($"[BanditMilitias] Module loaded in {_totalInitTime:F2}ms", Colors.Green);
                }
            }
            catch (Exception ex)
            {
                Infrastructure.FileLogger.LogError($"OnSubModuleLoad failed: {ex.Message}");
                HandleCriticalError("OnSubModuleLoad", ex);
                TransitionToState(ModState.Failed);
            }
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarter)
        {
            base.OnGameStart(game, gameStarter);

            Infrastructure.FileLogger.LogSection("OnGameStart");
            Infrastructure.FileLogger.Log($"OnGameStart: game={GetGameModeName(game)} starter={gameStarter?.GetType().Name ?? "null"}");

            if (!IsCampaignCompatibleMode(game, gameStarter))
            {
                if (Settings.Instance?.TestingMode == true)
                {
                    DisplayInfo($"[BanditMilitias] '{GetGameModeName(game)}' modu desteklenmiyor  mod devre dýþý.");
                }
                return;
            }

            IsSandboxMode = game.GameType != null && game.GameType.GetType().Name.IndexOf("SandBox", StringComparison.OrdinalIgnoreCase) >= 0;

            try
            {
                Infrastructure.CompatibilityLayer.ResetActivationDelayState();
            }
            catch (Exception ex)
            {
                Infrastructure.FileLogger.LogWarning($"ActivationDelayState Reset skipped: {ex.Message}");
                DebugLogger.Warning("SubModule", $"ActivationDelayState Reset skipped: {ex.Message}");
            }

            if (Settings.Instance?.TestingMode == true)
            {
                DisplayInfo($"[BanditMilitias] Baþlatýlýyor: {GetGameModeName(game)}", Colors.Cyan);
            }

            if (_currentState == ModState.Failed)
            {
                DisplayError("Mod initialization failed - BanditMilitias is disabled");
                return;
            }

            _initializationAttempts++;

            if (_initializationAttempts > MAX_INIT_ATTEMPTS)
            {
                DisplayError("Max initialization attempts exceeded - mod disabled");
                TransitionToState(ModState.Failed);
                return;
            }

            var timer = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                Settings.Instance?.ValidateAndClampSettings();
                ConfigureTestMode();

                Infrastructure.FileLogger.Log("InitializeInfrastructure: begin");
                if (!InitializeInfrastructure())
                {
                    throw new InvalidOperationException("Infrastructure initialization failed");
                }

                Infrastructure.FileLogger.Log("InitializeInfrastructure: done");

                Infrastructure.FileLogger.Log("ModuleManager.InitializeAll: begin");

                // BOOT WATCHDOG: Başlatma süresini gerçek zamanlı izle
                var bootWatch = System.Diagnostics.Stopwatch.StartNew();
                // Infrastructure.ModuleManager.Instance.InitializeAll();
                bootWatch.Stop();

                if (bootWatch.ElapsedMilliseconds > 500)
                {
                    Infrastructure.FileLogger.LogWarning($"[BootWatchdog] InitializeAll took {bootWatch.ElapsedMilliseconds}ms! Potential hang detected.");
                }
                Infrastructure.FileLogger.Log("ModuleManager.InitializeAll: done");

                // Kayıt defteri denetimi — her zaman çalışır
                BanditMilitias.Core.Registry.AuditResult? registryAudit = null;
                try
                {
                    registryAudit = BanditMilitias.Core.Registry.ModuleRegistry.Instance.Audit();
                    Infrastructure.FileLogger.Log($"[ModuleRegistry] {registryAudit}");
                }
                catch (Exception ex)
                {
                    Infrastructure.FileLogger.LogWarning($"[ModuleRegistry] Audit skipped: {ex.GetType().Name}: {ex.Message}");
                    DebugLogger.Warning("SubModule", $"ModuleRegistry audit skipped: {ex.Message}");
                }

                if (registryAudit?.HasProblems == true)
                {
                    try
                    {
                        string report = BanditMilitias.Core.Registry.ModuleRegistry.Instance.GenerateReport();
                        Infrastructure.FileLogger.Log(report);
                    }
                    catch (Exception ex)
                    {
                        Infrastructure.FileLogger.LogWarning($"[ModuleRegistry] Report generation skipped: {ex.GetType().Name}: {ex.Message}");
                        DebugLogger.Warning("SubModule", $"ModuleRegistry report generation skipped: {ex.Message}");
                    }

                    // Kayıtsız (ghost) modül varsa ekrana da göster
                    if (registryAudit.Unregistered.Count > 0)
                    {
                        string ghostList = string.Join(", ",
                            registryAudit.Unregistered.Select(e => e.Name));
                        TaleWorlds.Library.InformationManager.DisplayMessage(
                            new TaleWorlds.Library.InformationMessage(
                                $"[BanditMilitias] ❌ {registryAudit.Unregistered.Count} kayıtsız modül: {ghostList}",
                                TaleWorlds.Library.Colors.Red));
                    }
                }

                // ── KRİTİK: Ağır sistem başlatma ertelendi ──────────────────
                // InitializeCoreSystems, InitializeAISystems, InitializeStrategicSystems,
                // InitializeBrainSystem artık OnGameStart'ta çalışmıyor.
                // Bu sistemler Campaign verilerine (Settlement.All, WarlordSystem vb.) erişiyor
                // ve oyun dünyası henüz yüklenmeden bu verilere erişmek CRASH'e neden oluyordu.
                // Tüm ağır başlatma MilitiaBehavior.OnSessionLaunched'a taşındı.
                // Mod, haritaya girildikten 2 oyun günü sonra tamamen aktif olacak.
                Infrastructure.FileLogger.Log("Heavy system init DEFERRED to OnSessionLaunched");
                _deferredInitDone = false;

                if (gameStarter == null)
                {
                    Infrastructure.FileLogger.LogError("OnGameStart: gameStarter is null");
                    throw new InvalidOperationException("GameStarter is null");
                }

                Infrastructure.FileLogger.Log("RegisterGameModels: begin");
                RegisterGameModels(gameStarter);
                Infrastructure.FileLogger.Log("RegisterGameModels: done");

                Infrastructure.FileLogger.Log("RegisterCampaignBehaviors: begin");
                if (!RegisterCampaignBehaviors(gameStarter))
                {
                    DebugLogger.Error("SubModule",
                        $"gameStarter '{gameStarter.GetType().Name}' AddBehavior desteði sunmuyor. " +
                        "Davranýþlar kaydedilemedi  mod sýnýrlý modda çalýþacak.");
                    TransitionToState(ModState.Degraded);
                    DisplayWarning("[BanditMilitias] Campaign behaviors could not be registered. Some features may not work.");
                }

                Infrastructure.FileLogger.Log("RegisterCampaignBehaviors: done");

                TransitionToState(ModState.Dormant);
                Infrastructure.FileLogger.Log("OnGameStart: state dormant (waiting for activation)");

                _initializationAttempts = 0;

                timer.Stop();
                _totalInitTime += timer.Elapsed.TotalMilliseconds;
                Infrastructure.FileLogger.Log($"OnGameStart: done in {timer.Elapsed.TotalMilliseconds:F2}ms (heavy init deferred)");

                if (Settings.Instance?.TestingMode == true)
                {
                    DisplayInfo($"[BanditMilitias] Temel yükleme tamamlandı ({timer.Elapsed.TotalMilliseconds:F2}ms) — sistemler harita yüklenince başlayacak", Colors.Cyan);
                }
            }
            catch (Exception ex)
            {
                Infrastructure.FileLogger.LogError($"OnGameStart failed: {ex.Message}");
                HandleCriticalError("OnGameStart", ex);

                if (_initializationAttempts < MAX_INIT_ATTEMPTS)
                {
                    TransitionToState(ModState.Degraded);
                    DisplayWarning("Mod running in degraded mode - some features disabled");
                }
                else
                {
                    TransitionToState(ModState.Failed);
                }
            }
        }

        private bool InitializeInfrastructure()
        {
            try
            {
                try
                {
                    Core.Events.EventBus.Instance.Clear();
                    _eventBusEnabled = true;
                }
                catch (Exception ex)
                {
                    DebugLogger.Error("SubModule", $"EventBus initialization failed: {ex.Message}");
                    _eventBusEnabled = false;
                }


                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Error("SubModule", $"Infrastructure initialization failed: {ex.Message}");
                return false;
            }
        }

        private static void RegisterModules()
        {
            try
            {
                var mm = Infrastructure.ModuleManager.Instance;
                var criticalFailures = new System.Collections.Generic.List<string>();

                void RegisterSafe(Func<BanditMilitias.Core.Components.IMilitiaModule?> moduleFactory, string moduleLabel, bool critical = false)
                {
                    BanditMilitias.Core.Components.IMilitiaModule? module = null;

                    try
                    {
                        module = moduleFactory();
                        if (module == null)
                        {
                            Infrastructure.FileLogger.LogWarning($"[SubModule] Module resolution returned NULL for {moduleLabel}");
                        }
                        else
                        {
                            Infrastructure.FileLogger.Log($"[SubModule] Resolved {moduleLabel} ({module.GetType().Name}). Status: {(module.IsEnabled ? "Enabled" : "Disabled")}");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Error("SubModule", $"Module resolution failed for {moduleLabel}: {ex}");
                        Infrastructure.FileLogger.LogWarning($"[SubModule] CRITICAL: Resolution exception for {moduleLabel}: {ex.Message}");
                        if (critical)
                        {
                            criticalFailures.Add(moduleLabel);
                        }
                        return;
                    }

                    if (module == null)
                    {
                        DebugLogger.Warning("SubModule", $"Module resolution returned null for {moduleLabel}");
                        if (critical)
                        {
                            criticalFailures.Add(moduleLabel);
                        }
                        return;
                    }

                    try
                    {
                        mm.RegisterModule(module);
                    }
                    catch (Exception ex)
                    {
                        string moduleName = string.IsNullOrWhiteSpace(module.ModuleName)
                            ? module.GetType().Name
                            : module.ModuleName;
                        DebugLogger.Error("SubModule", $"Module registration failed for {moduleLabel} ({moduleName}): {ex}");
                        if (critical)
                        {
                            criticalFailures.Add(moduleLabel);
                        }
                    }
                }

                RegisterSafe(() => Intelligence.AI.Components.StaticDataCache.Instance, nameof(Intelligence.AI.Components.StaticDataCache), critical: true);
                RegisterSafe(() => Systems.Grid.SpatialGridSystem.Instance, nameof(Systems.Grid.SpatialGridSystem), critical: true);
                RegisterSafe(() => Intelligence.Swarm.SwarmCoordinator.Instance, nameof(Intelligence.Swarm.SwarmCoordinator), critical: true);
                RegisterSafe(() => new Systems.Spawning.MilitiaSpawningSystem(), nameof(Systems.Spawning.MilitiaSpawningSystem), critical: true);

                RegisterSafe(() => Systems.Fear.FearSystem.Instance, nameof(Systems.Fear.FearSystem), critical: true);
                RegisterSafe(() => Systems.Progression.WarlordLegitimacySystem.Instance, nameof(Systems.Progression.WarlordLegitimacySystem), critical: true);
                RegisterSafe(() => Systems.Progression.AscensionEvaluator.Instance, nameof(Systems.Progression.AscensionEvaluator), critical: false);
                RegisterSafe(() => Systems.Bounty.BountySystem.Instance, nameof(Systems.Bounty.BountySystem));
                RegisterSafe(() => Systems.Raiding.MilitiaRaidSystem.Instance, nameof(Systems.Raiding.MilitiaRaidSystem));
                RegisterSafe(() => Systems.Logistics.WarlordLogisticsSystem.Instance, nameof(Systems.Logistics.WarlordLogisticsSystem));
                RegisterSafe(() => Systems.AI.AdaptiveAIDoctrineSystem.Instance, nameof(Systems.AI.AdaptiveAIDoctrineSystem));

                RegisterSafe(() => new Systems.Scheduling.AISchedulerSystem(), nameof(Systems.Scheduling.AISchedulerSystem), critical: true);
                RegisterSafe(() => new Systems.Cleanup.PartyCleanupSystem(), nameof(Systems.Cleanup.PartyCleanupSystem), critical: true);
                RegisterSafe(() => Systems.Cleanup.MilitiaConsolidationSystem.Instance, nameof(Systems.Cleanup.MilitiaConsolidationSystem));

                RegisterSafe(() => Systems.Tracking.WarActivityTracker.Instance, nameof(Systems.Tracking.WarActivityTracker));
                RegisterSafe(() => Systems.Tracking.CaravanActivityTracker.Instance, nameof(Systems.Tracking.CaravanActivityTracker));
                RegisterSafe(() => Systems.Enhancement.BanditEnhancementSystem.Instance, nameof(Systems.Enhancement.BanditEnhancementSystem));
                RegisterSafe(() => Systems.Enhancement.HeroicFeatsSystem.Instance, nameof(Systems.Enhancement.HeroicFeatsSystem));
                RegisterSafe(() => Systems.Spawning.DynamicHideoutSystem.Instance, nameof(Systems.Spawning.DynamicHideoutSystem), critical: true);
                RegisterSafe(() => Systems.Spawning.HardcoreDynamicHideoutSystem.Instance, nameof(Systems.Spawning.HardcoreDynamicHideoutSystem));
                RegisterSafe(() => Systems.Diplomacy.ExtortionSystem.Instance, nameof(Systems.Diplomacy.ExtortionSystem));
                RegisterSafe(() => Systems.Diplomacy.BanditPoliticsSystem.Instance, nameof(Systems.Diplomacy.BanditPoliticsSystem));
                RegisterSafe(() => Systems.Diplomacy.DuelSystem.Instance, nameof(Systems.Diplomacy.DuelSystem));

                RegisterSafe(() => Systems.Events.JailbreakMissionSystem.Instance, nameof(Systems.Events.JailbreakMissionSystem));
                RegisterSafe(() => new Systems.Economy.BlackMarketSystem(), nameof(Systems.Economy.BlackMarketSystem));
                RegisterSafe(() => Systems.Economy.WarlordEconomySystem.Instance, nameof(Systems.Economy.WarlordEconomySystem));
                RegisterSafe(() => new Systems.Diplomacy.PropagandaSystem(), nameof(Systems.Diplomacy.PropagandaSystem));
                RegisterSafe(() => Systems.Enhancement.WarlordTacticsSystem.Instance, nameof(Systems.Enhancement.WarlordTacticsSystem));
                RegisterSafe(() => Systems.Workshop.WarlordWorkshopSystem.Instance, nameof(Systems.Workshop.WarlordWorkshopSystem));
                RegisterSafe(() => Systems.Behavior.WarlordBehaviorSystem.Instance, nameof(Systems.Behavior.WarlordBehaviorSystem));

                // ── Yeni Sistemler (v5.1) ──────────────────────────────────────────
                RegisterSafe(() => Systems.Economy.CaravanTaxSystem.Instance, nameof(Systems.Economy.CaravanTaxSystem));
                RegisterSafe(() => Systems.Seasonal.SeasonalEffectsSystem.Instance, nameof(Systems.Seasonal.SeasonalEffectsSystem));
                RegisterSafe(() => Systems.Progression.WarlordSuccessionSystem.Instance, nameof(Systems.Progression.WarlordSuccessionSystem));
                RegisterSafe(() => Systems.Combat.MilitiaMoraleSystem.Instance, nameof(Systems.Combat.MilitiaMoraleSystem));
                // BUG FIX v5.2: Assertion ve oto-düzeltme katmanı eklendi
                RegisterSafe(() => Systems.Diagnostics.MilitiaAssertionSystem.Instance, nameof(Systems.Diagnostics.MilitiaAssertionSystem));
                // ──────────────────────────────────────────────────────────────────

                // ── Eksik 6 sistem kaydı ──────────────────────────────────
                RegisterSafe(() => Systems.Progression.WarlordCareerSystem.Instance, nameof(Systems.Progression.WarlordCareerSystem));
                RegisterSafe(() => Systems.Legacy.WarlordLegacySystem.Instance, nameof(Systems.Legacy.WarlordLegacySystem));
                RegisterSafe(() => Systems.Territory.TerritorySystem.Instance, nameof(Systems.Territory.TerritorySystem));
                // WarlordCombatSystem → MissionBehavior, OnMissionBehaviorInitialize'da ekleniyor
                // ─────────────────────────────────────────────────────────

                RegisterSafe(() => Intelligence.Strategic.BanditBrain.Instance, nameof(Intelligence.Strategic.BanditBrain), critical: true);
                RegisterSafe(() => Intelligence.Strategic.WarlordSystem.Instance, nameof(Intelligence.Strategic.WarlordSystem), critical: true);
                RegisterSafe(() => Intelligence.Narrative.WarlordNarrativeSystem.Instance, nameof(Intelligence.Narrative.WarlordNarrativeSystem));
                RegisterSafe(() => Systems.Tracking.PlayerTracker.Instance, nameof(Systems.Tracking.PlayerTracker));
                RegisterSafe(() => Intelligence.ML.AILearningSystem.Instance, nameof(Intelligence.ML.AILearningSystem));
                RegisterSafe(() => Systems.Crisis.CrisisEventSystem.Instance, nameof(Systems.Crisis.CrisisEventSystem));

                // Developer-only — IsEnabled=false üretimde, sıfır overhead
                RegisterSafe(() => Systems.Dev.DevDataCollector.Instance, nameof(Systems.Dev.DevDataCollector));

                bool isTestingMode = false;
                try { isTestingMode = Settings.Instance?.TestingMode == true; } catch { }
                if (isTestingMode)
                {
                    Infrastructure.ModuleValidator.ValidateRegistrations();
                }

                if (criticalFailures.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Critical modules failed to register: {string.Join(", ", criticalFailures.Distinct())}");
                }

                DebugLogger.Info("SubModule", "All modules registered successfully");
            }
            catch (Exception ex)
            {
                DebugLogger.Error("SubModule", $"Module registration failed: {ex.Message}");
                throw;
            }
        }

        private static bool InitializeCoreSystems()
        {

            bool dynamicHideoutReady = true;

            try
            {
                Systems.Spawning.DynamicHideoutSystem.Instance.Initialize();
                Systems.Spawning.HardcoreDynamicHideoutSystem.Instance.Initialize();
            }
            catch (Exception ex)
            {
                dynamicHideoutReady = false;
                DebugLogger.Warning("SubModule", $"Hideout systems initialization deferred: {ex.Message}");
            }

            if (!dynamicHideoutReady)
            {
                DisplayWarning("Hideout initialization deferred; retry will run on session launch.");
            }

            return true;
        }

        private static bool InitializeAISystems()
        {
            try
            {
                Intelligence.AI.CustomMilitiaAI.Initialize();

                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Error("SubModule", $"AI systems initialization failed: {ex.Message}");
                return false;
            }
        }

        private static bool InitializeStrategicSystems()
        {
            try
            {
                if (Settings.Instance?.EnableWarlords == true)
                {
                    if (Settings.Instance?.TestingMode == true)
                    {
                        var warlordCount = Intelligence.Strategic.WarlordSystem.Instance.GetAllWarlords().Count;
                        DebugLogger.Info("SubModule", $"WarlordSystem active: {warlordCount} warlords");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Error("SubModule", $"Strategic systems initialization failed: {ex.Message}");
                return false;
            }
        }

        private static bool InitializeBrainSystem()
        {
            try
            {
                var brainModule = Infrastructure.ModuleManager.Instance
                    .GetModule<Intelligence.Strategic.BanditBrain>();

                if (brainModule == null)
                {
                    DebugLogger.Warning("SubModule", "BanditBrain module not registered");
                    return false;
                }

                if (!brainModule.IsEnabled)
                {
                    DebugLogger.Warning("SubModule", "BanditBrain module is disabled by settings");
                    return false;
                }

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Info("SubModule", "BanditBrain managed by ModuleManager - Strategic AI active");
                }

                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Error("SubModule", $"BanditBrain initialization failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// OnGameStart'tan ertelenen ağır sistem başlatmalarını çalıştırır.
        /// MilitiaBehavior.OnSessionLaunched tarafından çağrılır — Campaign verisi hazır olduğunda.
        /// </summary>
        public static bool RunDeferredSystemInit()
        {
            if (_deferredInitDone) return true;

            Infrastructure.FileLogger.Log("RunDeferredSystemInit: begin");
            var timer = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Sistem sağlık kontrolü
                if (Settings.Instance?.TestingMode == true)
                {
                    try
                    {
                        var health = Infrastructure.HealthCheck.RunDiagnostics(autoFix: true);
                        Infrastructure.FileLogger.Log($"[HealthCheck] {health}");
                    }
                    catch (Exception ex)
                    {
                        Infrastructure.FileLogger.LogWarning($"[HealthCheck] skipped: {ex.Message}");
                    }
                }

                Infrastructure.FileLogger.Log("InitializeCoreSystems: begin");
                bool coreOk = InitializeCoreSystems();
                Infrastructure.FileLogger.Log($"InitializeCoreSystems: {(coreOk ? "OK" : "FAILED")}");

                Infrastructure.FileLogger.Log("InitializeAISystems: begin");
                _aiSystemEnabled = InitializeAISystems();
                Infrastructure.FileLogger.Log($"InitializeAISystems: {(_aiSystemEnabled ? "OK" : "FAILED")}");

                Infrastructure.FileLogger.Log("InitializeStrategicSystems: begin");
                _warlordSystemEnabled = InitializeStrategicSystems();
                Infrastructure.FileLogger.Log($"InitializeStrategicSystems: {(_warlordSystemEnabled ? "OK" : "FAILED")}");

                Infrastructure.FileLogger.Log("InitializeBrainSystem: begin");
                _brainSystemEnabled = InitializeBrainSystem();
                Infrastructure.FileLogger.Log($"InitializeBrainSystem: {(_brainSystemEnabled ? "OK" : "FAILED")}");

                Infrastructure.FileLogger.Log("ModuleManager.InitializeAll: begin");
                var bootWatch = System.Diagnostics.Stopwatch.StartNew();
                Infrastructure.ModuleManager.Instance.InitializeAll();
                bootWatch.Stop();
                if (bootWatch.ElapsedMilliseconds > 500)
                {
                    Infrastructure.FileLogger.LogWarning($"[BootWatchdog] InitializeAll took {bootWatch.ElapsedMilliseconds}ms! Potential hang detected.");
                }
                Infrastructure.FileLogger.Log("ModuleManager.InitializeAll: done");

                _deferredInitDone = true;
                _deferredInitFailureCount = 0;

                timer.Stop();
                Infrastructure.FileLogger.Log($"RunDeferredSystemInit: done in {timer.Elapsed.TotalMilliseconds:F2}ms");

                if (Settings.Instance?.TestingMode == true)
                {
                    DisplayInfo($"[BanditMilitias] Sistemler başlatıldı ({timer.Elapsed.TotalMilliseconds:F2}ms)", Colors.Green);
                    DisplaySystemStatus();
                }

                return true;
            }
            catch (Exception ex)
            {
                Infrastructure.FileLogger.LogError($"RunDeferredSystemInit failed: {ex.Message}");
                DebugLogger.Error("SubModule", $"RunDeferredSystemInit failed: {ex}");
                _deferredInitFailureCount++;
                if (_deferredInitFailureCount >= MAX_DEFERRED_INIT_FAILURES)
                {
                    EnterEmergencyStop(
                        $"Deferred system init failed {_deferredInitFailureCount} times.",
                        ex);
                }
                return false;
            }
        }

        private static void RegisterGameModels(IGameStarter gameStarter)
        {
            static void AddModelSafe(IGameStarter starter, GameModel model, string modelName)
            {
                Infrastructure.FileLogger.Log($"RegisterGameModel: {modelName}");
                starter.AddModel(model);
                Infrastructure.FileLogger.Log($"Registered game model: {modelName}");
            }

            try
            {
                AddModelSafe(gameStarter, new Models.ModBanditDensityModel(), nameof(Models.ModBanditDensityModel));
                AddModelSafe(gameStarter, new Models.MilitiaSpeedModel(), nameof(Models.MilitiaSpeedModel));
                AddModelSafe(gameStarter, new Models.ModPartySizeLimitModel(), nameof(Models.ModPartySizeLimitModel));
            }
            catch (Exception ex)
            {
                Infrastructure.FileLogger.LogError($"RegisterGameModels failed: {ex}");
                DebugLogger.Error("SubModule", $"Model registration failed: {ex.Message}");
            }
        }

        private static bool RegisterCampaignBehaviors(IGameStarter gameStarter)
        {
            static void AddBehaviorSafe(Action<CampaignBehaviorBase> register, CampaignBehaviorBase behavior, string behaviorName)
            {
                Infrastructure.FileLogger.Log($"RegisterCampaignBehavior: {behaviorName}");
                register(behavior);
                Infrastructure.FileLogger.Log($"Registered campaign behavior: {behaviorName}");
            }

            try
            {
                if (gameStarter is CampaignGameStarter campaignStarter)
                {
                    AddBehaviorSafe(campaignStarter.AddBehavior, new Behaviors.MilitiaBehavior(), nameof(Behaviors.MilitiaBehavior));
                    AddBehaviorSafe(campaignStarter.AddBehavior, new Behaviors.MilitiaHideoutCampaignBehavior(), nameof(Behaviors.MilitiaHideoutCampaignBehavior));
                    AddBehaviorSafe(campaignStarter.AddBehavior, new Behaviors.MilitiaRewardCampaignBehavior(), nameof(Behaviors.MilitiaRewardCampaignBehavior));
                    AddBehaviorSafe(campaignStarter.AddBehavior, new Behaviors.MilitiaDiplomacyCampaignBehavior(), nameof(Behaviors.MilitiaDiplomacyCampaignBehavior));
                    AddBehaviorSafe(campaignStarter.AddBehavior, new Behaviors.WarlordCampaignBehavior(), nameof(Behaviors.WarlordCampaignBehavior));
                    return true;
                }

                var addBehaviorMethod = gameStarter.GetType().GetMethod(
                    "AddBehavior",
                    new[] { typeof(CampaignBehaviorBase) })
                    ?? gameStarter.GetType().GetMethod("AddBehavior");

                if (addBehaviorMethod == null)
                {

                    if (gameStarter.GetType().Name.Contains("SandBox") || gameStarter.GetType().Name.Contains("Sandbox"))
                    {
                        try
                        {
                            dynamic starter = gameStarter;
                            AddBehaviorSafe(starter.AddBehavior, new Behaviors.MilitiaBehavior(), nameof(Behaviors.MilitiaBehavior));
                            AddBehaviorSafe(starter.AddBehavior, new Behaviors.MilitiaHideoutCampaignBehavior(), nameof(Behaviors.MilitiaHideoutCampaignBehavior));
                            AddBehaviorSafe(starter.AddBehavior, new Behaviors.MilitiaRewardCampaignBehavior(), nameof(Behaviors.MilitiaRewardCampaignBehavior));
                            AddBehaviorSafe(starter.AddBehavior, new Behaviors.MilitiaDiplomacyCampaignBehavior(), nameof(Behaviors.MilitiaDiplomacyCampaignBehavior));
                            AddBehaviorSafe(starter.AddBehavior, new Behaviors.WarlordCampaignBehavior(), nameof(Behaviors.WarlordCampaignBehavior));
                            return true;
                        }
                        catch (Exception innerEx)
                        {
                            Infrastructure.FileLogger.LogError($"Dynamic behavior registration failed: {innerEx}");
                            DebugLogger.Error("SubModule", $"Dynamic behavior registration failed: {innerEx.Message}");
                            return false;
                        }
                    }
                    return false;
                }

                AddBehaviorSafe(
                    behavior => _ = addBehaviorMethod.Invoke(gameStarter, new object[] { behavior }),
                    new Behaviors.MilitiaBehavior(),
                    nameof(Behaviors.MilitiaBehavior));
                AddBehaviorSafe(
                    behavior => _ = addBehaviorMethod.Invoke(gameStarter, new object[] { behavior }),
                    new Behaviors.MilitiaHideoutCampaignBehavior(),
                    nameof(Behaviors.MilitiaHideoutCampaignBehavior));
                AddBehaviorSafe(
                    behavior => _ = addBehaviorMethod.Invoke(gameStarter, new object[] { behavior }),
                    new Behaviors.MilitiaRewardCampaignBehavior(),
                    nameof(Behaviors.MilitiaRewardCampaignBehavior));
                AddBehaviorSafe(
                    behavior => _ = addBehaviorMethod.Invoke(gameStarter, new object[] { behavior }),
                    new Behaviors.MilitiaDiplomacyCampaignBehavior(),
                    nameof(Behaviors.MilitiaDiplomacyCampaignBehavior));
                AddBehaviorSafe(
                    behavior => _ = addBehaviorMethod.Invoke(gameStarter, new object[] { behavior }),
                    new Behaviors.WarlordCampaignBehavior(),
                    nameof(Behaviors.WarlordCampaignBehavior));
                return true;
            }
            catch (Exception ex)
            {
                Infrastructure.FileLogger.LogError($"RegisterCampaignBehaviors failed: {ex}");
                DebugLogger.Error("SubModule", $"Behavior registration failed: {ex.Message}");
                return false;
            }
        }

        public override void OnGameLoaded(Game game, object initDataObject)
        {
            base.OnGameLoaded(game, initDataObject);

            if (!IsCampaignCompatibleMode(game)) return;

            try
            {
                Patches.SurrenderFix.SurrenderCrashPatch.ResetState();
                if (_aiSystemEnabled)
                {
                    Intelligence.AI.CustomMilitiaAI.Initialize();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error("SubModule", $"OnGameLoaded error: {ex.Message}");
            }
        }

        public override void OnNewGameCreated(Game game, object initDataObject)
        {
            base.OnNewGameCreated(game, initDataObject);

            if (!IsCampaignCompatibleMode(game)) return;

            try
            {
                Patches.SurrenderFix.SurrenderCrashPatch.ResetState();
            }
            catch (Exception ex)
            {
                DebugLogger.Error("SubModule", $"OnNewGameCreated error: {ex.Message}");
            }
        }
        public override void OnGameEnd(Game game)
        {
            base.OnGameEnd(game);
            if (!IsCampaignCompatibleMode(game)) return;

            // FIX-DOUBLE-CLEANUP: Önceden WarlordSystem, BanditBrain, PlayerTracker burada
            // ayrıca temizleniyordu, CleanupAll() içinde tekrar çağrılıyordu. Aynı şekilde
            // WarlordCareerSystem, WarlordLegacySystem ve v5.1 sistemleri CleanupAll()'dan
            // SONRA tekrar çağrılıyordu — çift Cleanup() hata/corruption riskine neden oluyordu.
            // Kayıtlı tüm modüller CleanupAll() tarafından ters öncelik sırasıyla temizleniyor.

            // FIX Bug 4: Reset static states on game end to prevent pollution of next loaded session
            try
            {
                Infrastructure.CompatibilityLayer.ResetCampaignStartTimeCache();
                Infrastructure.CompatibilityLayer.ResetActivationDelayState();
                Infrastructure.ClanCache.Reset();
            }
            catch (Exception ex)
            {
                Infrastructure.FileLogger.LogWarning($"Static state reset failed on OnGameEnd: {ex.Message}");
            }

            // CleanupAll: WarlordSystem, BanditBrain, PlayerTracker, WarlordCareerSystem,
            // WarlordLegacySystem, CaravanTaxSystem, SeasonalEffectsSystem, WarlordSuccessionSystem,
            // MilitiaMoraleSystem ve diğer tüm kayıtlı modüller burada temizleniyor.
            try { Infrastructure.ModuleManager.Instance.CleanupAll(); }
            catch (Exception ex) { HandleCriticalError("ModuleManager Cleanup", ex); }

            // CustomMilitiaAI kayıtlı bir modül değil — ayrı temizleniyor
            try { Intelligence.AI.CustomMilitiaAI.Cleanup(); }
            catch (Exception ex) { HandleCriticalError("CustomMilitiaAI Cleanup", ex); }

            try { if (_eventBusEnabled) Core.Events.EventBus.Instance.Clear(); }
            catch (Exception ex) { HandleCriticalError("EventBus Cleanup", ex); }

            try { BanditMilitias.Core.Config.Globals.Reset(); }
            catch (Exception ex) { HandleCriticalError("Globals Reset", ex); }

            // CompatibilityLayer sıfırlaması yukarıda (FIX Bug 4 bloğu) zaten yapıldı;
            // buradaki tekrar çağrılar kaldırıldı.

            try { BanditMilitias.Systems.Combat.MilitiaVictorySystem.Reset(); }
            catch (Exception ex) { HandleCriticalError("VictorySystem Reset", ex); }

            try { BanditMilitias.Debug.DebugPanel.Reset(); }
            catch (Exception ex) { HandleCriticalError("DebugPanel Reset", ex); }

            // FIX: Watchdog'u temizle - bir sonraki oyunda eski CampaignTime
            // değerleri kalmasın, sahte timeout alarmı tetiklenmesin
            try { BanditMilitias.Systems.Diagnostics.SystemWatchdog.Instance.Clear(); }
            catch (Exception ex) { HandleCriticalError("Watchdog Clear", ex); }

            try
            {
                _tickErrorCount = 0;
                _initializationAttempts = 0;
                IsSandboxMode = false;
                // FIX: _deferredInitDone sıfırlanmıyordu — yeni oyun açıldığında
                // RunDeferredSystemInit() "already done" diyerek erken dönüyordu.
                _deferredInitDone = false;

                _eventBusEnabled = true;
                _aiSystemEnabled = true;
                _warlordSystemEnabled = true;
                _brainSystemEnabled = true;
                _deferredInitFailureCount = 0;

                TransitionToState(ModState.Uninitialized);

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Info("SubModule", "Complete cleanup executed - mod reset");
                }
            }
            catch (Exception ex)
            {
                HandleCriticalError("OnGameEnd State Reset", ex);
            }
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);

            if (_currentState != ModState.Active && _currentState != ModState.Degraded) return;

            try
            {
                if (_eventBusEnabled)
                {
                    Core.Events.EventBus.Instance?.ProcessQueue();
                }
                else
                {
                    Core.Events.EventBus.Instance?.ClearQueue();
                }

                if (Infrastructure.ModuleManager.Instance != null)
                {
                    Infrastructure.ModuleManager.Instance.OnApplicationTick(dt);
                }

                DebugPanel.Instance.Update();
            }
            catch (Exception ex)
            {
                HandleTickError(ex);
            }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);

            if (_warlordSystemEnabled)
            {
                // HTN Tactical Layer Integration (Katman A -> Katman B köprüsü)
                mission.AddMissionBehavior(new Intelligence.Tactical.WarlordTacticalMissionBehavior());

                // FIX-GHOST: WarlordEquipmentMissionBehavior tanımlıydı ama hiç eklenmiyordu.
                // AdaptiveAIDoctrineSystem doctrine seçip CurrentBattleDoctrine'i set ediyordu,
                // ancak bu değeri okuyan MissionBehavior misyona bağlı değildi. Artık aktif.
                if (Settings.Instance?.EnableAdaptiveAIDoctrine == true)
                {
                    mission.AddMissionBehavior(new Systems.AI.WarlordEquipmentMissionBehavior());
                }

                if (Settings.Instance?.EnableWarlordRegeneration == true)
                {
                    mission.AddMissionBehavior(new Systems.Combat.WarlordRegenerationSystem());
                    mission.AddMissionBehavior(new Systems.Combat.WarlordCombatSystem());
                }
            }
        }

        private void HandleTickError(Exception ex)
        {
            var now = DateTime.Now;

            if ((now - _lastTickError).TotalMinutes > 1.0)
            {
                _tickErrorCount = 0;
            }

            _tickErrorCount++;
            _lastTickError = now;

            if (_tickErrorCount > MAX_TICK_ERRORS_PER_MINUTE)
            {
                if (_currentState == ModState.Active)
                {
                    TransitionToState(ModState.Degraded);
                    DisplayError("Too many tick errors - mod entering degraded mode");
                }

                _eventBusEnabled = false;

                if (_tickErrorCount > MAX_TICK_ERRORS_FOR_EMERGENCY)
                {
                    EnterEmergencyStop(
                        $"Tick error storm detected ({_tickErrorCount}/min).",
                        ex);
                }
                return;
            }

            if (Settings.Instance?.TestingMode == true && _tickErrorCount % 5 == 1)
            {
                DisplayError($"Tick error ({_tickErrorCount}): {ex.Message}");
            }
        }

        private static void HandleCriticalError(string context, Exception ex)
        {
            string message = $"[BanditMilitias] CRITICAL ERROR in {context}: {ex.Message}";

            InformationManager.DisplayMessage(new InformationMessage(message, Colors.Red));
            TaleWorlds.Library.Debug.Print(message);
            TaleWorlds.Library.Debug.Print($"Stack trace: {ex.StackTrace}");

            DebugLogger.Error("SubModule", $"{context} critical error: {ex}");
        }

        private static void EnterEmergencyStop(string reason, Exception? ex = null)
        {
            if (_currentState == ModState.EmergencyStop) return;

            _eventBusEnabled = false;
            _aiSystemEnabled = false;
            _warlordSystemEnabled = false;
            _brainSystemEnabled = false;

            try { Core.Events.EventBus.Instance?.ClearQueue(); } catch { }

            TransitionToState(ModState.EmergencyStop);

            string message = $"EmergencyStop activated: {reason}";
            try { Infrastructure.FileLogger.LogError(message); } catch { }

            if (ex != null)
            {
                DebugLogger.Error("SubModule", $"{message} Exception: {ex}");
            }
            else
            {
                DebugLogger.Error("SubModule", message);
            }

            DisplayError($"Emergency stop activated: {reason}");
        }

        private bool ApplyHarmonyPatches()
        {
            if (_harmonyPatched) return true;

            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll();
                _harmonyPatched = true;

                if (!ValidateCriticalPatches())
                {
                    DebugLogger.Warning("SubModule", "Critical Harmony patches missing - running in degraded mode.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                DisplayError($"Harmony patching failed: {ex.Message}");
                DebugLogger.Error("SubModule", $"Harmony patch error: {ex}");
                return false;
            }
        }

        private bool ValidateCriticalPatches()
        {
            bool ok = true;

            var captivityType = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.PlayerCaptivityCampaignBehavior");
            if (captivityType != null) ok &= EnsurePatch(captivityType, "CheckCaptivityChange", "Captivity fix");

            var destroyPartyType = AccessTools.TypeByName("TaleWorlds.CampaignSystem.Actions.DestroyPartyAction");
            if (destroyPartyType != null) ok &= EnsurePatch(destroyPartyType, "Apply", "Captor destruction guard");

            var aiPatrolType = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.AiBehaviors.AiPatrollingBehavior");
            var aiMethod = aiPatrolType != null
                ? (AccessTools.Method(aiPatrolType, "AiHourlyTick", new[] { typeof(MobileParty), typeof(PartyThinkParams) })
                   ?? AccessTools.Method(aiPatrolType, "AiHourlyTick"))
                : null;

            if (aiMethod == null)
            {
                DebugLogger.Warning("SubModule", "Critical patch target not found: AiPatrollingBehavior.AiHourlyTick");
                ok = false;
            }
            else if (!IsPatchedByUs(aiMethod))
            {
                DebugLogger.Error("SubModule", "Critical patch missing: AiPatrollingBehavior.AiHourlyTick");
                ok = false;
            }

            var aiLandBanditType = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.AiBehaviors.AiLandBanditPatrollingBehavior");
            var aiLandBanditMethod = aiLandBanditType != null
                ? (AccessTools.Method(aiLandBanditType, "AiHourlyTick", new[] { typeof(MobileParty), typeof(PartyThinkParams) })
                   ?? AccessTools.Method(aiLandBanditType, "AiHourlyTick"))
                : null;

            if (aiLandBanditType != null)
            {
                if (aiLandBanditMethod == null)
                {
                    DebugLogger.Warning("SubModule", "Patch target not found: AiLandBanditPatrollingBehavior.AiHourlyTick");
                    ok = false;
                }
                else if (!IsPatchedByUs(aiLandBanditMethod))
                {
                    DebugLogger.Error("SubModule", "Critical patch missing: AiLandBanditPatrollingBehavior.AiHourlyTick");
                    ok = false;
                }
            }

            return ok;
        }

        private bool EnsurePatch(Type type, string methodName, string label)
        {
            var method = AccessTools.Method(type, methodName);
            if (method == null)
            {
                DebugLogger.Warning("SubModule", $"Critical patch target not found: {type.FullName}.{methodName} ({label})");
                return false;
            }

            if (!IsPatchedByUs(method))
            {
                DebugLogger.Error("SubModule", $"Critical patch missing: {type.FullName}.{methodName} ({label})");
                return false;
            }

            return true;
        }

        private bool IsPatchedByUs(MethodBase method)
        {
            var patchInfo = Harmony.GetPatchInfo(method);
            return patchInfo?.Owners?.Contains(HarmonyId) == true;
        }

        private void ValidateSettings()
        {
            if (Settings.Instance == null)
            {
                DisplayWarning("Settings not loaded - using defaults");
                return;
            }

            try
            {
                int corrections = Settings.Instance.ValidateAndClampSettingsWithDiagnostics(out string report);
                if (corrections > 0)
                {
                    DebugLogger.Warning("SubModule", report);

                    if (Settings.Instance.TestingMode)
                    {
                        DisplayWarning(report);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error("SubModule", $"Settings validation failed: {ex.Message}");
            }
        }

        private void ValidateSystemHealth()
        {
            bool healthy = true;

            if (!_aiSystemEnabled)
            {
                DebugLogger.Warning("SubModule", "AI system is disabled");
                healthy = false;
            }

            if (!_eventBusEnabled)
            {
                DebugLogger.Warning("SubModule", "Event system is disabled");
                healthy = false;
            }

            if (!Patches.SurrenderFix.SurrenderCrashPatch.ReflectionAccessAvailable)
            {
                DebugLogger.Warning("SubModule",
                    "SurrenderFix running in public API fallback mode (reflection unavailable).");
            }

            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("SubModule", Patches.SurrenderFix.SurrenderCrashPatch.GetDiagnostics());
            }

            var moduleManager = Infrastructure.ModuleManager.Instance;
            if (moduleManager.HasFailedModules)
            {
                string failedSummary = moduleManager.GetFailedModuleSummary();
                DebugLogger.Warning("SubModule", $"Fail-safe active, disabled modules: {failedSummary}");
                healthy = false;

                if (Settings.Instance?.TestingMode == true)
                {
                    DisplayWarning($"Disabled modules: {failedSummary}. Command: militia.failed_modules");
                }
            }

            if (!Infrastructure.ReleaseGate.Validate(moduleManager, out string gateMessage))
            {
                DebugLogger.Warning("SubModule", gateMessage);
                healthy = false;
            }
            else if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("SubModule", gateMessage);
            }

            if (!healthy && _currentState == ModState.Active)
            {
                TransitionToState(ModState.Degraded);
            }
        }

        private static void TransitionToState(ModState newState)
        {
            lock (_stateLock)
            {
                if (_currentState == newState) return;

                var oldState = _currentState;
                _currentState = newState;

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Info("SubModule", $"State transition: {oldState} -> {newState}");
                }
            }
        }

        private void ConfigureTestMode()
        {
            if (Settings.Instance == null) return;

            if (Settings.Instance.TestingMode && !Settings.Instance.ShowTestMessages)
            {
                Settings.Instance.ShowTestMessages = true;
                DisplayInfo("[BanditMilitias] ShowTestMessages auto-enabled", Colors.Yellow);
            }
        }

        private static void DisplaySystemStatus()
        {
            if (Settings.Instance?.TestingMode != true) return;

            string eventQueue = _eventBusEnabled
                ? Core.Events.EventBus.Instance.GetQueueDiagnostics()
                : "EventBus disabled";

            var status = "[BanditMilitias] System Status:\n" +
                        $"  Version: {ModVersion}\n" +
                        $"  State: {_currentState}\n" +
                        $"  Init Time: {_totalInitTime:F2}ms\n" +
                        $"  EventBus: {(_eventBusEnabled ? "OK" : "FAIL")}\n" +
                        $"  EventQueue: {eventQueue}\n" +
                        $"  AI System: {(_aiSystemEnabled ? "OK" : "FAIL")}\n" +
                        $"  Warlords: {(_warlordSystemEnabled ? "OK" : "FAIL")}\n" +
                        $"  BanditBrain: {(_brainSystemEnabled ? "OK" : "FAIL")}";

            DebugLogger.Info("SubModule", status);
        }

        public static string GetDiagnostics()
        {
            return "SubModule:\n" +
                   $"  State: {_currentState}\n" +
                   $"  Version: {ModVersion}\n" +
                   $"  Init Time: {_totalInitTime:F2}ms\n" +
                   $"  Tick Errors: {_tickErrorCount}\n" +
                   $"  Features: EventBus={_eventBusEnabled}, AI={_aiSystemEnabled}, " +
                   $"Warlords={_warlordSystemEnabled}, Brain={_brainSystemEnabled}";
        }

        protected override void OnSubModuleUnloaded()
        {
            base.OnSubModuleUnloaded();

            try
            {
                if (_harmonyPatched && _harmony != null)
                {
                    _harmony.UnpatchAll(_harmony.Id);
                    _harmonyPatched = false;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[BanditMilitias] Harmony unpatch failed: {ex.Message}");
            }
        }

        private static void DisplayInfo(string message, Color? color = null)
        {
            InformationManager.DisplayMessage(new InformationMessage(message, color ?? Colors.White));
        }

        private static void DisplayWarning(string message)
        {
            InformationManager.DisplayMessage(new InformationMessage($"[BanditMilitias] WARNING: {message}", Colors.Yellow));
        }

        private static void DisplayError(string message)
        {
            InformationManager.DisplayMessage(new InformationMessage($"[BanditMilitias] ERROR: {message}", Colors.Red));
        }
    }
}
