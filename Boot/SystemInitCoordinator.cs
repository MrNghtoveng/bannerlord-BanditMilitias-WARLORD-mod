using BanditMilitias.Debug;
using BanditMilitias.Diagnostics;
using BanditMilitias.Infrastructure;
using System;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;

namespace BanditMilitias.Boot
{
    public sealed class SystemInitCoordinator
    {
        private readonly BootWatchdog _bootWatchdog;

        public SystemInitCoordinator(BootWatchdog bootWatchdog)
        {
            _bootWatchdog = bootWatchdog;
        }

        public bool InitializeInfrastructure(out bool eventBusEnabled)
        {
            eventBusEnabled = true;

            try
            {
                Core.Events.EventBus.Instance.ResetForSessionEnd();
            }
            catch (Exception ex)
            {
                DebugLogger.Error("SubModule", $"EventBus initialization failed: {ex.Message}");
                eventBusEnabled = false;
            }

            return true;
        }

        public void RegisterGameModels(IGameStarter gameStarter)
        {
            static bool AddModelSafe(IGameStarter starter, GameModel model, string modelName)
            {
                try
                {
                    FileLogger.Log($"RegisterGameModel: {modelName}");
                    starter.AddModel(model);
                    FileLogger.Log($"Registered game model: {modelName}");
                    return true;
                }
                catch (Exception ex)
                {
                    FileLogger.LogError($"RegisterGameModel failed for {modelName}: {ex}");
                    DebugLogger.Error("SubModule", $"Model registration failed for {modelName}: {ex.Message}");
                    return false;
                }
            }

            _ = AddModelSafe(gameStarter, new Models.ModBanditDensityModel(), nameof(Models.ModBanditDensityModel));
            _ = AddModelSafe(gameStarter, new Models.MilitiaSpeedModel(), nameof(Models.MilitiaSpeedModel));
            _ = AddModelSafe(gameStarter, new Models.ModPartySizeLimitModel(), nameof(Models.ModPartySizeLimitModel));
            _ = AddModelSafe(gameStarter, new Models.MilitiaVisibilityModel(), nameof(Models.MilitiaVisibilityModel));
        }

        public bool RegisterCampaignBehaviors(IGameStarter gameStarter)
        {
            static bool AddBehaviorSafe(Action<CampaignBehaviorBase> register, CampaignBehaviorBase behavior, string behaviorName)
            {
                try
                {
                    FileLogger.Log($"RegisterCampaignBehavior: {behaviorName}");
                    register(behavior);
                    FileLogger.Log($"Registered campaign behavior: {behaviorName}");
                    return true;
                }
                catch (Exception ex)
                {
                    FileLogger.LogError($"RegisterCampaignBehavior failed for {behaviorName}: {ex}");
                    DebugLogger.Error("SubModule", $"Behavior registration failed for {behaviorName}: {ex.Message}");
                    return false;
                }
            }

            if (gameStarter is not CampaignGameStarter campaignStarter)
            {
                FileLogger.LogError(
                    $"RegisterCampaignBehaviors: AddBehavior method not found on {gameStarter.GetType().FullName}");
                return false;
            }

            bool allRegistered = true;
            allRegistered &= AddBehaviorSafe(campaignStarter.AddBehavior, new Behaviors.MilitiaBehavior(), nameof(Behaviors.MilitiaBehavior));
            allRegistered &= AddBehaviorSafe(campaignStarter.AddBehavior, new Behaviors.MilitiaDiplomacyCampaignBehavior(), nameof(Behaviors.MilitiaDiplomacyCampaignBehavior));
            allRegistered &= AddBehaviorSafe(campaignStarter.AddBehavior, new Behaviors.WarlordCampaignBehavior(), nameof(Behaviors.WarlordCampaignBehavior));
            return allRegistered;
        }

        public bool InitializeCoreSystems()
        {
            bool allReady = true;

            try
            {
                Systems.Spawning.DynamicHideoutSystem.Instance.Initialize();
            }
            catch (Exception ex)
            {
                allReady = false;
                DebugLogger.Warning("SubModule", $"DynamicHideoutSystem initialization deferred: {ex.Message}");
            }

            try
            {
                Systems.Spawning.HardcoreDynamicHideoutSystem.Instance.Initialize();
            }
            catch (Exception ex)
            {
                allReady = false;
                DebugLogger.Warning("SubModule", $"HardcoreDynamicHideoutSystem initialization deferred: {ex.Message}");
            }

            if (!allReady)
            {
                UiNotifier.TryShow(
                    "One or more hideout systems were deferred; retry will run on session launch.",
                    TaleWorlds.Library.Colors.Yellow,
                    "SystemInitCoordinator");
            }

            return allReady;
        }

        public bool InitializeAISystems()
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

        public bool InitializeStrategicSystems()
        {
            try
            {
                if (Settings.Instance?.EnableWarlords == true && Settings.Instance?.TestingMode == true)
                {
                    int warlordCount = Intelligence.Strategic.WarlordSystem.Instance.GetAllWarlords().Count;
                    DebugLogger.Info("SubModule", $"WarlordSystem active: {warlordCount} warlords");
                }

                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Error("SubModule", $"Strategic systems initialization failed: {ex.Message}");
                return false;
            }
        }

        public bool InitializeBrainSystem()
        {
            try
            {
                var brainModule = ModuleManager.Instance.GetModule<Intelligence.Strategic.BanditBrain>();

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

        public bool RunDeferredSystemInit(Action<string, TaleWorlds.Library.Color?> info, Action displayStatus, ref bool aiSystemEnabled, ref bool warlordSystemEnabled, ref bool brainSystemEnabled)
        {
            FileLogger.Log("RunDeferredSystemInit: begin");
            var timer = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (Settings.Instance?.TestingMode == true)
                {
                    try
                    {
                        var health = HealthCheck.RunDiagnostics(autoFix: true);
                        FileLogger.Log($"[HealthCheck] {health}");
                        HealthCheck.DisplayReport(health);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.LogWarning($"[HealthCheck] skipped: {ex.Message}");
                    }
                }

                FileLogger.Log("InitializeCoreSystems: begin");
                bool coreOk = InitializeCoreSystems();
                FileLogger.Log($"InitializeCoreSystems: {(coreOk ? "OK" : "FAILED")}");

                FileLogger.Log("InitializeAISystems: begin");
                aiSystemEnabled = InitializeAISystems();
                FileLogger.Log($"InitializeAISystems: {(aiSystemEnabled ? "OK" : "FAILED")}");

                FileLogger.Log("InitializeStrategicSystems: begin");
                warlordSystemEnabled = InitializeStrategicSystems();
                FileLogger.Log($"InitializeStrategicSystems: {(warlordSystemEnabled ? "OK" : "FAILED")}");


                FileLogger.Log("InitializeBrainSystem: begin");
                brainSystemEnabled = InitializeBrainSystem();
                FileLogger.Log($"InitializeBrainSystem: {(brainSystemEnabled ? "OK" : "FAILED")}");

                timer.Stop();
                FileLogger.Log($"RunDeferredSystemInit: done in {timer.Elapsed.TotalMilliseconds:F2}ms");

                if (Settings.Instance?.TestingMode == true)
                {
                    info($"[BanditMilitias] Systems initialized ({timer.Elapsed.TotalMilliseconds:F2}ms)", TaleWorlds.Library.Colors.Green);
                    displayStatus();
                }

                return coreOk;
            }
            catch (Exception ex)
            {
                FileLogger.LogError($"RunDeferredSystemInit failed: {ex.Message}");
                DebugLogger.Error("SubModule", $"RunDeferredSystemInit failed: {ex}");
                return false;
            }
        }
    }
}
