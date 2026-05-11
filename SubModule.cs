using BanditMilitias.Lifecycle;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BanditMilitias
{
    public class SubModule : MBSubModuleBase
    {
        public static readonly string ModuleId = "BanditMilitias";
        public static readonly System.Version ModVersion = new(1, 3, 15);

        private static ModLifecycleManager Lifecycle => ModLifecycleManager.Instance;
        private static BanditMilitias.Boot.WarlordPowerController Grid => BanditMilitias.Boot.WarlordPowerController.Instance;

        public static bool IsDeferredInitDone => Grid.IsEnergized || Lifecycle.DeferredInitDone;
        public static void SetStateDormant() => Lifecycle.SetStateDormant();
        public static void SetStateActive() => Lifecycle.SetStateActive();
        public static bool IsSandboxMode => Lifecycle.IsSandboxMode;
        public static bool IsLoadedSaveSession => Lifecycle.IsLoadedSaveSession;

        // Degraded mode recovery heartbeat counter (counts application frames)
        private int _degradedTickCounter;
        private const int DegradedRecoveryIntervalTicks = 600; // ~10 s at 60 fps

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            // --- POWER GRID: STAGE 1 (WIRING) ---
            Grid.PowerOn(typeof(SubModule).Assembly);
            
            // Sync with legacy lifecycle for UI/compat
            Lifecycle.OnSubModuleLoad(typeof(SubModule).Assembly);
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarter)
        {
            base.OnGameStart(game, gameStarter);
            // --- POWER GRID: STAGE 2 (GROUNDING) ---
            Grid.ConnectLoad(game, gameStarter);
            
            // Sync with legacy lifecycle
            Lifecycle.OnGameStart(game, gameStarter);
        }

        public override void OnGameLoaded(Game game, object initDataObject)
        {
            base.OnGameLoaded(game, initDataObject);
            // --- POWER GRID: STAGE 3 (ENERGIZING) ---
            Grid.Energize();
            
            // Sync with legacy lifecycle
            Lifecycle.OnGameLoaded(game);
        }

        public override void OnGameEnd(Game game)
        {
            base.OnGameEnd(game);
            // --- POWER GRID: SHUTDOWN ---
            Grid.Shutdown();
            
            Lifecycle.OnGameEnd();
        }

        protected override void OnSubModuleUnloaded()
        {
            Grid.Shutdown();
            Lifecycle.OnSubModuleUnloaded();
            base.OnSubModuleUnloaded();
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);

            var state = Lifecycle.CurrentState;

            // ── Degraded mode: minimal, safe tick with self-healing heartbeat ──────
            if (state == ModState.Degraded)
            {
                // Drain the EventBus queue safely to prevent memory / queue overflow
                // We clear (not process) to avoid running handlers that may be broken
                try
                {
                    Core.Events.EventBus.Instance?.ClearQueue();
                }
                catch { /* must never crash the game tick */ }

                // Recovery heartbeat: every N frames, attempt to heal out of Degraded
                _degradedTickCounter++;
                if (_degradedTickCounter >= DegradedRecoveryIntervalTicks)
                {
                    _degradedTickCounter = 0;
                    try
                    {
                        Lifecycle.TryRecoverFromDegraded();
                    }
                    catch (System.Exception ex)
                    {
                        TaleWorlds.Library.Debug.Print(
                            $"[BanditMilitias] DegradedRecovery heartbeat error: {ex.Message}");
                    }
                }
                return;
            }

            // ── All other non-Active states: do nothing ───────────────────────────
            if (state != ModState.Active) return;

            // ── Active mode: full tick ────────────────────────────────────────────
            _degradedTickCounter = 0; // reset counter when healthy
            try
            {
                if (Lifecycle.EventBusEnabled)
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

                BanditMilitias.Debug.DebugPanel.Instance?.Update();
            }
            catch (System.Exception ex)
            {
                // Just log it or fallback to avoiding crash
                TaleWorlds.Library.Debug.Print($"[BanditMilitias] Tick Error: {ex.Message}");
            }
        }

        public static bool RunDeferredSystemInit()
        {
            return Lifecycle.RunDeferredSystemInit();
        }

        public static string GetDiagnostics()
        {
            return Lifecycle.GetDiagnostics();
        }
    }
}
