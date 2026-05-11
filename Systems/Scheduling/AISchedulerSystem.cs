using System;
using System.Collections.Generic;
using System.Linq;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Components;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Scheduling
{
    [Core.Components.ModuleDependency(typeof(BanditMilitias.Systems.Grid.SpatialGridSystem))]
    [BanditMilitias.Core.Components.AutoRegister(Priority = 130, IsCritical = true)]
    public class AISchedulerSystem : Core.Components.MilitiaModuleBase
    {
        public override string ModuleName => "AIScheduler";
        public override int Priority => 130;
        private static AISchedulerSystem? _instance;
        public static AISchedulerSystem? Instance => _instance;

        private bool _isEnabled;
        public override bool IsEnabled => _isEnabled;

        // ── Tracking ──────────────────────────────────────────────────────────
        private readonly Dictionary<string, CampaignTime> _lastUpdateTimes = new();
        private readonly Dictionary<string, float> _urgencyCache = new();

        // ── Queues ────────────────────────────────────────────────────────────
        private readonly Queue<(MobileParty party, bool urgent)> _decisionQueue = new();
        private readonly Queue<(MobileParty party, bool urgent)> _decisionOverflowQueue = new();
        private readonly Queue<Settlement> _spawnEvalQueue = new();
        private readonly HashSet<string> _queuedHideouts = new(StringComparer.Ordinal);
        private readonly HashSet<MobileParty> _stuckCandidates = new();

        // ── Budget constants ──────────────────────────────────────────────────
        // Max urgent decisions always processed (bypass budget cap).
        private const int MaxUrgentDecisionsPerTick = 10;
        private const int ZombieCandidateRefreshIntervalHours = 6;

        // ── LOD tier thresholds (distance from player) ────────────────────────
        private const float LOD_TIER1_RANGE = 30f;   // Near:  every tick
        private const float LOD_TIER2_RANGE = 100f;  // Mid:   every 3 ticks
                                                      // Far:   every 6 ticks

        // ── Diagnostics ───────────────────────────────────────────────────────
        private int _totalDecisionsProcessed;
        private int _totalSpawnEvalsProcessed;
        private int _zombiesRescued;
        private int _lastZombieCandidateRefreshHour = int.MinValue;

        // Reusable scratch list to avoid per-call allocation in CalculateUrgency.
        private readonly List<MobileParty> _scratchNearby = new(32);
        private static int MaxAIDecisionsPerTick => Math.Max(1, Settings.Instance?.MaxAITasksPerTick ?? 20);
        private static int MaxSpawnEvalsPerTick => Math.Max(1, Settings.Instance?.MaxSpawnEvaluationsPerTick ?? 30);

        // ─────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────

        public override void Initialize()
        {
            _instance = this;
            _isEnabled = true;
            _lastUpdateTimes.Clear();
            _urgencyCache.Clear();
            _decisionQueue.Clear();
            _decisionOverflowQueue.Clear();
            _spawnEvalQueue.Clear();
            _queuedHideouts.Clear();
            _stuckCandidates.Clear();
            _totalDecisionsProcessed = 0;
            _totalSpawnEvalsProcessed = 0;
            _zombiesRescued = 0;
            _lastZombieCandidateRefreshHour = int.MinValue;
            // SubscribeSafe guarantees removal via base.Cleanup(),
            // no need for a separate EventBus.Subscribe call.
            SubscribeSafe<StrategicCommandEvent>(OnStrategicCommandIssued);
        }

        public override void Cleanup()
        {
            // SubscribeSafe handles unsubscription via base.Cleanup().
            _isEnabled = false;
            _instance = null;
            _lastUpdateTimes.Clear();
            _urgencyCache.Clear();
            _decisionQueue.Clear();
            _decisionOverflowQueue.Clear();
            _spawnEvalQueue.Clear();
            _queuedHideouts.Clear();
            _stuckCandidates.Clear();
            _lastZombieCandidateRefreshHour = int.MinValue;
            base.Cleanup(); // guarantees SubscribeSafe registrations are removed
        }

        // ─────────────────────────────────────────────────────────────────────
        // Enqueue API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Enqueues a party for an AI decision on the next hourly tick.</summary>
        public void EnqueueDecision(MobileParty party, bool urgent = false)
        {
            if (party == null || !party.IsActive) return;
            _decisionQueue.Enqueue((party, urgent));
            if (urgent)
                _lastUpdateTimes.Remove(party.StringId);
        }

        /// <summary>Enqueues a hideout for spawn evaluation on the next hourly tick.</summary>
        public void EnqueueSpawnEvaluation(Settlement hideout)
        {
            if (hideout == null || string.IsNullOrWhiteSpace(hideout.StringId)) return;
            if (!_queuedHideouts.Add(hideout.StringId)) return;
            _spawnEvalQueue.Enqueue(hideout);
        }

        /// <summary>
        /// Removes all queue entries that reference the destroyed party.
        /// Call from MilitiaBehavior.OnPartyDestroyed to prevent zombie references.
        /// </summary>
        public void OnPartyDestroyedCleanup(MobileParty party)
        {
            if (party == null) return;

            _lastUpdateTimes.Remove(party.StringId);
            _urgencyCache.Remove(party.StringId);
            _ = _stuckCandidates.Remove(party);
        }

        // ─────────────────────────────────────────────────────────────────────
        // LOD / Urgency helpers
        // ─────────────────────────────────────────────────────────────────────

        public bool ShouldUpdate(MobileParty party)
        {
            if (party == null || !party.IsActive) return false;
            if (IsUrgent(party)) return true;

            int tickSkip = GetLODTickSkip(party);
            int partyHash = Math.Abs(party.StringId.GetHashCode());
            int currentHour = (int)CampaignTime.Now.ToHours;
            if (partyHash % tickSkip != currentHour % tickSkip) return false;

            if (_lastUpdateTimes.TryGetValue(party.StringId, out CampaignTime lastTime))
            {
                if ((CampaignTime.Now - lastTime).ToHours < 1.0) return false;
            }

            _lastUpdateTimes[party.StringId] = CampaignTime.Now;
            return true;
        }

        private static int GetLODTickSkip(MobileParty party)
        {
            if (MobileParty.MainParty == null) return 3;
            Vec2 playerPos = CompatibilityLayer.GetPartyPosition(MobileParty.MainParty);
            Vec2 partyPos = CompatibilityLayer.GetPartyPosition(party);
            if (!playerPos.IsValid || !partyPos.IsValid) return 3;

            float distSq = playerPos.DistanceSquared(partyPos);
            if (distSq <= LOD_TIER1_RANGE * LOD_TIER1_RANGE) return 1;
            if (distSq <= LOD_TIER2_RANGE * LOD_TIER2_RANGE) return 3;
            return 6;
        }

        private bool IsUrgent(MobileParty party)
        {
            if (party.MapEvent != null || party.IsMoving == false) return true;
            return GetCachedUrgency(party) > 0.8f;
        }

        private float GetCachedUrgency(MobileParty party)
        {
            if (_urgencyCache.TryGetValue(party.StringId, out float cached)) return cached;
            float urgency = CalculateUrgency(party);
            _urgencyCache[party.StringId] = urgency;
            return urgency;
        }

        private float CalculateUrgency(MobileParty party)
        {
            float urgency = 0.1f;
            Vec2 partyPosition = CompatibilityLayer.GetPartyPosition(party);
            if (!partyPosition.IsValid)
            {
                return urgency;
            }

            _scratchNearby.Clear();
            Systems.Grid.SpatialGridSystem.Instance.QueryNearby(
                partyPosition, 15f, _scratchNearby);

            foreach (var nearby in _scratchNearby)
            {
                if (nearby.MapFaction != null && party.MapFaction != null
                    && nearby.MapFaction.IsAtWarWith(party.MapFaction))
                {
                    urgency += 0.5f;
                    break;
                }
            }

            if (party.Morale < 30f) urgency += 0.3f;
            if (party.Food < 2f)    urgency += 0.2f;
            return MathF.Min(1.0f, urgency);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Hourly tick — budget-based queue draining
        // ─────────────────────────────────────────────────────────────────────

        // Counter for periodic _lastUpdateTimes pruning.
        private int _hourlyTickCount;

        public override void OnHourlyTick()
        {
            _urgencyCache.Clear();
            _hourlyTickCount++;

            // Every 24 ticks remove _lastUpdateTimes entries whose parties no longer
            // exist in the active militia list.  This is a safety net for cases where
            // OnPartyDestroyedCleanup was never called (crash, unregistered party, etc.).
            if (_hourlyTickCount % 24 == 0)
            {
                PruneStaleUpdateTimes();
            }

            UpdateStuckCandidates();
            RescueZombies();
            ProcessDecisionQueue();
            ProcessSpawnEvalQueue();
        }

        private void PruneStaleUpdateTimes()
        {
            if (_lastUpdateTimes.Count == 0) return;

            var activeMilitias = ModuleManager.Instance?.ActiveMilitias;
            if (activeMilitias == null) return;

            // Build a quick lookup of live StringIds.
            var liveIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in activeMilitias)
            {
                if (p?.StringId != null) liveIds.Add(p.StringId);
            }

            // Collect stale keys without modifying during enumeration.
            List<string>? toRemove = null;
            foreach (var key in _lastUpdateTimes.Keys)
            {
                if (!liveIds.Contains(key))
                {
                    (toRemove ??= new List<string>()).Add(key);
                }
            }

            if (toRemove == null) return;

            foreach (var key in toRemove)
            {
                _lastUpdateTimes.Remove(key);
                _urgencyCache.Remove(key);
            }

            if (Settings.Instance?.TestingMode == true && toRemove.Count > 0)
                DebugLogger.Info("AIScheduler",
                    $"[PruneStaleUpdateTimes] Removed {toRemove.Count} stale entries. Tracked={_lastUpdateTimes.Count}");
        }

        private void ProcessDecisionQueue()
        {
            if (_decisionQueue.Count == 0) return;

            int urgentProcessed = 0;
            int normalProcessed = 0;

            // Snapshot count so we don't loop over items enqueued this same tick.
            int toProcess = _decisionQueue.Count;

            for (int i = 0; i < toProcess; i++)
            {
                if (_decisionQueue.Count == 0) break;

                var (party, urgent) = _decisionQueue.Dequeue();

                if (party == null || !party.IsActive) continue;

                if (urgent)
                {
                    if (urgentProcessed >= MaxUrgentDecisionsPerTick)
                    {
                        _decisionOverflowQueue.Enqueue((party, urgent));
                        continue;
                    }
                    urgentProcessed++;
                }
                else
                {
                    if (normalProcessed >= MaxAIDecisionsPerTick)
                    {
                        _decisionOverflowQueue.Enqueue((party, false));
                        continue;
                    }
                    normalProcessed++;
                }

                try
                {
                    if (urgent)
                        _lastUpdateTimes.Remove(party.StringId);

                    var component = party.GetMilitiaComponent();
                    if (component != null) component.IsPriorityAIUpdate = false;
                    
                    Intelligence.AI.CustomMilitiaAI.UpdateTacticalDecision(party);
                    _lastUpdateTimes[party.StringId] = CampaignTime.Now;
                    _totalDecisionsProcessed++;
                }
                catch (Exception ex)
                {
                    // Record timestamp even on failure: if this was urgent (Remove was called above),
                    // missing the entry means IsEligibleForUpdate returns true immediately next tick
                    // → persistent exceptions cause an infinite tight retry loop.
                    _lastUpdateTimes[party.StringId] = CampaignTime.Now;
                    DebugLogger.Warning("AIScheduler",
                        $"Decision processing failed for {party.Name}: {ex.Message}");
                }
            }

            while (_decisionOverflowQueue.Count > 0)
            {
                _decisionQueue.Enqueue(_decisionOverflowQueue.Dequeue());
            }
        }

        private void ProcessSpawnEvalQueue()
        {
            if (_spawnEvalQueue.Count == 0) return;

            var spawner = ModuleManager.Instance.GetModule<Systems.Spawning.MilitiaSpawningSystem>();
            if (spawner == null || !spawner.IsEnabled) return;

            int processed = 0;
            int toProcess = Math.Min(_spawnEvalQueue.Count, MaxSpawnEvalsPerTick);

            for (int i = 0; i < toProcess; i++)
            {
                if (_spawnEvalQueue.Count == 0) break;

                Settlement hideout = _spawnEvalQueue.Dequeue();
                if (hideout != null && !string.IsNullOrWhiteSpace(hideout.StringId))
                {
                    _ = _queuedHideouts.Remove(hideout.StringId);
                }
                if (hideout == null) continue;

                try
                {
                    spawner.ProcessSpawnEvaluation(hideout);
                    _totalSpawnEvalsProcessed++;
                    processed++;
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("AIScheduler",
                        $"Spawn evaluation failed for {hideout.Name}: {ex.Message}");
                }
            }

            if (processed > 0 && Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("AIScheduler",
                    $"Processed {processed} spawn evaluations this tick. Total: {_totalSpawnEvalsProcessed}.");
            }
        }

        private void RescueZombies()
        {
            if (_stuckCandidates.Count == 0)
            {
                return;
            }

            List<MobileParty> resolved = new();

            foreach (var party in _stuckCandidates)
            {
                if (party == null || !party.IsActive) continue;
                if (party.PartyComponent is not Components.MilitiaPartyComponent comp) continue;

                bool orderMissing;
                bool isMoving;
                try
                {
                    orderMissing = comp.CurrentOrder == null;
                    isMoving = party.IsMoving;
                }
                catch
                {
                    orderMissing = true;
                    isMoving = false;
                }

                bool isStuck = orderMissing
                            && party.MapEvent == null
                            && !isMoving;

                if (!isStuck)
                {
                    resolved.Add(party);
                    continue;
                }

                try
                {
                    comp.IsPriorityAIUpdate = true;
                    EnqueueDecision(party, urgent: true);
                    _zombiesRescued++;

                    var evt = BanditMilitias.Core.Events.EventBus.Instance.Get<ZombiePartyDetectedEvent>();
                    if (evt != null)
                    {
                        evt.Party = party;
                        evt.HomeSettlement = comp.HomeSettlement;
                        // Use try/finally so Return is guaranteed even if Publish throws.
                        // Without this, the event object leaks out of the pool permanently.
                        try { BanditMilitias.Core.Events.EventBus.Instance.Publish(evt); }
                        finally { BanditMilitias.Core.Events.EventBus.Instance.Return(evt); }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Warning("AIScheduler", $"RescueZombie failed for {party.Name}: {ex.Message}");
                    // Add to resolved anyway: the party will be re-detected as stuck on the next
                    // UpdateStuckCandidates pass if still frozen, preventing infinite warning spam.
                    resolved.Add(party);
                }
            }

            foreach (var party in resolved)
            {
                _ = _stuckCandidates.Remove(party);
            }
        }

        private void UpdateStuckCandidates()
        {
            int currentHour = (int)CampaignTime.Now.ToHours;
            if (currentHour == _lastZombieCandidateRefreshHour)
            {
                return;
            }

            bool refreshAll = _stuckCandidates.Count == 0
                           || currentHour - _lastZombieCandidateRefreshHour >= ZombieCandidateRefreshIntervalHours;
            _lastZombieCandidateRefreshHour = currentHour;

            if (!refreshAll)
            {
                return;
            }

            _stuckCandidates.Clear();

            foreach (var party in ModuleManager.Instance.ActiveMilitias)
            {
                if (party == null || !party.IsActive) continue;
                if (party.PartyComponent is not Components.MilitiaPartyComponent comp) continue;

                bool orderMissing;
                bool isMoving;
                try
                {
                    orderMissing = comp.CurrentOrder == null;
                    isMoving = party.IsMoving;
                }
                catch
                {
                    orderMissing = true;
                    isMoving = false;
                }

                if (orderMissing && party.MapEvent == null && !isMoving)
                {
                    _ = _stuckCandidates.Add(party);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Event handlers
        // ─────────────────────────────────────────────────────────────────────

        private void OnStrategicCommandIssued(StrategicCommandEvent evt)
        {
            if (evt.TargetParty != null)
            {
                _lastUpdateTimes.Remove(evt.TargetParty.StringId);
            }
            else if (evt.TargetRegion != null)
            {
                var keys = _lastUpdateTimes.Keys.ToList();
                foreach (var key in keys)
                    _lastUpdateTimes.Remove(key);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Diagnostics
        // ─────────────────────────────────────────────────────────────────────

        public override string GetDiagnostics()
        {
            return $"AIScheduler: Tracked={_lastUpdateTimes.Count} | " +
                   $"PendingDecisions={_decisionQueue.Count} | " +
                   $"OverflowDecisions={_decisionOverflowQueue.Count} | " +
                   $"PendingSpawnEvals={_spawnEvalQueue.Count} | " +
                   $"Processed={_totalDecisionsProcessed} decisions, " +
                   $"{_totalSpawnEvalsProcessed} spawn evals | " +
                   $"ZombiesRescued={_zombiesRescued}";
        }
    }
}

