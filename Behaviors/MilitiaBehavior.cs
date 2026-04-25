using BanditMilitias.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Systems.Combat;
using BanditMilitias.Systems.Enhancement;
using BanditMilitias.Core.Neural;
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
            if (_sessionBootstrapCompleted || _lazyInitScheduled)
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
                    // FIX: TryLazyInitialization() zaten RebuildCaches yaptıysa tekrar çağırma.
                    // _lazyInitScheduled = true iken lazy init aktif demek — o RebuildCaches'i yapıyor.
                    // Buradan tekrar çağırmak 184-248ms'lik çift Bootstrap'e yol açıyordu.
                    if (!_lazyInitScheduled)
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

            // ✅ FIX: Cache total party count for performance
            Infrastructure.ModuleManager.Instance.CachedTotalParties = Campaign.Current?.MobileParties?.Count ?? 0;

            TryCompleteSessionBootstrapIfReady();

            BanditMilitias.Systems.Diagnostics.DiagnosticsSystem.StartScope("Militia.HourlyTick");
            try
            {
                ModuleManager.Instance.OnHourlyTick();
                
                // HİBRİT AI: Bölgesel Strateji Analizi (Çaresizlik Doktrini)
                // Tek geçişte tüm sığınakları değerlendir — foreach+Where yerine O(M) batch
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
                    NeuralEventRouter.Instance.Publish(evt);
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

            // OPTIMIZATION: Return used rosters to the object pool to reduce GC pressure
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
                    var killedEvt = EventBus.Instance.Get<MilitiaKilledEvent>();
                    killedEvt.Party = party;
                    killedEvt.Killer = destroyer?.LeaderHero;
                    killedEvt.HomeHideout = homeHideout;
                    killedEvt.IsPlayerResponsible = destroyer?.LeaderHero == Hero.MainHero;
                    NeuralEventRouter.Instance.Publish(killedEvt);
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
                            // cooldownHours büyük bir double (örn. ~87600h), float'a direkt cast \u00b10.5h hata verir.
                            // Çözüm: double hassasiyetle kalan süreyi hesapla, sadece KÜÇÜK relative değeri cast et.
                            double cooldownHours = kvp.Value;
                            double nowHours = CampaignTime.Now.ToHours;

                            if (cooldownHours <= nowHours)
                                continue; // Süresi geçmiş \u2192 atla

                            double maxHours = nowHours + CampaignTime.Days(30).ToHours;
                            if (cooldownHours > maxHours)
                                cooldownHours = nowHours + 1.0; // Çok ileride \u2192 1 saat kaldı say

                            // Kalan süre küçük sayı \u2192 float cast güvenli (max 720h = 30 gün)
                            float remainingHours = (float)(cooldownHours - nowHours);
                            _hideoutCooldowns[kvp.Key] = CampaignTime.Now + CampaignTime.Hours(remainingHours);
                        }
                    }
                }

                _ = dataStore.SyncData("_isModActive", ref _isModActive);
                _ = dataStore.SyncData("_saveVersion", ref _saveVersion);

                if (dataStore.IsLoading && _saveVersion < CurrentSaveVersion)
                {
                    DebugLogger.Info("MilitiaBehavior", $"Save version mismatch: {_saveVersion} -> {CurrentSaveVersion}. Performing migration...");
                    _saveVersion = CurrentSaveVersion;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("MilitiaBehavior", $"SyncData error: {ex.Message}");
            }
        }

    }
}
