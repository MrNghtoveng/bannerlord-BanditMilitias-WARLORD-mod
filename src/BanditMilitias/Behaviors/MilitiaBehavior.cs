using BanditMilitias.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Systems.Combat;
using BanditMilitias.Systems.Enhancement;
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
        private int _saveVersion = CurrentSaveVersion;

        private int _sessionLaunchRetries = 0;
        // FIX-5: 10 retry ile birçok parti init edilemiyordu (log: 30+ "Max lazy init retries reached").
        // 30'a çıkarıldı; yavaş yüklenen kampanyalarda daha uzun bekleme süresi tanınıyor.
        private const int MAX_SESSION_RETRIES = 30;
        private bool _lazyInitScheduled = false;
        private bool _sessionBootstrapPending = false;
        private bool _sessionBootstrapCompleted = false;
        private bool _activationDelayPendingLogged = false;
        private bool _lazyInitFailureNotified = false;

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

                if (_lazyInitScheduled)
                {
                    try
                    {
                        CampaignEvents.DailyTickEvent.RemoveNonSerializedListener(this, TryLazyInitialization);
                    }
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
            }
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

                if (CompatibilityLayer.IsGameplayActivationDelayed())
                {
                    DebugLogger.Info("MilitiaBehavior", "Activation delay active: skipping ALL startup scans to prevent hang.");
                    Infrastructure.FileLogger.Log("Activation delay active: total silence in OnSessionLaunched");
                    
                    // AGENT_TOTAL_SILENCE_FIX: Globals.Initialize() ve diğer taramalar 
                    // artık burada yapılmıyor. 2 gün sonra mod uyandığında yapılacak.

                    CompatibilityLayer.TryStartActivationDelayClock();
                    return;
                }

                try
                {
                    moduleManager.RebuildCaches();
                    DebugLogger.Info("MilitiaBehavior", "Session launched, initial cache rebuild attempted.");
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("MilitiaBehavior", $"Initial cache rebuild failed: {ex.Message}");
                }

                try
                {
                    _ = SubModule.RunDeferredSystemInit();
                    DebugLogger.Info("MilitiaBehavior", "Deferred system init completed. Mod state: Dormant");
                    SubModule.SetStateDormant();
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("MilitiaBehavior", $"Deferred system init failed: {ex.Message}");
                }

                try
                {
                    Core.Config.Globals.Initialize(force: true);
                    Infrastructure.ClanCache.Reset();
                    Infrastructure.ClanCache.Initialize();
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("MilitiaBehavior", $"Static data initialization failed: {ex.Message}");
                }

                bool cachesReady = moduleManager.HideoutCache.Count > 0 && Core.Config.Globals.BasicInfantry.Count > 0;
                bool gameReady = CompatibilityLayer.IsGameFullyInitialized();

                if ((!cachesReady || !gameReady) && !_lazyInitScheduled)
                {
                    _lazyInitScheduled = true;
                    _sessionLaunchRetries = 0;
                    DebugLogger.Warning("MilitiaBehavior", "Caches not ready. Scheduling lazy initialization.");
                    ScheduleLazyInitialization();
                }

                try
                {
                    Infrastructure.HealthCheck.RunDiagnostics(autoFix: true);
                    Infrastructure.TroopRosterPool.Clear();
                    Intelligence.AI.PatrolDetection.RefreshPatrolCache();
                    
                    Systems.Grid.SpatialGridSystem.Instance.OnSessionLaunched();
                    
                    DebugLogger.Info("MilitiaBehavior", "Infrastructure systems initialized (Patrol, Grid, Pool).");
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("MilitiaBehavior", $"Infrastructure init failed: {ex.Message}");
                }

                if (cachesReady && gameReady)
                {
                    TryCompleteSessionBootstrap(moduleManager, cachesReady, gameReady);
                }

                DebugLogger.Info("MilitiaBehavior", "Session launch bootstrap sequence ended.");
            }
            catch (Exception ex)
            {
                DebugLogger.Error("MilitiaBehavior", $"OnSessionLaunched failed: {ex}");
                try { FileLogger.LogError($"OnSessionLaunched failed: {ex}"); } catch {}
            }
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
                // FIX-5: Hangi cache'in dolu, hangisinin boş olduğunu açıkça logla.
                var mgr = Infrastructure.ModuleManager.Instance;
                int errHideoutCount = mgr.HideoutCache.Count;
                int errGlobalsCount = Core.Config.Globals.BasicInfantry.Count;
                bool errGameReady   = CompatibilityLayer.IsGameFullyInitialized();

                DebugLogger.Error("MilitiaBehavior",
                    $"Max lazy init retries ({MAX_SESSION_RETRIES}) reached — spawning devre dışı kalabilir! " +
                    $"[Hideouts={errHideoutCount} {(errHideoutCount == 0 ? "<<BOŞ>>" : "OK")} | " +
                    $"Globals={errGlobalsCount} {(errGlobalsCount == 0 ? "<<BOŞ>>" : "OK")} | " +
                    $"GameReady={errGameReady} {(!errGameReady ? "<<HAZIR DEĞİL>>" : "OK")}]");

                HandleLazyInitializationFailure(errHideoutCount, errGlobalsCount, errGameReady);
                CampaignEvents.DailyTickEvent.RemoveNonSerializedListener(this, TryLazyInitialization);
                _lazyInitScheduled = false;
                return;
            }

            _sessionLaunchRetries++;

            var moduleManager = Infrastructure.ModuleManager.Instance;
            int hideoutCount = moduleManager.HideoutCache.Count;
            int globalsCount = Core.Config.Globals.BasicInfantry.Count;

            bool gameReady = CompatibilityLayer.IsGameFullyInitialized();

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
                Infrastructure.HealthCheck.RunDiagnostics(autoFix: true);
            }
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
                    $"[BanditMilitias] Session bootstrap gecikti. Hideouts={hideoutCount}, Globals={globalsCount}, GameReady={gameReady}. Sistem kurtarma modunda yeniden deneyecek.",
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

            if (!CompatibilityLayer.IsGameplayActivationSwitchClosed())
            {
                if (!_activationDelayPendingLogged)
                {
                    _activationDelayPendingLogged = true;
                    int requiredDays = Settings.Instance?.ActivationDelay ?? 2;
                    DebugLogger.Info("MilitiaBehavior",
                        $"Gameplay activation switch is open: waiting {requiredDays} in-game days before session bootstrap.");
                }

                return;
            }

            try
            {
                if (!SubModule.IsDeferredInitDone)
                {
                    _ = SubModule.RunDeferredSystemInit();
                    Systems.Grid.SpatialGridSystem.Instance.OnSessionLaunched();
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
            catch (Exception ex)
            {
                DebugLogger.Warning("MilitiaBehavior", $"Session bootstrap completion failed: {ex.Message}");
            }
        }

        private void TryCompleteSessionBootstrapIfReady()
        {
            if (_sessionBootstrapCompleted)
            {
                return;
            }

            var moduleManager = Infrastructure.ModuleManager.Instance;
            bool cachesReady = moduleManager.HideoutCache.Count > 0 && Core.Config.Globals.BasicInfantry.Count > 0;
            bool gameReady = CompatibilityLayer.IsGameFullyInitialized();

            if (!cachesReady || !gameReady)
            {
                try
                {
                    moduleManager.RebuildCaches();
                    Core.Config.Globals.Initialize(force: true);
                    Infrastructure.ClanCache.Initialize();

                    cachesReady = moduleManager.HideoutCache.Count > 0 && Core.Config.Globals.BasicInfantry.Count > 0;
                    gameReady = CompatibilityLayer.IsGameFullyInitialized();
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("MilitiaBehavior", $"Bootstrap cache rebuild failed: {ex.Message}");
                }
            }

            TryCompleteSessionBootstrap(moduleManager, cachesReady, gameReady);
        }

        private void OnDailyTick()
        {
            if (CompatibilityLayer.IsGameplayActivationDelayed())
                return;

            TryCompleteSessionBootstrapIfReady();

            BanditMilitias.Systems.Diagnostics.DiagnosticsSystem.StartScope("Militia.DailyTick");
            try
            {
                DecayCooldowns();

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
                                    $"GameReady={CompatibilityLayer.IsGameFullyInitialized()}");
                            }
                            return;
                        }
                    }

                    try
                    {
                        Infrastructure.ModuleManager.Instance.OnDailyTick();
                    }
                    catch (Exception ex)
                    {

                        string err = $"[DailyTick] ModuleManager: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
                        Debug.DebugLogger.Error("MilitiaBehavior", err);
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
            if (CompatibilityLayer.IsGameplayActivationDelayed())
                return;

            if (!_isModActive) return;

            TryCompleteSessionBootstrapIfReady();

            BanditMilitias.Systems.Diagnostics.DiagnosticsSystem.StartScope("Militia.HourlyTick");
            try
            {
                ModuleManager.Instance.OnHourlyTick();
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
            catch (Exception ex)
            {
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
            // BUG-10 FIX: Activation kontrolü metriklerin önünde olmalı —
            // mod uyanmadan önce gereksiz hashing ve hesaplama yapılmasını önler.
            if (CompatibilityLayer.IsGameplayActivationDelayed())
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
            catch (Exception ex)
            {
                if (_totalTicks % 1000 == 0)
                    DebugLogger.Warning("MilitiaBehavior", $"Tick error: {ex.Message}");
            }
        }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            if (mapEvent == null) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;

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
                            DebugLogger.TestLog($"[SCAVENGE] {m.Name} savaş meydanından {scavengeGold} altın ve ganimet topladı.", Colors.Cyan);
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

                var evt = EventBus.Instance.Get<HideoutClearedEvent>();
                if (evt != null)
                {
                    evt.Hideout = hideout;
                    evt.Clearer = destroyerHero;
                    evt.SurvivingMilitias = survivingMilitias;
                    EventBus.Instance.Publish(evt);
                    EventBus.Instance.Return(evt);
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

            if (party?.PartyComponent is not MilitiaPartyComponent) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;

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
            if (party?.PartyComponent is not MilitiaPartyComponent) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;
            ModuleManager.Instance.UnregisterMilitia(party);

            if (EventBus.Instance != null)
            {
                Settlement? homeHideout = (party.PartyComponent as MilitiaPartyComponent)?.GetHomeSettlementRaw();
                if (homeHideout != null)
                {
                    var killedEvt = EventBus.Instance.Get<MilitiaKilledEvent>();
                    killedEvt.Party = party;
                    killedEvt.Killer = destroyer?.LeaderHero;
                    killedEvt.HomeHideout = homeHideout;
                    killedEvt.IsPlayerResponsible = destroyer?.LeaderHero == Hero.MainHero;
                    EventBus.Instance.Publish(killedEvt);
                    EventBus.Instance.Return(killedEvt);
                }
            }

        }

        private void OnSettlementOwnerChanged(
            Settlement settlement, bool openToClaim,
            Hero newOwner, Hero oldOwner, Hero capturerHero,
            ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail)
        {
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;

            if (settlement?.IsHideout == true)
            {
                bool ownershipShiftedAwayFromBandits =
                    oldOwner?.Occupation == Occupation.Bandit &&
                    newOwner?.Occupation != Occupation.Bandit;

                if (!settlement.IsActive || ownershipShiftedAwayFromBandits)
                {
                    OnHideoutDefeated(capturerHero, settlement);
                }
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

        public override void SyncData(IDataStore dataStore)
        {
            try
            {
                if (dataStore.IsSaving)
                {
                    var serializableCooldowns = new Dictionary<string, double>();
                    foreach (var kvp in _hideoutCooldowns)
                        serializableCooldowns[kvp.Key] = kvp.Value.ToHours;
                    _ = dataStore.SyncData("_hideoutCooldowns", ref serializableCooldowns);
                }
                else
                {
                    var serializableCooldowns = new Dictionary<string, double>();
                    _ = dataStore.SyncData("_hideoutCooldowns", ref serializableCooldowns);

                    if (serializableCooldowns != null)
                    {
                        _hideoutCooldowns.Clear();
                        foreach (var kvp in serializableCooldowns)
                        {
                            // FIX #5: Epoch-relative double değerini float'a dökerken hassasiyet kaybı önleme.
                            // cooldownHours büyük bir double (örn. ~87600h), float'a direkt cast ±0.5h hata verir.
                            // Çözüm: double hassasiyetle kalan süreyi hesapla, sadece KÜÇÜK relative değeri cast et.
                            double cooldownHours = kvp.Value;
                            double nowHours = CampaignTime.Now.ToHours;

                            if (cooldownHours <= nowHours)
                                continue; // Süresi geçmiş → atla

                            double maxHours = nowHours + CampaignTime.Days(30).ToHours;
                            if (cooldownHours > maxHours)
                                cooldownHours = nowHours + 1.0; // Çok ileride → 1 saat kaldı say

                            // Kalan süre küçük sayı → float cast güvenli (max 720h = 30 gün)
                            float remainingHours = (float)(cooldownHours - nowHours);
                            _hideoutCooldowns[kvp.Key] = CampaignTime.Now + CampaignTime.Hours(remainingHours);
                        }
                    }
                }

                if (dataStore.IsSaving)
                {
                    var serializableFailureCooldowns = new Dictionary<string, double>();
                    foreach (var kvp in _hideoutFailureCooldowns)
                        serializableFailureCooldowns[kvp.Key] = kvp.Value.ToHours;
                    _ = dataStore.SyncData("_hideoutFailureCooldowns", ref serializableFailureCooldowns);
                }
                else
                {
                    var serializableFailureCooldowns = new Dictionary<string, double>();
                    _ = dataStore.SyncData("_hideoutFailureCooldowns", ref serializableFailureCooldowns);
                    if (serializableFailureCooldowns != null)
                    {
                        _hideoutFailureCooldowns.Clear();
                        foreach (var kvp in serializableFailureCooldowns)
                        {
                            double cooldownHours = kvp.Value;
                            double nowHours = CampaignTime.Now.ToHours;

                            if (cooldownHours <= nowHours)
                                continue;

                            double maxHours = nowHours + CampaignTime.Days(7).ToHours;
                            if (cooldownHours > maxHours)
                                cooldownHours = nowHours + 1.0;

                            float remainingHours = (float)(cooldownHours - nowHours);
                            _hideoutFailureCooldowns[kvp.Key] = CampaignTime.Now + CampaignTime.Hours(remainingHours);
                        }
                    }
                }

                _ = dataStore.SyncData("_bm_save_version", ref _saveVersion);

                _ = ModuleManager.Instance.SyncData(dataStore);

                if (dataStore.IsLoading)
                {
                    int loadedVersion = _saveVersion <= 0 ? 1 : _saveVersion;
                    if (loadedVersion < CurrentSaveVersion)
                    {
                        MigrateSave(loadedVersion);
                    }
                    _saveVersion = CurrentSaveVersion;
                }
            }
            catch (Exception ex)
            {

                DebugLogger.Error("MilitiaBehavior", $"SyncData error: {ex.Message}");
                if (Settings.Instance?.TestingMode == true)
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[BanditMilitias] Kayýt hatasý: {ex.Message}", Colors.Red));
            }
        }

        private void MigrateSave(int fromVersion)
        {
            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("MilitiaBehavior", $"Migrating save v{fromVersion} -> v{CurrentSaveVersion}");
            }
            // Reserved for future migrations.
        }
    }


    // ── MilitiaHideoutCampaignBehavior (inline) ──────────────────────────────
    public class MilitiaHideoutCampaignBehavior : CampaignBehaviorBase
    {
        private Dictionary<string, HideoutData> _hideoutData = new Dictionary<string, HideoutData>();

        private const float FOOD_SHORTAGE_MORALE = -5f;
        private const float FOOD_SURPLUS_MORALE = +2f;
        private const float PASSIVE_XP_PER_DAY = 0.5f;

        public override void RegisterEvents()
        {
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }

        public override void SyncData(IDataStore dataStore)
        {
            _ = dataStore.SyncData("_hideoutData", ref _hideoutData);
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            _hideoutData ??= new Dictionary<string, HideoutData>();
        }

        private void OnDailyTick()
        {
            // BUG-01 FIX: Tüm aktif sığınaklara günlük işlem uygula.
            // Garnizon beslenmesi, XP kazanımı ve komutan gelişimi bu metot üzerinden tetiklenir.
            if (_hideoutData == null || _hideoutData.Count == 0) return;

            foreach (var kvp in _hideoutData)
            {
                if (kvp.Value == null) continue;
                try
                {
                    ProcessHideoutDaily(kvp.Key, kvp.Value);
                }
                catch (Exception ex)
                {
                    BanditMilitias.Debug.DebugLogger.Warning(
                        "MilitiaHideoutCampaignBehavior",
                        $"ProcessHideoutDaily failed for {kvp.Key}: {ex.Message}");
                }
            }
        }

        private void ProcessHideoutDaily(string hideoutId, HideoutData data)
        {
            if (data.Garrison == null || data.Garrison.TotalManCount == 0) return;

            int garrisonSize = data.Garrison.TotalManCount;

            int foodRequired = System.Math.Max(1, garrisonSize / 5);

            int foodConsumed = 0;
            if (data.StoredLoot != null)
            {
                for (int i = data.StoredLoot.Count - 1; i >= 0 && foodConsumed < foodRequired; i--)
                {
                    var itemElement = data.StoredLoot.GetElementCopyAtIndex(i);
                    if (itemElement.EquipmentElement.Item != null && itemElement.EquipmentElement.Item.IsFood)
                    {
                        int amountToConsume = System.Math.Min(itemElement.Amount, foodRequired - foodConsumed);
                        _ = data.StoredLoot.AddToCounts(itemElement.EquipmentElement.Item, -amountToConsume);
                        foodConsumed += amountToConsume;
                    }
                }
            }

            var hideout = Settlement.Find(hideoutId);

            var villageCache = BanditMilitias.Infrastructure.ModuleManager.Instance.VillageCache;
            int nearbyVillages = hideout != null
                ? villageCache.Count(s => s.GatePosition.DistanceSquared(hideout.GatePosition) < 900f)
                : 0;
            int foragers = System.Math.Min(garrisonSize / 2, 10);
            int foraged = (int)(foragers * (0.3f + nearbyVillages * 0.1f));
            foodConsumed = System.Math.Min(foodRequired, foodConsumed + foraged);

            if (foodConsumed < foodRequired)
            {

                data.Morale = TaleWorlds.Library.MathF.Clamp(data.Morale + FOOD_SHORTAGE_MORALE, 0f, 100f);

                int starvingMen = (foodRequired - foodConsumed) * 5;
                int woundedCount = 0;
                for (int i = 0; i < data.Garrison.Count && woundedCount < starvingMen; i++)
                {
                    var element = data.Garrison.GetElementCopyAtIndex(i);
                    if (element.Number - element.WoundedNumber > 0)
                    {
                        int woundAmount = System.Math.Min(element.Number - element.WoundedNumber, starvingMen - woundedCount);
                        data.Garrison.WoundTroop(element.Character, woundAmount);
                        woundedCount += woundAmount;
                    }
                }
            }
            else
            {

                if (foodConsumed >= foodRequired * 2)
                    data.Morale = TaleWorlds.Library.MathF.Clamp(data.Morale + FOOD_SURPLUS_MORALE, 0f, 100f);

                int totalXp = garrisonSize * 10;
                if (data.Garrison.Count > 0)
                {
                    int randomIdx = MBRandom.RandomInt(data.Garrison.Count);
                    data.Garrison.AddXpToTroop(data.Garrison.GetCharacterAtIndex(randomIdx), totalXp);
                }

                if (data.CommanderHero != null)
                {
                    float xpGain = PASSIVE_XP_PER_DAY * (1f + garrisonSize * 0.01f);
                    if (data.CommanderHero.GetSkillValue(DefaultSkills.Leadership) < 300)
                        data.CommanderHero.AddSkillXp(DefaultSkills.Leadership, xpGain);
                    if (data.CommanderHero.GetSkillValue(DefaultSkills.Tactics) < 200 && garrisonSize > 20)
                        data.CommanderHero.AddSkillXp(DefaultSkills.Tactics, xpGain * 0.5f);
                }
            }
        }

        public HideoutData GetHideoutData(Settlement hideout)
        {
            if (hideout == null) return new HideoutData();

            if (!_hideoutData.ContainsKey(hideout.StringId))
            {
                _hideoutData[hideout.StringId] = new HideoutData();
            }
            return _hideoutData[hideout.StringId];
        }

        public void DepositGold(Settlement hideout, int amount)
        {
            var data = GetHideoutData(hideout);
            if (data != null)
            {
                data.StoredGold += amount;

                if (data.StoredGold > 1000000) data.StoredGold = 1000000;
            }
        }

        public int WithdrawGold(Settlement hideout, int amount)
        {
            var data = GetHideoutData(hideout);
            if (data != null && data.StoredGold >= amount)
            {
                data.StoredGold -= amount;
                return amount;
            }
            return 0;
        }

    }


    // ── MilitiaRewardCampaignBehavior (inline) ──────────────────────────────
    public class MilitiaRewardCampaignBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
        }

        public override void SyncData(IDataStore dataStore)
        {

        }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            try
            {
                if (mapEvent == null) return;
                if (CompatibilityLayer.IsGameplayActivationDelayed()) return;

                if (mapEvent.IsPlayerMapEvent && mapEvent.WinningSide == mapEvent.PlayerSide)
                {
                    HandlePlayerVictory(mapEvent);
                }

                else if (mapEvent.WinningSide != BattleSideEnum.None)
                {
                    HandleMilitiaVictory(mapEvent);
                }
            }
            catch
            {

            }
        }

        private void HandlePlayerVictory(MapEvent mapEvent)
        {
            var defeatedSide = mapEvent.GetMapEventSide(mapEvent.DefeatedSide);

            var militiaParty = defeatedSide.Parties.FirstOrDefault(p =>
                p.Party.IsMobile &&
                p.Party.MobileParty.IsBandit &&
                (p.Party.MobileParty.PartyComponent is MilitiaPartyComponent || p.Party.MobileParty.StringId.Contains("Bandit_Militia")));

            if (militiaParty != null && Hero.MainHero != null)
            {

                float powerRatio = HeroicFeatsSystem.Instance.CalculatePowerRatio(
                    defeatedSide.LeaderParty,
                    mapEvent.GetMapEventSide(mapEvent.PlayerSide).LeaderParty);

                HeroicFeatsSystem.Instance.TryAwardAttributePoint(Hero.MainHero, powerRatio);
                HeroicFeatsSystem.Instance.TryAwardFocusPoint(Hero.MainHero, powerRatio);
                HeroicFeatsSystem.Instance.TryAwardHeroItem(Hero.MainHero, powerRatio);

                int baseGold = defeatedSide.TroopCount * 50;
                int finalGold = HeroicFeatsSystem.Instance.CalculateGoldWithUnderdogBonus(baseGold, powerRatio);

                float baseRenown = 1.0f;
                float bonusRenown = HeroicFeatsSystem.Instance.CalculateRenownWithUnderdogBonus(baseRenown, powerRatio) - baseRenown;

                if (finalGold > 0)
                {
                    GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, finalGold);
                }

                if (bonusRenown > 0.5f && Clan.PlayerClan != null)
                {
                    Clan.PlayerClan.AddRenown(bonusRenown);
                }

                HeroicFeatsSystem.Instance.ShowUnderdogMessage(powerRatio, finalGold, bonusRenown);
            }
        }

        private void HandleMilitiaVictory(MapEvent mapEvent)
        {
            var winningSide = mapEvent.GetMapEventSide(mapEvent.WinningSide);
            var militiaParty = winningSide.Parties.FirstOrDefault(p =>
                p.Party.IsMobile &&
                p.Party.MobileParty.PartyComponent is MilitiaPartyComponent);

            if (militiaParty != null && militiaParty.Party.MobileParty != null)
            {
                var mobileParty = militiaParty.Party.MobileParty;

                MilitiaVictorySystem.ProcessVictory(mobileParty, mapEvent);

                if (mobileParty.CurrentSettlement == null)
                {
                    var component = mobileParty.PartyComponent as MilitiaPartyComponent;
                    var home = component?.GetHomeSettlement();

                    if (home != null)
                    {
                        var ecology = Campaign.Current.GetCampaignBehavior<MilitiaHideoutCampaignBehavior>();
                        if (ecology != null)
                        {

                            int lootValue = mapEvent.GetMapEventSide(mapEvent.DefeatedSide).TroopCount * 50;
                            ecology.DepositGold(home, lootValue);
                        }
                    }
                }
            }
        }
    }


    // ── HideoutData (inline) ─────────────────────────────
    public class HideoutData
    {
        [SaveableProperty(1)]
        public int StoredGold { get; set; }

        [SaveableProperty(2)]
        public TroopRoster Garrison { get; set; }

        [SaveableProperty(3)]
        public ItemRoster StoredLoot { get; set; }

        [SaveableProperty(4)]
        public CampaignTime LastGarrisonUpdate { get; set; }

        [SaveableProperty(5)]
        public float Morale { get; set; } = 50f;

        [SaveableProperty(6)]
        public Hero? CommanderHero { get; set; }

        public HideoutData()
        {
            StoredGold = 0;
            Garrison = TroopRoster.CreateDummyTroopRoster();
            StoredLoot = new ItemRoster();
            LastGarrisonUpdate = CampaignTime.Now;
        }
    }
}
