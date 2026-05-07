using BanditMilitias.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Systems.Combat;
using BanditMilitias.Systems.Enhancement;
using BanditMilitias.Systems.Scheduling;
using BanditMilitias.Core.Neural;
using BanditMilitias.Intelligence.Strategic;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Behaviors
{
    public class MilitiaBehavior : CampaignBehaviorBase
    {

        private readonly Dictionary<string, CampaignTime> _hideoutCooldowns = new Dictionary<string, CampaignTime>();
        private readonly Dictionary<string, CampaignTime> _hideoutFailureCooldowns = new Dictionary<string, CampaignTime>();
        private readonly Dictionary<string, CampaignTime> _recentHideoutClearEvents = new Dictionary<string, CampaignTime>();

        private bool _isModActive = true;
        private int _totalTicks = 0;
        private float _currentLoad = 0f;
        private const int CurrentSaveVersion = Infrastructure.BanditMilitiasSaveDefiner.SAVE_VERSION;
        
        // ══════════════════════════════════════════════════════════════════════
        // State Persistence & Mapping
        // ══════════════════════════════════════════════════════════════════════
        private Dictionary<string, MilitiaData> _persistentData = new();
        private Dictionary<MobileParty, MilitiaPartyComponent> _runtimeMap = new();

        public static MilitiaBehavior? Instance { get; private set; }

        public MilitiaBehavior()
        {
            Instance = this;
        }

        public MilitiaPartyComponent? GetMilitiaComponent(MobileParty? party)
        {
            if (party == null) return null;
            return _runtimeMap.TryGetValue(party, out var component) ? component : null;
        }

        public void RegisterMilitia(MobileParty party, MilitiaPartyComponent component)
        {
            if (party == null || component == null) return;
            _runtimeMap[party] = component;
        }

        public void UnregisterMilitia(MobileParty party)
        {
            if (party == null) return;
            _runtimeMap.Remove(party);
        }

        private int _saveVersion = CurrentSaveVersion;

        private int _sessionLaunchRetries = 0;


        private const int MAX_SESSION_RETRIES = 30;
        private bool _lazyInitScheduled = false;
        private bool _sessionBootstrapPending = false;
        private bool _sessionBootstrapCompleted = false;
        private bool _activationDelayPendingLogged = false;
        private bool _lazyInitFailureNotified = false;
        private double _savedActivationDelayStartHours = -1d;
        private bool _savedActivationSwitchClosed = false;
        private readonly HashSet<MobileParty> _partiesBeingDestroyed = new HashSet<MobileParty>();
        private bool _hideoutCacheRefreshPending = false;

        public bool IsHideoutOnCooldown(Settlement hideout)
        {
            if (hideout == null) return false;
            if (_hideoutCooldowns.TryGetValue(hideout.StringId, out var cooldownEnd))
                return CampaignTime.Now < cooldownEnd;
            if (_hideoutFailureCooldowns.TryGetValue(hideout.StringId, out var failureCooldownEnd))
                return CampaignTime.Now < failureCooldownEnd;
            return false;
        }

        public override void RegisterEvents()
        {
            UnregisterEvents();

            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);

            CampaignEvents.MobilePartyCreated.AddNonSerializedListener(this, OnPartyCreated);
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnPartyDestroyed);

            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, OnSettlementOwnerChanged);

            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);

            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);

            CampaignEvents.AiHourlyTickEvent.AddNonSerializedListener(this, OnAiHourlyTick);
        }

        private void UnregisterEvents()
        {
            try
            {
                MbEventExtensions.RemoveListenerSafe(CampaignEvents.DailyTickEvent, this, OnDailyTick);
                MbEventExtensions.RemoveListenerSafe(CampaignEvents.HourlyTickEvent, this, OnHourlyTick);
                MbEventExtensions.RemoveListenerSafe(CampaignEvents.TickEvent, this, OnTick);

                MbEventExtensions.RemoveListenerSafe(CampaignEvents.MobilePartyCreated, this, OnPartyCreated);
                MbEventExtensions.RemoveListenerSafe(CampaignEvents.MobilePartyDestroyed, this, OnPartyDestroyed);

                MbEventExtensions.RemoveListenerSafe(CampaignEvents.OnSettlementOwnerChangedEvent, this,
                    OnSettlementOwnerChanged);

                MbEventExtensions.RemoveListenerSafe(CampaignEvents.MapEventEnded, this, OnMapEventEnded);

                MbEventExtensions.RemoveListenerSafe(CampaignEvents.OnSessionLaunchedEvent, this, OnSessionLaunched);

                MbEventExtensions.RemoveListenerSafe(CampaignEvents.AiHourlyTickEvent, this, (Action<MobileParty, PartyThinkParams>)OnAiHourlyTick);

                if (_lazyInitScheduled)
                {
                    try
                    {
                        CampaignEvents.DailyTickEvent.RemoveNonSerializedListener(this, TryLazyInitialization);
                    }
                    catch (OutOfMemoryException) { throw; }
                    catch (Exception ex)
                    {
                        DebugLogger.Warning("MilitiaBehavior", $"Lazy init listener removal failed: {ex.Message}");
                    }
                    _lazyInitScheduled = false;
                    _sessionLaunchRetries = 0;
                }

                _sessionBootstrapPending = false;
                _sessionBootstrapCompleted = false;
                _activationDelayPendingLogged = false;
                _lazyInitFailureNotified = false;
                _partiesBeingDestroyed.Clear();
                _hideoutCacheRefreshPending = false;
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex)
            {
                DebugLogger.Warning("MilitiaBehavior", $"UnregisterEvents error: {ex.Message}");
            }
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            try
            {
                var moduleManager = Infrastructure.ModuleManager.Instance;
                bool isLoadedSaveSession = SubModule.IsLoadedSaveSession;

                // Always start the activation-delay clock, regardless of whether delay is active.
                // Without this the clock never begins and the switch never closes.
                ModActivationManager.TryStartActivationDelayClock();

                // ── Infrastructure that must run even when activation delay is active ──────
                // Caches and globals must be populated NOW so that when the delay expires
                // and TryCompleteSessionBootstrap fires, all data is already ready.
                try
                {
                    moduleManager.RebuildCaches();
                    DebugLogger.Info("MilitiaBehavior", "Session launched, initial cache rebuild attempted.");
                }
                catch (OutOfMemoryException) { throw; }
                catch (Exception ex)
                {
                    DebugLogger.Warning("MilitiaBehavior", $"Initial cache rebuild failed: {ex.Message}");
                }

                try
                {
                    Core.Config.Globals.Initialize(force: true);
                    Infrastructure.ClanCache.Reset();
                    Infrastructure.ClanCache.Initialize();
                }
                catch (OutOfMemoryException) { throw; }
                catch (Exception ex)
                {
                    DebugLogger.Warning("MilitiaBehavior", $"Static data initialization failed: {ex.Message}");
                }

                if (isLoadedSaveSession)
                {
                    ReIdentifyMilitias();
                }

                try
                {
                    _ = SubModule.RunDeferredSystemInit();
                    DebugLogger.Info("MilitiaBehavior", "Deferred system init completed. Mod state: Dormant");
                    SubModule.SetStateDormant();
                }
                catch (OutOfMemoryException) { throw; }
                catch (Exception ex)
                {
                    DebugLogger.Warning("MilitiaBehavior", $"Deferred system init failed: {ex.Message}");
                    // SetStateDormant must still be called so the activation-delay timer can
                    // start. Without it the mod stays stuck and never transitions to Active.
                    try { SubModule.SetStateDormant(); } catch { }
                }

                try
                {
                    var healthReport = Infrastructure.HealthCheck.RunDiagnostics(autoFix: true);
                    Infrastructure.HealthCheck.DisplayReport(healthReport);
                    Infrastructure.TroopRosterPool.Clear();
                    Intelligence.AI.PatrolDetection.RefreshPatrolCache();
                    Systems.Grid.SpatialGridSystem.Instance.OnSessionStart();
                    DebugLogger.Info("MilitiaBehavior", "Infrastructure systems initialized (Patrol, Grid, Pool).");
                }
                catch (OutOfMemoryException) { throw; }
                catch (Exception ex)
                {
                    DebugLogger.Warning("MilitiaBehavior", $"Infrastructure init failed: {ex.Message}");
                }

                bool cachesReady = moduleManager.HideoutCache.Count > 0 && Core.Config.Globals.BasicInfantry.Count > 0;
                bool gameReady = ModActivationManager.IsGameFullyInitialized();

                if ((!cachesReady || !gameReady) && !_lazyInitScheduled)
                {
                    _lazyInitScheduled = true;
                    _sessionLaunchRetries = 0;
                    DebugLogger.Warning("MilitiaBehavior", "Caches not ready. Scheduling lazy initialization.");
                    ScheduleLazyInitialization();
                }

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Info("MilitiaBehavior",
                        $"Session source: {(isLoadedSaveSession ? "LoadedSave" : "NewSession")} | CachesReady={cachesReady} | GameReady={gameReady}");
                }

                // If activation delay is still active, log and defer bootstrap.
                // All infrastructure above is already initialised; we just wait for the switch.
                if (ModActivationManager.IsGameplayActivationDelayed())
                {
                    if (!_activationDelayPendingLogged)
                    {
                        _activationDelayPendingLogged = true;
                        int requiredDays = Settings.Instance?.ActivationDelay ?? 2;
                        DebugLogger.Info("MilitiaBehavior",
                            $"Activation delay active ({requiredDays} days). Caches populated; bootstrap deferred.");
                        Infrastructure.FileLogger.Log(
                            $"Activation delay active: caches ready={cachesReady}, gameReady={gameReady}. Bootstrap deferred.");
                    }
                    // Bootstrap will fire through TryCompleteSessionBootstrapIfReady on the hourly tick.
                }
                else if (cachesReady && gameReady)
                {
                    TryCompleteSessionBootstrap(moduleManager, cachesReady, gameReady);
                }

                DebugLogger.Info("MilitiaBehavior", "Session launch bootstrap sequence ended.");
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex)
            {
                DebugLogger.Error("MilitiaBehavior", $"OnSessionLaunched failed: {ex}");
                try { FileLogger.LogError($"OnSessionLaunched failed: {ex}"); } catch {}
            }
        }

        private void ReIdentifyMilitias()
        {
            _runtimeMap.Clear();
            if (_persistentData == null || _persistentData.Count == 0) return;

            int reidentified = 0;
            var parties = Campaign.Current?.MobileParties;
            if (parties == null) return;

            foreach (var party in parties)
            {
                if (party?.StringId != null && _persistentData.TryGetValue(party.StringId, out var data))
                {
                    var home = Settlement.Find(data.HomeSettlementId);
                    if (home != null)
                    {
                        var component = new MilitiaPartyComponent(home);
                        component.LoadFromData(data);
                        RegisterMilitia(party, component);
                        reidentified++;
                    }
                }
            }
            DebugLogger.Info("MilitiaBehavior", $"Re-identified {reidentified} militias from persistent data.");
        }

        private void ScheduleLazyInitialization()
        {
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, TryLazyInitialization);
            DebugLogger.Info("MilitiaBehavior", "Lazy initialization scheduled on DailyTick.");
        }

        private void TryLazyInitialization()
        {
            if (_sessionLaunchRetries >= MAX_SESSION_RETRIES)
            {


                var mgr = Infrastructure.ModuleManager.Instance;
                int errHideoutCount = mgr.HideoutCache.Count;
                int errGlobalsCount = Core.Config.Globals.BasicInfantry.Count;
                bool errGameReady   = ModActivationManager.IsGameFullyInitialized();

                DebugLogger.Error("MilitiaBehavior",
                    $"Max lazy init retries ({MAX_SESSION_RETRIES}) reached — spawning may be disabled! " +
                    $"[Hideouts={errHideoutCount} {(errHideoutCount == 0 ? "<<EMPTY>>" : "OK")} | " +
                    $"Globals={errGlobalsCount} {(errGlobalsCount == 0 ? "<<EMPTY>>" : "OK")} | " +
                    $"GameReady={errGameReady} {(!errGameReady ? "<<NOT READY>>" : "OK")}]");

                HandleLazyInitializationFailure(errHideoutCount, errGlobalsCount, errGameReady);
                CampaignEvents.DailyTickEvent.RemoveNonSerializedListener(this, TryLazyInitialization);
                _lazyInitScheduled = false;
                return;
            }

            _sessionLaunchRetries++;

            var moduleManager = Infrastructure.ModuleManager.Instance;
            int hideoutCount = moduleManager.HideoutCache.Count;
            int globalsCount = Core.Config.Globals.BasicInfantry.Count;

            bool gameReady = ModActivationManager.IsGameFullyInitialized();

            DebugLogger.Info("MilitiaBehavior",
                $"Lazy init attempt {_sessionLaunchRetries}/{MAX_SESSION_RETRIES}: " +
                $"Hideouts={hideoutCount}, Globals={globalsCount}, GameReady={gameReady}");


            if (hideoutCount == 0 || globalsCount == 0 || !gameReady)
            {
                moduleManager.RebuildCaches();
                Core.Config.Globals.Initialize(force: true);
                Infrastructure.ClanCache.Initialize();

                hideoutCount = moduleManager.HideoutCache.Count;
                globalsCount = Core.Config.Globals.BasicInfantry.Count;
            }

            if (hideoutCount > 0 && globalsCount > 0 && gameReady)
            {
                DebugLogger.Info("MilitiaBehavior",
                    $"Lazy initialization SUCCESS after {_sessionLaunchRetries} attempts! " +
                    $"Hideouts={hideoutCount}, Globals={globalsCount}");

                TryCompleteSessionBootstrap(moduleManager, true, true);

                CampaignEvents.DailyTickEvent.RemoveNonSerializedListener(this, TryLazyInitialization);
                _lazyInitScheduled = false;
                _lazyInitFailureNotified = false;
            }
            else if (_sessionLaunchRetries >= MAX_SESSION_RETRIES)
            {
                DebugLogger.Error("MilitiaBehavior",
                    $"Lazy initialization FAILED after {MAX_SESSION_RETRIES} attempts! " +
                    $"Hideouts={hideoutCount}, Globals={globalsCount}. " +
                    "Militia spawning may not work correctly.");

                HandleLazyInitializationFailure(hideoutCount, globalsCount, gameReady);
                CampaignEvents.DailyTickEvent.RemoveNonSerializedListener(this, TryLazyInitialization);
                _lazyInitScheduled = false;
            }
        }

        private void HandleLazyInitializationFailure(int hideoutCount, int globalsCount, bool gameReady)
        {
            _sessionBootstrapPending = true;

            try
            {
                var healthReport = Infrastructure.HealthCheck.RunDiagnostics(autoFix: true);
                Infrastructure.HealthCheck.DisplayReport(healthReport);
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex)
            {
                DebugLogger.Warning("MilitiaBehavior", $"Lazy-init health diagnostics failed: {ex.Message}");
            }

            if (_lazyInitFailureNotified)
                return;

            _lazyInitFailureNotified = true;
            try
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[BanditMilitias] Session bootstrap delayed. Hideouts={hideoutCount}, Globals={globalsCount}, GameReady={gameReady}. System will retry in recovery mode.",
                    Colors.Yellow));
            }
            catch
            {
            }
        }

        private void TryCompleteSessionBootstrap(ModuleManager moduleManager, bool cachesReady, bool gameReady)
        {
            if (_sessionBootstrapCompleted)
            {
                return;
            }

            _sessionBootstrapPending = cachesReady && gameReady;
            if (!_sessionBootstrapPending)
            {
                return;
            }

            if (!ModActivationManager.IsGameplayActivationSwitchClosed())
            {
                // Activation delay is still counting — just wait; clock is already running.
                return;
            }

            try
            {
                if (!SubModule.IsDeferredInitDone)
                {
                    _ = SubModule.RunDeferredSystemInit();
                    Systems.Grid.SpatialGridSystem.Instance.OnSessionStart();
                    Intelligence.AI.PatrolDetection.RefreshPatrolCache();
                    Infrastructure.TroopRosterPool.Clear();
                }

                moduleManager.CompleteSessionBootstrap();
                _sessionBootstrapCompleted = true;
                _sessionBootstrapPending = false;
                _activationDelayPendingLogged = false;
                DebugLogger.Info("MilitiaBehavior", "Gameplay activation switch closed; session bootstrap completed. Mod state: Active");

                CompatibilityLayer.UpdateAllPartiesNextThinkTime(CampaignTime.Now);

                SubModule.SetStateActive();
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex)
            {
                DebugLogger.Warning("MilitiaBehavior", $"Session bootstrap completion failed: {ex.Message}");
                FileLogger.LogError($"Session bootstrap completion failed: {ex}");
                // Transition to Degraded so the mod shows a visible warning instead of silently doing nothing.
                SubModule.SetStateDormant();
                InformationManager.DisplayMessage(new InformationMessage(
                    "[BanditMilitias] Session bootstrap failed — mod is in degraded mode. Check log for details.",
                    Colors.Yellow));
            }
        }

        private void TryCompleteSessionBootstrapIfReady()
        {
            if (_sessionBootstrapCompleted || _lazyInitScheduled)
            {
                return;
            }

            var moduleManager = Infrastructure.ModuleManager.Instance;
            bool cachesReady = moduleManager.HideoutCache.Count > 0 && Core.Config.Globals.BasicInfantry.Count > 0;
            bool gameReady = ModActivationManager.IsGameFullyInitialized();

            if (!cachesReady || !gameReady)
            {
                try
                {
                    if (!_lazyInitScheduled || SubModule.IsLoadedSaveSession)
                        moduleManager.RebuildCaches();
                    Core.Config.Globals.Initialize(force: true);
                    Infrastructure.ClanCache.Initialize();

                    cachesReady = moduleManager.HideoutCache.Count > 0 && Core.Config.Globals.BasicInfantry.Count > 0;
                    gameReady = ModActivationManager.IsGameFullyInitialized();
                }
                catch (OutOfMemoryException) { throw; }
                catch (Exception ex)
                {
                    DebugLogger.Warning("MilitiaBehavior", $"Bootstrap cache rebuild failed: {ex.Message}");
                }
            }

            TryCompleteSessionBootstrap(moduleManager, cachesReady, gameReady);
        }

        private void ProcessPendingHideoutCacheRefresh()
        {
            if (!_hideoutCacheRefreshPending)
            {
                return;
            }

            try
            {
                Infrastructure.ModuleManager.Instance.RebuildCaches();
                _hideoutCacheRefreshPending = false;
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex)
            {
                DebugLogger.Warning("MilitiaBehavior", $"Pending hideout cache refresh failed: {ex.Message}");
            }
        }

        private void OnDailyTick()
        {
            if (ModActivationManager.IsGameplayActivationDelayed())
                return;

            BanditMilitias.Systems.Diagnostics.DiagnosticsSystem.StartScope("Militia.DailyTick");
            try
            {
                DecayCooldowns();
                ProcessPendingHideoutCacheRefresh();

                if (_isModActive)
                {
                    if (!_sessionBootstrapCompleted)
                    {
                        TryCompleteSessionBootstrapIfReady();
                        if (!_sessionBootstrapCompleted)
                        {
                            if (Settings.Instance?.TestingMode == true)
                            {
                                DebugLogger.Warning("MilitiaBehavior",
                                    $"Session bootstrap still pending. Hideouts={Infrastructure.ModuleManager.Instance.HideoutCache.Count}, " +
                                    $"Globals={Core.Config.Globals.BasicInfantry.Count}, " +
                                    $"GameReady={ModActivationManager.IsGameFullyInitialized()}");
                            }
                            return;
                        }
                    }

                    try
                    {
                        Infrastructure.ModuleManager.Instance.OnDailyTick();
                    }
                    catch (OutOfMemoryException) { throw; }
                    catch (Exception ex)
                    {

                        string err = $"[DailyTick] ModuleManager: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
                        DebugLogger.Error("MilitiaBehavior", err);
                        try { BanditMilitias.Infrastructure.FileLogger.LogError(err); } catch { }
                    }
                }
            }
            finally
            {
                BanditMilitias.Systems.Diagnostics.DiagnosticsSystem.EndScope("Militia.DailyTick");
            }
        }

        private void OnHourlyTick()
        {
            if (ModActivationManager.IsGameplayActivationDelayed())
                return;

            if (!_isModActive) return;


            Infrastructure.ModuleManager.Instance.CachedTotalParties = Campaign.Current?.MobileParties?.Count ?? 0;

            if (!_sessionBootstrapCompleted)
            {
                TryCompleteSessionBootstrapIfReady();
            }

            ProcessPendingHideoutCacheRefresh();

            BanditMilitias.Systems.Diagnostics.DiagnosticsSystem.StartScope("Militia.HourlyTick");
            try
            {
                ModuleManager.Instance.OnHourlyTick();


                Intelligence.Strategic.StrategyEngine.EvaluateAllRegionalStrategies();

                BanditMilitias.Systems.Diagnostics.SystemWatchdog.Instance.CheckSystems();

                int interval = Settings.Instance?.AutoDiagnosticReportInterval ?? 0;
                if (interval > 0)
                {
                    if (Math.Abs(CampaignTime.Now.ToHours % interval) < 0.1)
                    {
                        BanditMilitias.Systems.Dev.DevDataCollector.CommandFullSimTest(new List<string>());
                        Infrastructure.FileLogger.Log($"Automated FullSim Report generated at absolute hour {CampaignTime.Now.ToHours:F0}");
                    }
                }
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex)
            {
                // Always write to file; only show in-game message in TestingMode to avoid spam.
                FileLogger.LogError($"[HourlyTick] {ex.GetType().Name}: {ex.Message}");
                if (_totalTicks % 100 == 0 && Settings.Instance?.TestingMode == true)
                    DebugLogger.Error("MilitiaBehavior", $"Hourly tick error: {ex.Message}");
            }
            finally
            {
                BanditMilitias.Systems.Diagnostics.DiagnosticsSystem.EndScope("Militia.HourlyTick");
            }
        }

        private void OnTick(float dt)
        {


            if (ModActivationManager.IsGameplayActivationDelayed())
                return;

            if (!_isModActive) return;

            try
            {
                _totalTicks++;
                if (_totalTicks % 20 == 0)
                {
                    _currentLoad = CalculateCurrentLoad();
                    BanditMilitias.Systems.Diagnostics.DiagnosticsSystem.SetMetric("MilitiaBehavior.Load", _currentLoad);
                    BanditMilitias.Systems.Diagnostics.DiagnosticsSystem.SetMetric("MilitiaBehavior.TotalTicks", _totalTicks);
                    BanditMilitias.Systems.Diagnostics.DiagnosticsSystem.SetMetric("MilitiaBehavior.ActiveMilitias", ModuleManager.Instance.GetMilitiaCount());
                }
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex)
            {
                FileLogger.LogError($"[Tick] {ex.GetType().Name}: {ex.Message}");
                if (_totalTicks % 1000 == 0)
                    DebugLogger.Warning("MilitiaBehavior", $"Tick error: {ex.Message}");
            }
        }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            if (mapEvent == null) return;
            if (ModActivationManager.IsGameplayActivationDelayed()) return;

            foreach (var party in mapEvent.InvolvedParties)
            {
                if (party?.MobileParty != null &&
                    party.MobileParty.PartyComponent is MilitiaPartyComponent comp)
                {
                    comp.WakeUp();
                    comp.IsPriorityAIUpdate = true;

                    if (IsOurSide(party.MobileParty, mapEvent))
                        BanditMilitias.Systems.Combat.MilitiaVictorySystem
                            .ProcessVictory(party.MobileParty, mapEvent);
                }
            }

            Vec2 battlePos = BanditMilitias.Infrastructure.CompatibilityLayer.ToVec2(mapEvent.Position);
            if (battlePos.IsValid)
            {
                var nearbyMilitias = new System.Collections.Generic.List<MobileParty>();
                BanditMilitias.Systems.Grid.SpatialGridSystem.Instance.QueryNearby(battlePos, 10f, nearbyMilitias);

                foreach (var m in nearbyMilitias)
                {
                    if (m != null && m.IsActive && !mapEvent.InvolvedParties.Any(p => p?.MobileParty == m))
                    {
                        int scavengeGold = MBRandom.RandomInt(50, 200);
                        m.PartyTradeGold += scavengeGold;

                        if (MBRandom.RandomFloat < 0.3f)
                        {
                            ItemObject? scrap = DefaultItems.Grain;
                            if (scrap != null)
                            {
                                _ = m.ItemRoster.AddToCounts(scrap, MBRandom.RandomInt(1, 5));
                            }
                        }

                        if (Settings.Instance?.TestingMode == true)
                        {
                            DebugLogger.TestLog($"[SCAVENGE] {m.Name} scavenged {scavengeGold} gold and loot from the battlefield.", Colors.Cyan);
                        }
                    }
                }
            }

            if (mapEvent.IsPlayerMapEvent && mapEvent.MapEventSettlement?.IsHideout == true)
            {
                if (mapEvent.WinningSide == mapEvent.PlayerSide)
                {
                    OnHideoutDefeated(Hero.MainHero, mapEvent.MapEventSettlement);
                }
            }
        }

        private static bool IsOurSide(MobileParty militia, MapEvent mapEvent)
        {
            var winningSide = mapEvent.Winner;
            if (winningSide == null) return false;

            return winningSide.Parties.Any(p => p?.Party?.MobileParty == militia);
        }

        private void OnHideoutDefeated(Hero? destroyerHero, Settlement hideout)
        {
            if (hideout == null || hideout.IsActive) return;
            if (ShouldSuppressHideoutCleared(hideout.StringId)) return;

            if (ModuleManager.Instance != null && EventBus.Instance != null)
            {
                int survivingMilitias = ModuleManager.Instance.ActiveMilitias
                    .Count(m => m?.IsActive == true &&
                                m.PartyComponent is MilitiaPartyComponent c &&
                                c.HomeSettlement == hideout);

                var evt = BanditMilitias.Core.Events.EventBus.Instance.Get<HideoutClearedEvent>();
                if (evt != null)
                {
                    evt.Hideout = hideout;
                    evt.Clearer = destroyerHero;
                    evt.SurvivingMilitias = survivingMilitias;
                    try { NeuralEventRouter.Instance.Publish(evt); }
                    finally { BanditMilitias.Core.Events.EventBus.Instance.Return(evt); }
                }
            }
        }

        private static Hero? ResolveBattleVictorHero(MapEvent mapEvent, bool playerWonBattle)
        {
            if (playerWonBattle) return Hero.MainHero;

            var winnerSide = mapEvent.Winner;
            if (winnerSide == null) return null;

            foreach (var partyRef in winnerSide.Parties)
            {
                Hero? leader = partyRef?.Party?.MobileParty?.LeaderHero;
                if (leader != null) return leader;
            }

            return null;
        }

        private bool ShouldSuppressHideoutCleared(string hideoutId)
        {
            if (string.IsNullOrEmpty(hideoutId)) return true;

            if (_recentHideoutClearEvents.TryGetValue(hideoutId, out CampaignTime lastPublish))
            {
                if (CampaignTime.Now < lastPublish + CampaignTime.Hours(6f))
                {
                    return true;
                }
            }

            _recentHideoutClearEvents[hideoutId] = CampaignTime.Now;
            return false;
        }

        public void SetHideoutCooldown(Settlement hideout)
        {
            if (hideout == null) return;
            float cooldownHours = Settings.Instance?.SpawnCooldownHours ?? 24f;
            _hideoutCooldowns[hideout.StringId] = CampaignTime.Now + CampaignTime.Hours(cooldownHours);
        }

        public void SetHideoutFailureCooldown(Settlement hideout, float cooldownHours = 6f)
        {
            if (hideout == null) return;
            if (cooldownHours <= 0f) return;
            _hideoutFailureCooldowns[hideout.StringId] = CampaignTime.Now + CampaignTime.Hours(cooldownHours);
        }

        private void OnPartyCreated(MobileParty party)
        {

            if (party.PartyComponent is not MilitiaPartyComponent) return;
            if (ModActivationManager.IsGameplayActivationDelayed()) return;

            bool isReady = false;
            Vec2 position = CompatibilityLayer.GetPartyPosition(party);
            if (position.IsValid && position.X != 0f && party.IsActive && party.Party != null)
            {
                isReady = true;
            }

            if (isReady && ModuleManager.Instance != null)
            {
                ModuleManager.Instance.RegisterMilitia(party);
            }

        }

        private void OnPartyDestroyed(MobileParty party, PartyBase destroyer)
        {
            if (party == null) return;
            if (!_partiesBeingDestroyed.Add(party)) return;
            try
            {
            // ── Defense-in-depth: captor destruction safeguard ──────────────────
            // If the destroyed party is the player's captor, release the player first
            // to prevent vanilla crash. This duplicates PreventCaptorDestructionPatch
            // as a safety net in case Harmony fails.
            try
            {
                if (Hero.MainHero?.IsPrisoner == true)
                {
                    var captorParty = Hero.MainHero.PartyBelongedToAsPrisoner;
                    if (captorParty?.MobileParty == party)
                    {
                        DebugLogger.Info("MilitiaBehavior",
                            $"Captor party '{party.Name}' destroyed — releasing player before destruction");
                        EndCaptivityAction.ApplyByEscape(Hero.MainHero, null);
                    }
                }
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex)
            {
                DebugLogger.Warning("MilitiaBehavior", $"Captor destruction safeguard error: {ex.Message}");
            }

            if (party.PartyComponent is not MilitiaPartyComponent) return;
            if (ModActivationManager.IsGameplayActivationDelayed()) return;

            // Clean up scheduler queues before unregistering to prevent zombie references.
            try
            {
                var scheduler = ModuleManager.Instance.GetModule<Systems.Scheduling.AISchedulerSystem>();
                scheduler?.OnPartyDestroyedCleanup(party);
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex)
            {
                DebugLogger.Warning("MilitiaBehavior", $"AIScheduler cleanup failed: {ex.Message}");
            }

            // Run structural party cleanup (hero leak prevention, warlord penalties, etc.)
            try
            {
                var cleanupSys = ModuleManager.Instance.GetModule<Systems.Cleanup.PartyCleanupSystem>();
                cleanupSys?.OnPartyDestroyedCleanup(party, destroyer);
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex)
            {
                DebugLogger.Warning("MilitiaBehavior", $"PartyCleanupSystem cleanup failed: {ex.Message}");
            }

            ModuleManager.Instance.UnregisterMilitia(party);


            if (party.MemberRoster != null)
            {
                Infrastructure.TroopRosterPool.Return(party.MemberRoster);
            }
            if (party.PrisonRoster != null)
            {
                Infrastructure.TroopRosterPool.Return(party.PrisonRoster);
            }

            if (EventBus.Instance != null)
            {
                Settlement? homeHideout = (party.PartyComponent as MilitiaPartyComponent)?.GetHomeSettlementRaw();
                if (homeHideout != null)
                {
                    var killedEvt = BanditMilitias.Core.Events.EventBus.Instance.Get<MilitiaKilledEvent>();
                    if (killedEvt == null)
                    {
                        DebugLogger.Warning("MilitiaBehavior", "EventBus.Get<MilitiaKilledEvent>() returned null — skipping event publish.");
                    }
                    else
                    {
                        killedEvt.Party = party;
                        killedEvt.Killer = destroyer?.MobileParty;
                        killedEvt.KillerHero = destroyer?.LeaderHero;
                        killedEvt.HomeHideout = homeHideout;
                        killedEvt.IsPlayerResponsible = destroyer?.LeaderHero == Hero.MainHero;
                        killedEvt.WasPlayerKill = killedEvt.IsPlayerResponsible;
                        try { NeuralEventRouter.Instance.Publish(killedEvt); }
                        finally { BanditMilitias.Core.Events.EventBus.Instance.Return(killedEvt); }
                    }
                }
            }
            }
            finally
            {
                _ = _partiesBeingDestroyed.Remove(party);
            }
        }

        private void OnSettlementOwnerChanged(
            Settlement settlement, bool openToClaim,
            Hero newOwner, Hero oldOwner, Hero capturerHero,
            ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail)
        {
            if (settlement?.IsHideout != true)
            {
                return;
            }

            _hideoutCacheRefreshPending = true;

            if (ModActivationManager.IsGameplayActivationDelayed()) return;

            bool ownershipShiftedAwayFromBandits =
                oldOwner?.Occupation == Occupation.Bandit &&
                newOwner?.Occupation != Occupation.Bandit;

            if (!settlement.IsActive || ownershipShiftedAwayFromBandits)
            {
                OnHideoutDefeated(capturerHero, settlement);
            }
        }

        private void DecayCooldowns()
        {
            var expired = new List<string>(4);
            foreach (var kvp in _hideoutCooldowns)
                if (CampaignTime.Now >= kvp.Value) expired.Add(kvp.Key);
            foreach (var key in expired)
                _ = _hideoutCooldowns.Remove(key);

            var failureExpired = new List<string>(4);
            foreach (var kvp in _hideoutFailureCooldowns)
                if (CampaignTime.Now >= kvp.Value) failureExpired.Add(kvp.Key);
            foreach (var key in failureExpired)
                _ = _hideoutFailureCooldowns.Remove(key);

            var staleHideoutEvents = new List<string>(4);
            foreach (var kvp in _recentHideoutClearEvents)
                if (CampaignTime.Now >= kvp.Value + CampaignTime.Days(2f)) staleHideoutEvents.Add(kvp.Key);
            foreach (var key in staleHideoutEvents)
                _ = _recentHideoutClearEvents.Remove(key);
        }

        private float CalculateCurrentLoad()
        {
            if (Settings.Instance == null) return 0f;
            int maxTotal = Settings.Instance.MaxTotalMilitias;
            int currentCount = ModuleManager.Instance.GetMilitiaCount();
            if (currentCount >= maxTotal) return 1f;
            return (float)currentCount / Math.Max(1, maxTotal);
        }

        public string GetDiagnostics()
        {
            return $"MilitiaBehavior Status:\n" +
                   $"Active: {_isModActive}\n" +
                   $"Ticks: {_totalTicks}\n" +
                   $"Load: {_currentLoad:P1}";
        }

        private static Dictionary<string, double> ExportCampaignTimeDictionary(
            Dictionary<string, CampaignTime> source)
        {
            var serialized = new Dictionary<string, double>(source.Count);
            foreach (var kvp in source)
            {
                serialized[kvp.Key] = kvp.Value.ToHours;
            }

            return serialized;
        }

        private static void ImportCampaignTimeDictionary(
            Dictionary<string, CampaignTime> target,
            Dictionary<string, double>? serialized,
            double maxFutureHours)
        {
            target.Clear();
            if (serialized == null || serialized.Count == 0)
            {
                return;
            }

            double nowHours = CampaignTime.Now.ToHours;
            foreach (var kvp in serialized)
            {
                double storedHours = kvp.Value;
                if (storedHours <= nowHours)
                {
                    continue;
                }

                double clampedHours = Math.Min(storedHours, nowHours + maxFutureHours);
                float remainingHours = (float)(clampedHours - nowHours);
                if (remainingHours <= 0f)
                {
                    continue;
                }

                target[kvp.Key] = CampaignTime.Now + CampaignTime.Hours(remainingHours);
            }
        }

        public override void SyncData(IDataStore dataStore)
        {
            try
            {
                ModuleManager.Instance.SyncData(dataStore);
                var serializableCooldowns = dataStore.IsSaving
                    ? ExportCampaignTimeDictionary(_hideoutCooldowns)
                    : new Dictionary<string, double>();
                _ = dataStore.SyncData("_hideoutCooldowns", ref serializableCooldowns);
                if (dataStore.IsLoading)
                {
                    ImportCampaignTimeDictionary(
                        _hideoutCooldowns,
                        serializableCooldowns,
                        CampaignTime.Days(30f).ToHours);
                }

                var serializableFailureCooldowns = dataStore.IsSaving
                    ? ExportCampaignTimeDictionary(_hideoutFailureCooldowns)
                    : new Dictionary<string, double>();
                _ = dataStore.SyncData("_hideoutFailureCooldowns", ref serializableFailureCooldowns);
                if (dataStore.IsLoading)
                {
                    ImportCampaignTimeDictionary(
                        _hideoutFailureCooldowns,
                        serializableFailureCooldowns,
                        CampaignTime.Days(7f).ToHours);
                }

                var serializableRecentHideoutEvents = dataStore.IsSaving
                    ? ExportCampaignTimeDictionary(_recentHideoutClearEvents)
                    : new Dictionary<string, double>();
                _ = dataStore.SyncData("_recentHideoutClearEvents", ref serializableRecentHideoutEvents);
                if (dataStore.IsLoading)
                {
                    ImportCampaignTimeDictionary(
                        _recentHideoutClearEvents,
                        serializableRecentHideoutEvents,
                        CampaignTime.Days(7f).ToHours);
                }

                if (dataStore.IsSaving)
                {
                    if (ModActivationManager.TryGetActivationDelayState(
                        out double activationDelayStartHours,
                        out bool activationSwitchClosed))
                    {
                        _savedActivationDelayStartHours = activationDelayStartHours;
                        _savedActivationSwitchClosed = activationSwitchClosed;
                    }
                    else
                    {
                        _savedActivationDelayStartHours = -1d;
                        _savedActivationSwitchClosed = false;
                    }
                }

                _ = dataStore.SyncData("_activationDelayStartHours", ref _savedActivationDelayStartHours);
                _ = dataStore.SyncData("_activationSwitchClosed", ref _savedActivationSwitchClosed);

                // ── Militia State Persistence ──
                if (dataStore.IsSaving)
                {
                    _persistentData.Clear();
                    foreach (var kvp in _runtimeMap)
                    {
                        if (kvp.Key.IsActive && kvp.Key.StringId != null)
                        {
                            _persistentData[kvp.Key.StringId] = kvp.Value.SaveToData();
                        }
                    }
                }

                _ = dataStore.SyncData("_militiaPersistentData", ref _persistentData);

                if (dataStore.IsLoading)
                {
                    // Guard: save key may be absent when mod is added to an existing save
                    // or when the save was written by an older version that used a different key.
                    _persistentData ??= new Dictionary<string, MilitiaData>();

                    // Sanitize each entry so null reference fields never escape the load boundary.
                    // This handles structs that were saved before InheritedTactics was added (version < 4).
                    var keys = new System.Collections.Generic.List<string>(_persistentData.Keys);
                    foreach (var k in keys)
                    {
                        var d = _persistentData[k];
                        if (d.InheritedTactics == null)
                            d.InheritedTactics = new Dictionary<string, float>();
                        if (d.HomeSettlementId == null)
                            d.HomeSettlementId = string.Empty;
                        _persistentData[k] = d;
                    }

                    // Re-identification happens in OnSessionLaunched to ensure all parties are loaded
                }

                _ = dataStore.SyncData("_isModActive", ref _isModActive);
                _ = dataStore.SyncData("_saveVersion", ref _saveVersion);

                if (dataStore.IsLoading)
                {
                    ModActivationManager.RestoreActivationDelayState(
                        _savedActivationDelayStartHours,
                        _savedActivationSwitchClosed);
                }

                if (dataStore.IsLoading && _saveVersion < CurrentSaveVersion)
                {
                    DebugLogger.Info("MilitiaBehavior",
                        $"Save version mismatch: loaded={_saveVersion}, current={CurrentSaveVersion}. " +
                        $"Migration complete — defaults applied to missing fields.");
                    _saveVersion = CurrentSaveVersion;
                }
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex)
            {
                DebugLogger.Warning("MilitiaBehavior", $"SyncData error: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // OnAiHourlyTick — replaces AiPatrollingBehaviorPatch + BanditAiPatch
        // ══════════════════════════════════════════════════════════════════════
        // Registered via CampaignEvents.AiHourlyTickEvent in RegisterEvents.
        // Called by the vanilla AI engine for every party on each hourly tick.
        // For militia parties: runs our custom AI decision logic.
        // For warlord-assigned parties: executes HTN plan.
        // SetMove* calls at the end guarantee our command wins even if vanilla
        // scores override ours later in the same tick.
        // ══════════════════════════════════════════════════════════════════════
        private void OnAiHourlyTick(MobileParty mobileParty, PartyThinkParams p)
        {
            if (Campaign.Current == null) return;
            if (mobileParty == null) return;
            var component = mobileParty.GetMilitiaComponent();
            if (component == null) return;
            if (!mobileParty.IsActive) return;
            if (ModActivationManager.IsGameplayActivationDelayed()) return;

            try
            {
                // ── Warlord HTN Engine path (formerly BanditAiPatch) ─────────────
                var warlordSystem = WarlordSystem.Instance;
                var careerSystem = Systems.Progression.WarlordCareerSystem.Instance;
                if (warlordSystem != null && careerSystem != null)
                {
                    var warlord = warlordSystem.GetWarlordForParty(mobileParty);
                    if (warlord != null)
                    {
                        var tier = careerSystem.GetTier(warlord.StringId);
                        bool handled = HTNEngine.ExecutePlan(mobileParty, tier);
                        if (handled) return;
                    }
                }

                // ── Custom militia AI path (formerly AiPatrollingBehaviorPatch) ───

                // Restocking/sell-prisoners states should not consume tactical decisions.
                if (component.CurrentState == MilitiaPartyComponent.WarlordState.Restocking ||
                    component.CurrentState == MilitiaPartyComponent.WarlordState.SellingPrisoners)
                    return;

                bool hasModOrder = component.CurrentOrder != null;

                var swarmCoordinator = Intelligence.Swarm.SwarmCoordinator.Instance;
                bool hasSwarmGroup = swarmCoordinator != null && swarmCoordinator.IsInSwarm(mobileParty);
                bool needsSurvival = Intelligence.AI.CustomMilitiaAI.IsPartyWounded(mobileParty);

                if (!hasModOrder && !hasSwarmGroup && !needsSurvival)
                {
                    // No mod-specific order — let vanilla AI handle this party
                    return;
                }

                if (!ShouldUpdateAiDecision(mobileParty, component))
                    return;

                bool urgent = mobileParty.MapEvent != null || component.IsPriorityAIUpdate;

                var moduleManager = ModuleManager.Instance;
                var scheduler = moduleManager?.GetModule<AISchedulerSystem>();
                bool usedScheduler = scheduler?.IsEnabled == true;

                if (usedScheduler)
                {
                    scheduler!.EnqueueDecision(mobileParty, urgent);
                    // Flag ve sleep scheduler'ın callback'ine bırakılıyor.
                }
                else
                {
                    Intelligence.AI.CustomMilitiaAI.UpdateTacticalDecision(mobileParty);
                    component.IsPriorityAIUpdate = false;
                }
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex)
            {
                DebugLogger.Error("MilitiaBehavior",
                    $"[AiHourlyTick] {mobileParty.Name}: {ex.Message}");
                // Sleep the party for 2h so we don't hammer the same broken path every hour.
                try
                {
                    var comp = GetMilitiaComponent(mobileParty);
                    comp?.SleepFor(2f);
                }
                catch { }
            }
        }

        private static bool ShouldUpdateAiDecision(MobileParty party, MilitiaPartyComponent component)
        {
            if (party.MapEvent != null)
            {
                component.WakeUp();
                return true;
            }

            if (component.IsPriorityAIUpdate)
            {
                component.WakeUp();
                return true;
            }

            if (component.NextThinkTime == CampaignTime.Zero)
                return true;

            if (component.GetSleepOverdueHours() >= 6f)
            {
                component.WakeUp();
                return true;
            }

            if (CampaignTime.Now < component.NextThinkTime)
                return false;

            int currentHour = (int)CampaignTime.Now.ToHours;
            int partyHash = Math.Abs(party.StringId.GetHashCode());
            return (partyHash % 3) == (currentHour % 3);
        }

        private static float GetAiSleepHours(MilitiaPartyComponent component)
        {
            return component.Role == MilitiaPartyComponent.MilitiaRole.Guardian ? 6f : 4f;
        }

    }
}
