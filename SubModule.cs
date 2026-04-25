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
        public static readonly Version ModVersion = new(1, 0, 0);

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
        private const int MAX_INIT_ATTEMPTS = 3;
        private const int MAX_DEFERRED_INIT_FAILURES = 3;
        private static int _deferredInitFailureCount = 0;

        private static readonly System.Diagnostics.Stopwatch _initTimer = new();
        private static double _totalInitTime = 0.0;

        private static bool _eventBusEnabled = true;
        private static bool _aiSystemEnabled = true;
        private static bool _warlordSystemEnabled = true;
        private static bool _brainSystemEnabled = true;
        private static bool _deferredInitDone = false;

        // Public API: MilitiaBehavior'Ä±n reflection yerine doÄŸrudan kullanmasÄ± iÃ§in
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
                
                // MÄ°MARÄ° DÃœZELTME: ModÃ¼lleri SyncData'dan Ã¶nce kayÄ±t etmeliyiz.
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

                Infrastructure.CompatibilityLayer.Reset();

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
                    DisplayInfo($"[BanditMilitias] '{GetGameModeName(game)}' modu desteklenmiyor â€” mod devre dÃ½Ã¾Ã½.");
                }
                return;
            }

            IsSandboxMode = game.GameType != null && game.GameType.GetType().Name.IndexOf("SandBox", StringComparison.OrdinalIgnoreCase) >= 0;

            try
            {
                Infrastructure.CompatibilityLayer.Reset();
            }
            catch (Exception ex)
            {
                Infrastructure.FileLogger.LogWarning($"ActivationDelayState Reset skipped: {ex.Message}");
                DebugLogger.Warning("SubModule", $"ActivationDelayState Reset skipped: {ex.Message}");
            }

            if (Settings.Instance?.TestingMode == true)
            {
                DisplayInfo($"[BanditMilitias] BaÃ¾latÃ½lÃ½yor: {GetGameModeName(game)}", Colors.Cyan);
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

                // BOOT WATCHDOG: BaÅŸlatma sÃ¼resini gerÃ§ek zamanlÄ± izle
                var bootWatch = System.Diagnostics.Stopwatch.StartNew();
                // Infrastructure.ModuleManager.Instance.InitializeAll();
                bootWatch.Stop();

                if (bootWatch.ElapsedMilliseconds > 500)
                {
                    Infrastructure.FileLogger.LogWarning($"[BootWatchdog] InitializeAll took {bootWatch.ElapsedMilliseconds}ms! Potential hang detected.");
                }
                Infrastructure.FileLogger.Log("ModuleManager.InitializeAll: done");

                // KayÄ±t defteri denetimi â€” her zaman Ã§alÄ±ÅŸÄ±r
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

                    // KayÄ±tsÄ±z (ghost) modÃ¼l varsa ekrana da gÃ¶ster
                    if (registryAudit.Unregistered.Count > 0)
                    {
                        string ghostList = string.Join(", ",
                            registryAudit.Unregistered.Select(e => e.Name));
                        TaleWorlds.Library.InformationManager.DisplayMessage(
                            new TaleWorlds.Library.InformationMessage(
                                $"[BanditMilitias] âŒ {registryAudit.Unregistered.Count} kayÄ±tsÄ±z modÃ¼l: {ghostList}",
                                TaleWorlds.Library.Colors.Red));
                    }
                }

                // â”€â”€ KRÄ°TÄ°K: AÄŸÄ±r sistem baÅŸlatma ertelendi â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                // InitializeCoreSystems, InitializeAISystems, InitializeStrategicSystems,
                // InitializeBrainSystem artÄ±k OnGameStart'ta Ã§alÄ±ÅŸmÄ±yor.
                // Bu sistemler Campaign verilerine (Settlement.All, WarlordSystem vb.) eriÅŸiyor
                // ve oyun dÃ¼nyasÄ± henÃ¼z yÃ¼klenmeden bu verilere eriÅŸmek CRASH'e neden oluyordu.
                // TÃ¼m aÄŸÄ±r baÅŸlatma MilitiaBehavior.OnSessionLaunched'a taÅŸÄ±ndÄ±.
                // Mod, haritaya girildikten 2 oyun gÃ¼nÃ¼ sonra tamamen aktif olacak.
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
                        $"gameStarter '{gameStarter.GetType().Name}' AddBehavior desteÃ°i sunmuyor. " +
                        "DavranÃ½Ã¾lar kaydedilemedi  mod sÃ½nÃ½rlÃ½ modda Ã§alÃ½Ã¾acak.");
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
                    DisplayInfo($"[BanditMilitias] Temel yÃ¼kleme tamamlandÄ± ({timer.Elapsed.TotalMilliseconds:F2}ms) â€” sistemler harita yÃ¼klenince baÅŸlayacak", Colors.Cyan);
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

                RegisterSafe(() => Core.Memory.WorldMemory.Instance, nameof(Core.Memory.WorldMemory), critical: true);
                RegisterSafe(() => Intelligence.AI.Components.StaticDataCache.Instance, nameof(Intelligence.AI.Components.StaticDataCache), critical: true);
                RegisterSafe(() => Systems.Grid.SpatialGridSystem.Instance, nameof(Systems.Grid.SpatialGridSystem), critical: true);
                RegisterSafe(() => Intelligence.Swarm.SwarmCoordinator.Instance, nameof(Intelligence.Swarm.SwarmCoordinator), critical: true);
                RegisterSafe(() => new Systems.Spawning.MilitiaSpawningSystem(), nameof(Systems.Spawning.MilitiaSpawningSystem), critical: true);

                RegisterSafe(() => Systems.Fear.FearSystem.Instance, nameof(Systems.Fear.FearSystem), critical: true);
                RegisterSafe(() => Systems.Progression.WarlordLegitimacySystem.Instance, nameof(Systems.Progression.WarlordLegitimacySystem), critical: true);
                RegisterSafe(() => Systems.Progression.MilitiaProgressionSystem.Instance, nameof(Systems.Progression.MilitiaProgressionSystem), critical: true);
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

                // â”€â”€ Yeni Sistemler (v5.1) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                RegisterSafe(() => Systems.Economy.CaravanTaxSystem.Instance, nameof(Systems.Economy.CaravanTaxSystem));
                RegisterSafe(() => Systems.Seasonal.SeasonalEffectsSystem.Instance, nameof(Systems.Seasonal.SeasonalEffectsSystem));
                RegisterSafe(() => Systems.Progression.WarlordSuccessionSystem.Instance, nameof(Systems.Progression.WarlordSuccessionSystem));
                RegisterSafe(() => Systems.Combat.MilitiaMoraleSystem.Instance, nameof(Systems.Combat.MilitiaMoraleSystem));
                // BUG FIX v5.2: Assertion ve oto-dÃ¼zeltme katmanÄ± eklendi
                RegisterSafe(() => Systems.Diagnostics.MilitiaAssertionSystem.Instance, nameof(Systems.Diagnostics.MilitiaAssertionSystem));
                // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

                // â”€â”€ Eksik 6 sistem kaydÄ± â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                RegisterSafe(() => Systems.Progression.WarlordCareerSystem.Instance, nameof(Systems.Progression.WarlordCareerSystem));
                RegisterSafe(() => Systems.Legacy.WarlordLegacySystem.Instance, nameof(Systems.Legacy.WarlordLegacySystem));
                RegisterSafe(() => Systems.Territory.TerritorySystem.Instance, nameof(Systems.Territory.TerritorySystem));
                // WarlordCombatSystem â†’ MissionBehavior, OnMissionBehaviorInitialize'da ekleniyor
                // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

                RegisterSafe(() => Intelligence.Strategic.BanditBrain.Instance, nameof(Intelligence.Strategic.BanditBrain), critical: true);
                RegisterSafe(() => Intelligence.Strategic.WarlordSystem.Instance, nameof(Intelligence.Strategic.WarlordSystem), critical: true);
                RegisterSafe(() => Intelligence.Narrative.WarlordNarrativeSystem.Instance, nameof(Intelligence.Narrative.WarlordNarrativeSystem));
                RegisterSafe(() => Systems.Tracking.PlayerTracker.Instance, nameof(Systems.Tracking.PlayerTracker));
                RegisterSafe(() => Core.Neural.NervousSystem.Instance, nameof(Core.Neural.NervousSystem));
                RegisterSafe(() => Systems.Crisis.CrisisEventSystem.Instance, nameof(Systems.Crisis.CrisisEventSystem));

                // Developer telemetry: sadece DevMode/TestingMode oturumlarinda yuklenir.
                bool isTestingMode = false;
                try { isTestingMode = Settings.Instance?.TestingMode == true; }
                catch (Exception ex) { DebugLogger.Warning("SubModule", $"Settings check in RegisterModules failed: {ex.Message}"); }

                bool isDevMode = false;
                try { isDevMode = Settings.Instance?.DevMode == true; }
                catch (Exception ex) { DebugLogger.Warning("SubModule", $"DevMode check in RegisterModules failed: {ex.Message}"); }

                // Developer telemetry: normal oyuncu oturumunda yuklemeyelim.
                // Test/Dev modunda yuklenir, veri toplama davranisi korunur.
                if (isDevMode || isTestingMode)
                {
                    RegisterSafe(() => Systems.Dev.DevDataCollector.Instance, nameof(Systems.Dev.DevDataCollector));
                }
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

        public override void OnGameEnd(Game game)
        {
            base.OnGameEnd(game);
            Infrastructure.FileLogger.LogSection("OnGameEnd");
            
            try
            {
                Infrastructure.ModuleManager.Instance.OnSessionEnd();
                Infrastructure.FileLogger.Log("ModuleManager.OnSessionEnd: done");
            }
            catch (Exception ex) { DebugLogger.Warning("SubModule", $"ModuleManager cleanup failed: {ex.Message}"); }

            try
            {
                Intelligence.AI.CustomMilitiaAI.Cleanup();
                Infrastructure.FileLogger.Log("CustomMilitiaAI.Cleanup: done");
            }
            catch (Exception ex) { DebugLogger.Warning("SubModule", $"CustomMilitiaAI cleanup failed: {ex.Message}"); }

            try
            {
                Core.Neural.NeuralEventRouter.Instance.Reset();
                Infrastructure.FileLogger.Log("NeuralEventRouter.Reset: done");
            }
            catch (Exception ex) { DebugLogger.Warning("SubModule", $"NeuralEventRouter cleanup failed: {ex.Message}"); }

            try
            {
                Systems.Spawning.MilitiaNameGenerator.ClearCache();
                Infrastructure.FileLogger.Log("MilitiaNameGenerator.ClearCache: done");
            }
            catch (Exception ex) { DebugLogger.Warning("SubModule", $"MilitiaNameGenerator cleanup failed: {ex.Message}"); }

            try
            {
                Systems.Grid.CampaignGridSystem.ResetCache();
                Infrastructure.FileLogger.Log("CampaignGridSystem.ResetCache: done");
            }
            catch (Exception ex) { DebugLogger.Warning("SubModule", $"CampaignGridSystem cleanup failed: {ex.Message}"); }

            try
            {
                if (_eventBusEnabled) Core.Events.EventBus.Instance.Clear();
                Infrastructure.FileLogger.Log("EventBus.Clear: done");
            }
            catch (Exception ex) { DebugLogger.Warning("SubModule", $"EventBus cleanup failed: {ex.Message}"); }

            try
            {
                BanditMilitias.Core.Config.Globals.Reset();
                Infrastructure.FileLogger.Log("Globals.Reset: done");
            }
            catch (Exception ex) { DebugLogger.Warning("SubModule", $"Globals cleanup failed: {ex.Message}"); }

            try
            {
                BanditMilitias.Systems.Combat.MilitiaVictorySystem.Reset();
                Infrastructure.FileLogger.Log("MilitiaVictorySystem.Reset: done");
            }
            catch (Exception ex) { DebugLogger.Warning("SubModule", $"MilitiaVictorySystem cleanup failed: {ex.Message}"); }

            try
            {
                BanditMilitias.Debug.DebugPanel.Reset();
                Infrastructure.FileLogger.Log("DebugPanel.Reset: done");
            }
            catch (Exception ex) { DebugLogger.Warning("SubModule", $"DebugPanel cleanup failed: {ex.Message}"); }

            _deferredInitDone = false;
            TransitionToState(ModState.Ready);
            Infrastructure.FileLogger.Log("OnGameEnd: state reset to Ready");
        }

        protected override void OnSubModuleUnloaded()
        {
            Infrastructure.FileLogger.LogSection("OnSubModuleUnloaded");
            
            try
            {
                if (_harmonyPatched && _harmony != null)
                {
                    _harmony.UnpatchAll(HarmonyId);
                    _harmonyPatched = false;
                    Infrastructure.FileLogger.Log("Harmony patches unapplied");
                }
            }
            catch (Exception ex) { Infrastructure.FileLogger.LogError($"Harmony unpatching failed: {ex.Message}"); }

            try { Core.Events.EventBus.Instance.Clear(); } catch (Exception ex) { Infrastructure.FileLogger.LogWarning($"EventBus clear failed: {ex.Message}"); }
            
            Infrastructure.FileLogger.Log("OnSubModuleUnloaded: complete");
            base.OnSubModuleUnloaded();
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
        /// OnGameStart'tan ertelenen aÄŸÄ±r sistem baÅŸlatmalarÄ±nÄ± Ã§alÄ±ÅŸtÄ±rÄ±r.
        /// MilitiaBehavior.OnSessionLaunched tarafÄ±ndan Ã§aÄŸrÄ±lÄ±r â€” Campaign verisi hazÄ±r olduÄŸunda.
        /// </summary>
        public static bool RunDeferredSystemInit()
        {
            if (_deferredInitDone) return true;

            Infrastructure.FileLogger.Log("RunDeferredSystemInit: begin");
            var timer = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Sistem saÄŸlÄ±k kontrolÃ¼
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
                    DisplayInfo($"[BanditMilitias] Sistemler baÅŸlatÄ±ldÄ± ({timer.Elapsed.TotalMilliseconds:F2}ms)", Colors.Green);
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

            static bool TryCreateBehaviorRegistrar(IGameStarter starter, out Action<CampaignBehaviorBase>? register)
            {
                register = null;

                if (starter is CampaignGameStarter campaignStarter)
                {
                    register = campaignStarter.AddBehavior;
                    return true;
                }

                var addBehaviorMethod = starter.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m =>
                    {
                        if (!string.Equals(m.Name, "AddBehavior", StringComparison.Ordinal)) return false;

                        var parameters = m.GetParameters();
                        return parameters.Length == 1
                               && parameters[0].ParameterType.IsAssignableFrom(typeof(CampaignBehaviorBase));
                    });

                if (addBehaviorMethod == null) return false;

                register = behavior => _ = addBehaviorMethod.Invoke(starter, new object[] { behavior });
                return true;
            }

            try
            {
                if (!TryCreateBehaviorRegistrar(gameStarter, out var registerBehavior) || registerBehavior == null)
                {
                    Infrastructure.FileLogger.LogError(
                        $"RegisterCampaignBehaviors: AddBehavior method not found on {gameStarter.GetType().FullName}");
                    return false;
                }

                AddBehaviorSafe(registerBehavior, new Behaviors.MilitiaBehavior(), nameof(Behaviors.MilitiaBehavior));
                AddBehaviorSafe(registerBehavior, new Behaviors.MilitiaDiplomacyCampaignBehavior(), nameof(Behaviors.MilitiaDiplomacyCampaignBehavior));
                AddBehaviorSafe(registerBehavior, new Behaviors.WarlordCampaignBehavior(), nameof(Behaviors.WarlordCampaignBehavior));
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

                // BUG-CRASH-5 DÃœZELTMESÄ°: _ascendedCaptains statik HashSet yÃ¼kleme sonrasÄ±
                // sÄ±fÄ±rlanmÄ±yordu; kaptan ikinci kez yÃ¼kselme tetikleyebiliyordu.
                Systems.Combat.MilitiaVictorySystem.Reset();

                if (_aiSystemEnabled)
                {
                    Intelligence.AI.CustomMilitiaAI.Initialize();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("SubModule", $"OnGameLoaded maintenance failed: {ex.Message}");
            }
        }

        private static void TransitionToState(ModState newState)
        {
            lock (_stateLock)
            {
                if (_currentState == ModState.EmergencyStop && newState != ModState.Uninitialized)
                {
                    Infrastructure.FileLogger.LogWarning($"[SubModule] Rejected state transition: {_currentState} -> {newState} (EMERGENCY_STOP is final)");
                    return;
                }

                Infrastructure.FileLogger.Log($"[SubModule] State Transition: {_currentState} -> {newState}");
                _currentState = newState;
            }
        }

        private static void HandleCriticalError(string context, Exception ex)
        {
            Infrastructure.FileLogger.LogError($"[CRITICAL ERROR] {context}: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
            DebugLogger.Error("SubModule", $"Critical Error in {context}: {ex.Message}");
        }

        private static void EnterEmergencyStop(string reason, Exception? ex = null)
        {
            TransitionToState(ModState.EmergencyStop);
            Infrastructure.FileLogger.LogError($"[EMERGENCY STOP] Reason: {reason}");
            if (ex != null) Infrastructure.FileLogger.LogError($"Exception: {ex}");
            
            DisplayError($"[BanditMilitias] EMERGENCY STOP: {reason}. Mod disabled to prevent save corruption.");
        }

        private static void DisplayInfo(string message, Color? color = null)
        {
            InformationManager.DisplayMessage(new InformationMessage(message, color ?? Colors.White));
        }

        private static void DisplayWarning(string message)
        {
            InformationManager.DisplayMessage(new InformationMessage(message, Colors.Yellow));
        }

        private static void DisplayError(string message)
        {
            InformationManager.DisplayMessage(new InformationMessage(message, Colors.Red));
        }

        private static void DisplaySystemStatus()
        {
            string status = $"[BanditMilitias] Status: {_currentState} | AI:{(_aiSystemEnabled ? "ON" : "OFF")} | Warlord:{(_warlordSystemEnabled ? "ON" : "OFF")}";
            DisplayInfo(status, _currentState == ModState.Active ? Colors.Green : Colors.Yellow);
        }

        public static string GetDiagnostics()
        {
            return $"SubModule State: {_currentState} | DeferredInit={_deferredInitDone} | " +
                   $"AI={_aiSystemEnabled} | EventBus={_eventBusEnabled} | Warlord={_warlordSystemEnabled} | Brain={_brainSystemEnabled}";
        }

        private void ValidateSettings()
        {
            try
            {
                if (Settings.Instance == null)
                {
                    Infrastructure.FileLogger.LogWarning("Settings.Instance is null. Using defaults.");
                    return;
                }
                Settings.Instance.ValidateAndClampSettings();
            }
            catch (Exception ex)
            {
                Infrastructure.FileLogger.LogError($"Settings validation failed: {ex.Message}");
            }
        }

        private bool ApplyHarmonyPatches()
        {
            if (_harmonyPatched) return true;

            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(typeof(SubModule).Assembly);
                _harmonyPatched = true;
                Infrastructure.FileLogger.Log("Harmony patches applied successfully");
                return true;
            }
            catch (Exception ex)
            {
                Infrastructure.FileLogger.LogError($"Harmony patching failed: {ex}");
                DebugLogger.Error("SubModule", $"Harmony patching failed: {ex.Message}");
                return false;
            }
        }

        private void ConfigureTestMode()
        {
            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("SubModule", "TestingMode enabled - enhanced diagnostics active");
            }
        }
    }
}


