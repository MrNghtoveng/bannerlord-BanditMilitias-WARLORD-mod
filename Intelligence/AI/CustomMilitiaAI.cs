using BanditMilitias.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.AI.Components;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.AI;
using BanditMilitias.Core.Neural;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Intelligence.AI
{

    public static class CustomMilitiaAI
    {

        private const float PATROL_RADIUS = 40f;
        private const float RAID_SEARCH_RADIUS = 60f;
        private const float ENEMY_DETECTION_RADIUS = 40f;
        // FIX-DUAL-BRAIN: THREAT_SCAN_RADIUS ve THREAT_OVERPOWER_RATIO buradan kaldırıldı.
        // Kaçış mantığı MilitiaDecider.TrySurvivalRetreat (Katman 0)'a taşındı.
        public const float STRATEGIC_ORDER_DURATION = 24f;

        private static bool _isInitialized = false;
        private static readonly object _initLock = new object();

        public static void Initialize()
        {
            lock (_initLock)
            {
                if (_isInitialized) return;

                try
                {
                    EventBus.Instance.Subscribe<StrategicCommandEvent>(OnStrategicCommand);
                    EventBus.Instance.Subscribe<CommandCompletionEvent>(OnCommandCompletion);
                    EventBus.Instance.Subscribe<BanditMilitias.Systems.Territory.TerritoryOpportunityEvent>(OnTerritoryOpportunity);

                    _isInitialized = true;

                    if (Settings.Instance?.TestingMode == true)
                    {
                        DebugLogger.Info("CustomMilitiaAI", "Operational Layer Initialized (Hybrid Mode)");
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Error("CustomMilitiaAI", $"Initialization failed: {ex.Message}");
                }
            }
        }

        public static void Cleanup()
        {
            lock (_initLock)
            {
                if (!_isInitialized) return;

                EventBus.Instance.Unsubscribe<StrategicCommandEvent>(OnStrategicCommand);
                EventBus.Instance.Unsubscribe<CommandCompletionEvent>(OnCommandCompletion);
                EventBus.Instance.Unsubscribe<BanditMilitias.Systems.Territory.TerritoryOpportunityEvent>(OnTerritoryOpportunity);

                _isInitialized = false;
            }
        }

        public static void SetStrategicTargetForVanilla(MobileParty party)
        {
            var component = party.PartyComponent as MilitiaPartyComponent;
            if (component?.CurrentOrder == null) return;

            var command = component.CurrentOrder;

            switch (command.Type)
            {
                case CommandType.Patrol:

                    var patrolCenter = component.GetHomeSettlement() != null
                        ? CompatibilityLayer.GetSettlementPosition(component.GetHomeSettlement()!)
                        : command.TargetLocation;

                    if (patrolCenter.IsValid)
                        SetMovePatrol(party, patrolCenter);
                    break;

                case CommandType.Raid:
                case CommandType.Harass:

                    var village = FindBestVillageToRaid(party, command.TargetLocation);
                    if (village != null)
                        SetMoveRaid(party, village);
                    else
                        SetMovePatrol(party, command.TargetLocation);
                    break;

                case CommandType.Hunt:
                case CommandType.Ambush:
                case CommandType.Engage:

                    if (command.TargetParty != null && command.TargetParty.IsActive)
                    {
                        float myStr = CompatibilityLayer.GetTotalStrength(party);
                        float targetStr = CompatibilityLayer.GetTotalStrength(command.TargetParty);
                        if (targetStr <= myStr * 1.5f)
                        {
                            SetMoveEngage(party, command.TargetParty);
                        }
                        else if (component.GetHomeSettlement() != null)
                        {
                            SetMoveGoTo(party, component.GetHomeSettlement()!);
                        }
                    }
                    else if (command.TargetLocation.IsValid)
                    {

                        SetMoveGoTo(party, command.TargetLocation);
                    }
                    break;

                case CommandType.Defend:

                    if (component.GetHomeSettlement() != null)
                        SetMoveDefend(party, component.GetHomeSettlement()!);
                    break;

                case CommandType.Retreat:

                    if (component.GetHomeSettlement() != null)
                        SetMoveGoTo(party, component.GetHomeSettlement()!);
                    break;
                case CommandType.Scavenge:

                    if (command.TargetLocation.IsValid)
                    {
                        SetMovePatrol(party, command.TargetLocation);
                    }
                    break;
            }
        }

        public static void UpdateTacticalDecision(MobileParty party)
        {
            var component = party.PartyComponent as MilitiaPartyComponent;
            if (component == null) return;

            // Ev savunma acil durumu
            if (!IsPartyWounded(party) && component.GetHomeSettlement() != null && component.GetHomeSettlement()!.LastAttackerParty != null)
            {
                if (component.GetHomeSettlement()!.LastAttackerParty!.IsActive &&
                    component.GetHomeSettlement()!.LastAttackerParty!.MapFaction.IsAtWarWith(party.MapFaction))
                {

                    if (component.CurrentOrder?.Type != CommandType.Defend)
                    {
                        AssignCommand(party, new StrategicCommand
                        {
                            Type = CommandType.Defend,
                            TargetLocation = CompatibilityLayer.GetSettlementPosition(component.GetHomeSettlement()!),
                            Priority = 0.9f,
                            Reason = "Home Under Attack!"
                        });
                        return;
                    }
                }
            }

            // FIX-DUAL-BRAIN: CurrentOrder olsa bile erken dönmüyoruz (return KALDIRILDI).
            // MilitiaDecider artık bu emri Layer 2'de (Swarm'dan sonra) değerlendirecek.
            if (component.CurrentOrder != null)
            {
                if (CampaignTime.Now - component.OrderTimestamp >= CampaignTime.Hours(STRATEGIC_ORDER_DURATION))
                {
                    var evt = new BanditMilitias.Core.Events.CommandCompletionEvent
                    {
                        Party = party,
                        Command = component.CurrentOrder,
                        Status = CommandCompletionStatus.Expired,
                        CompletionTime = CampaignTime.Now
                    };
                    NeuralEventRouter.Instance.Publish(evt);
                    component.CurrentOrder = null;
                }
            }

            if (CheckForOpportunities(party, component))
            {
                SetSleepByDistance(party, component);
                return;
            }

            _ = TryExecuteComponentDecision(party, component);
            SetSleepByDistance(party, component);
        }

        private static void SetSleepByDistance(MobileParty party, MilitiaPartyComponent component)
        {
            if (party.MapEvent != null || component.IsPriorityAIUpdate) return;

            float sleepHours;

            if (component.CurrentOrder != null && component.CurrentOrder.TargetLocation.IsValid)
            {
                Vec2 current = CompatibilityLayer.GetPartyPosition(party);
                float dist = current.Distance(component.CurrentOrder.TargetLocation);
                float speed = System.Math.Max(0.5f, CompatibilityLayer.GetPartySpeed(party));
                sleepHours = System.Math.Min(dist / speed + 1f, 8f);
            }
            else
            {
                sleepHours = component.Role == MilitiaPartyComponent.MilitiaRole.Guardian ? 6f : 4f;
            }

            component.SleepFor(sleepHours);
        }

        private static bool CheckForOpportunities(MobileParty party, MilitiaPartyComponent component)
        {
            if (party.MapFaction == null) return false;
            var doctrineSystem = ModuleManager.Instance.GetModule<AdaptiveAIDoctrineSystem>();

            var target = FindBestRaidTarget(party);
            if (target != null)
            {

                float distSq = CompatibilityLayer.GetPartyPosition(target).DistanceSquared(CompatibilityLayer.GetPartyPosition(party));
                float maxChaseDist = DecisionRules.GetChaseDistance(component);
                if (doctrineSystem != null && doctrineSystem.IsEnabled)
                {
                    maxChaseDist *= doctrineSystem.GetChaseDistanceMultiplier(party);
                }

                if (distSq < maxChaseDist * maxChaseDist)
                {
                    SetMoveEngage(party, target);
                    return true;
                }
            }

            if (component.Role == MilitiaPartyComponent.MilitiaRole.Raider && component.GetHomeSettlement() != null)
            {
                float distToHomeSq = CompatibilityLayer.GetPartyPosition(party).DistanceSquared(CompatibilityLayer.GetSettlementPosition(component.GetHomeSettlement()!));

                if (distToHomeSq < 8f * 8f)
                {
                    var targetSettlement = FindExpansionTarget(party, component.GetHomeSettlement()!);
                    if (targetSettlement != null)
                    {
                        AssignCommand(party, new StrategicCommand
                        {
                            Type = CommandType.Patrol,
                            TargetLocation = CompatibilityLayer.GetSettlementPosition(targetSettlement),
                            Priority = 0.45f,
                            Reason = "Roaming: move out from hideout"
                        });
                        return true;
                    }
                    else
                    {

                        Vec2 randomOffset = new Vec2(MBRandom.RandomFloat - 0.5f, MBRandom.RandomFloat - 0.5f);
                        _ = randomOffset.Normalize();
                        Vec2 newTarget = CompatibilityLayer.GetSettlementPosition(component.GetHomeSettlement()!) + (randomOffset * 15f);

                        AssignCommand(party, new StrategicCommand
                        {
                            Type = CommandType.Patrol,
                            TargetLocation = newTarget,
                            Priority = 0.45f,
                            Reason = "Roaming: forced outward"
                        });
                        return true;
                    }
                }
            }

            if (TryApplyLearningHint(party, component))
            {
                return true;
            }

            if (component.Role == MilitiaPartyComponent.MilitiaRole.Guardian)
            {

                if (component.GetHomeSettlement() != null && CompatibilityLayer.GetPartyPosition(party).DistanceSquared(CompatibilityLayer.GetSettlementPosition(component.GetHomeSettlement()!)) > 15f * 15f)
                {
                    SetMoveGoTo(party, component.GetHomeSettlement()!);
                    return true;
                }
            }

            return false;
        }

        private static Settlement? FindExpansionTarget(MobileParty me, Settlement home)
        {
            var allTargets = StaticDataCache.Instance.AllVillages.Concat(StaticDataCache.Instance.AllTowns);
            var candidates = allTargets.Where(s => s != home &&
                s.GatePosition.DistanceSquared(home.GatePosition) > 10f * 10f).ToList();

            if (candidates.Count == 0) return null;

            var idealCandidates = candidates.Where(s =>
            {
                float d2 = s.GatePosition.DistanceSquared(home.GatePosition);
                return d2 > 15f * 15f && d2 < 60f * 60f;
            }).ToList();

            if (idealCandidates.Count > 0)
            {
                return idealCandidates[MBRandom.RandomInt(idealCandidates.Count)];
            }

            return candidates.OrderBy(s => s.GatePosition.DistanceSquared(home.GatePosition)).First();
        }

        private static MobileParty? FindBestRaidTarget(MobileParty me)
        {
            if (Campaign.Current == null) return null;
            float searchRadius = RAID_SEARCH_RADIUS;
            float myStr = CompatibilityLayer.GetTotalStrength(me);
            MobileParty? bestTarget = null;
            float bestScore = float.MinValue;

            var nearby = new System.Collections.Generic.List<MobileParty>();
            MilitiaSmartCache.Instance.GetNearbyParties(
                CompatibilityLayer.GetPartyPosition(me), searchRadius, nearby);

            foreach (var p in nearby)
            {
                if (!p.IsActive) continue;
                if (p.MapEvent != null || p.SiegeEvent != null)
                {
                    if (p.MapEvent != null && p.MapEvent.WinningSide == BattleSideEnum.None)
                    {
                        var comp = p.PartyComponent as MilitiaPartyComponent;
                        if (comp != null)
                        {
                            float searchBoost = (comp.Role == MilitiaPartyComponent.MilitiaRole.VeteranCaptain ? 20f : 0f) + (comp.Renown / 5f);
                            float scavengeScore = 15f + searchBoost + (p.MapEvent.AttackerSide.TroopCount + p.MapEvent.DefenderSide.TroopCount) / 10f;
                            if (scavengeScore > bestScore)
                            {
                                bestScore = scavengeScore;
                                bestTarget = p;
                            }
                        }
                    }
                    continue;
                }

                if (p.Army != null) continue;
                if (p.MapFaction == null || me.MapFaction == null) continue;

                bool isMilitia = p.PartyComponent is MilitiaPartyComponent;

                if (!p.MapFaction.IsAtWarWith(me.MapFaction) && !isMilitia) continue;

                float theirStr = CompatibilityLayer.GetTotalStrength(p);

                if (isMilitia)
                {
                    if (theirStr > myStr * 0.7f) continue;

                    float predatoryScore = 20f + (myStr / (theirStr + 1f)) * 5f;

                    var warlord = WarlordSystem.Instance.GetWarlordForParty(me);
                    int availableGold = (int)(warlord?.Gold
                        ?? (me.PartyComponent as MilitiaPartyComponent)?.Gold
                        ?? 0);
                    bool hasMinimumPowerForDesperation = myStr >= System.Math.Max(40f, theirStr * 1.15f)
                        && (me.MemberRoster?.TotalManCount ?? 0) >= 18;
                    if (availableGold < 5000 && hasMinimumPowerForDesperation)
                    {
                        predatoryScore *= 2.5f;
                    }

                    if (predatoryScore > bestScore)
                    {
                        bestScore = predatoryScore;
                        bestTarget = p;
                    }
                    continue;
                }

                if (!p.IsCaravan && !p.IsVillager) continue;

                if (!DecisionRules.IsAdvantageousEngagement(myStr, theirStr,
                    (p.PartyComponent as MilitiaPartyComponent)?.Role == MilitiaPartyComponent.MilitiaRole.VeteranCaptain ? 1.05f : 1.25f)) continue;

                float valueBias = p.IsCaravan ? 40f : 15f;
                float score = valueBias + (myStr - theirStr);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = p;
                }
            }
            return bestTarget;
        }

        private static bool TryExecuteComponentDecision(MobileParty party, MilitiaPartyComponent component)
        {
            try
            {
                var sensors = new MilitiaAISensors(party, ENEMY_DETECTION_RADIUS);
                var decider = new MilitiaDecider();
                MilitiaDecider.DecisionResult decision = decider.GetBestDecision(party, component, sensors);

                switch (decision.Type)
                {
                    case AIDecisionType.Engage:
                        if (decision.TargetParty != null && decision.TargetParty.IsActive)
                        {
                            MilitiaActionExecutor.ExecuteEngage(party, decision.TargetParty);
                            return true;
                        }
                        break;

                    case AIDecisionType.Raid:
                        if (decision.TargetSettlement != null)
                        {
                            MilitiaActionExecutor.ExecuteRaid(party, decision.TargetSettlement);
                            return true;
                        }
                        break;

                    case AIDecisionType.Flee:
                    case AIDecisionType.Retreat:
                        if (decision.MovePoint.HasValue)
                        {
                            SetMoveGoTo(party, decision.MovePoint.Value);
                            return true;
                        }
                        if (decision.TargetParty != null && decision.TargetParty.IsActive)
                        {
                            MilitiaActionExecutor.ExecuteFlee(party, decision.TargetParty);
                            return true;
                        }
                        if (component.GetHomeSettlement() != null)
                        {
                            SetMoveGoTo(party, component.GetHomeSettlement()!);
                            return true;
                        }
                        break;

                    case AIDecisionType.Defend:
                        if (component.GetHomeSettlement() != null)
                        {
                            SetMoveDefend(party, component.GetHomeSettlement()!);
                            return true;
                        }
                        break;

                    case AIDecisionType.Ambush:
                        MilitiaActionExecutor.ExecuteAmbush(party);
                        return true;

                    case AIDecisionType.Patrol:
                    default:
                        MilitiaActionExecutor.ExecutePatrol(party, component.GetHomeSettlement(), decision.MovePoint);
                        return true;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("CustomMilitiaAI", $"Component decision fallback failed: {ex.Message}");
            }

            return false;
        }

        private static bool TryApplyLearningHint(MobileParty party, MilitiaPartyComponent component)
        {
            if (!MilitiaSmartCache.Instance.TryGetDecision(party, 0f, out var cached))
            {
                return false;
            }

            if ((CampaignTime.Now - cached.Timestamp).ToHours > 1.5f)
            {
                return false;
            }

            switch (cached.Decision)
            {
                case AIDecisionType.Raid:
                    {
                        Settlement? raidTarget = cached.TargetSettlement
                            ?? FindBestVillageToRaid(
                                party,
                                component.GetHomeSettlement() != null
                                    ? CompatibilityLayer.GetSettlementPosition(component.GetHomeSettlement()!)
                                    : CompatibilityLayer.GetPartyPosition(party));
                        if (raidTarget != null)
                        {
                            SetMoveRaid(party, raidTarget);
                            return true;
                        }
                        break;
                    }

                case AIDecisionType.Engage:
                    if (cached.ThreatParty != null && cached.ThreatParty.IsActive)
                    {
                        float myStr = CompatibilityLayer.GetTotalStrength(party);
                        float targetStr = CompatibilityLayer.GetTotalStrength(cached.ThreatParty);
                        if (DecisionRules.IsAdvantageousEngagement(myStr, targetStr, 1.1f))
                        {
                            SetMoveEngage(party, cached.ThreatParty);
                            return true;
                        }
                    }
                    break;

                case AIDecisionType.Flee:
                case AIDecisionType.Retreat:
                    if (component.GetHomeSettlement() != null)
                    {
                        SetMoveGoTo(party, component.GetHomeSettlement()!);
                        return true;
                    }
                    break;

                case AIDecisionType.Defend:
                    if (component.GetHomeSettlement() != null)
                    {
                        SetMoveDefend(party, component.GetHomeSettlement()!);
                        return true;
                    }
                    break;
            }

            return false;
        }

        private static void OnStrategicCommand(StrategicCommandEvent evt)
        {
            if (evt.Command == null) return;
            if (evt.TargetParty != null)
            {
                AssignCommand(evt.TargetParty, evt.Command);
            }
            else
            {

                var militias = ModuleManager.Instance.ActiveMilitias;
                foreach (var party in militias)
                {
                    if (party != null && party.IsActive)
                    {
                        AssignCommand(party, evt.Command);
                    }
                }
            }
        }

        public static void AssignCommand(MobileParty party, StrategicCommand command)
        {
            var component = party.PartyComponent as MilitiaPartyComponent;
            if (component == null) return;

            component.CurrentOrder = command;
            component.OrderTimestamp = CampaignTime.Now;
            component.IsPriorityAIUpdate = true;

            SetStrategicTargetForVanilla(party);
        }

        private static void OnCommandCompletion(CommandCompletionEvent evt)
        {

            if (evt.Party?.PartyComponent is MilitiaPartyComponent component)
            {
                if (evt.Status == CommandCompletionStatus.Success ||
                    evt.Status == CommandCompletionStatus.Failure ||
                    evt.Status == CommandCompletionStatus.Expired)
                {
                    component.CurrentOrder = null;
                    component.OrderTimestamp = CampaignTime.Zero;
                }

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Info("CustomMilitiaAI",
                        $"Command {evt.Command?.Type} completed ({evt.Status}) for {evt.Party?.Name}");
                }
            }
        }

        private static void OnTerritoryOpportunity(BanditMilitias.Systems.Territory.TerritoryOpportunityEvent evt)
        {

            CommandType cmdType = CommandType.Patrol;
            if (evt.Type == BanditMilitias.Systems.Territory.HotspotType.BattleGround) cmdType = CommandType.Scavenge;
            else if (evt.Type == BanditMilitias.Systems.Territory.HotspotType.RaidTarget) cmdType = CommandType.Raid;

            var candidates = ModuleManager.Instance.ActiveMilitias
                .Where(p =>
                    p.IsActive &&
                    IsPartyIdle(p) &&
                    !IsPartyWounded(p) &&
                    CompatibilityLayer.GetPartyPosition(p).DistanceSquared(evt.Position) < 50f * 50f)
                .OrderBy(p => CompatibilityLayer.GetPartyPosition(p).DistanceSquared(evt.Position))
                .Take(2);

            foreach (var party in candidates)
            {

                AssignCommand(party, new StrategicCommand
                {
                    Type = cmdType,
                    TargetLocation = evt.Position,
                    Priority = 0.6f,
                    Reason = $"Opportunity: {evt.Type}"
                });

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Info("CustomMilitiaAI", $"Dispatched {party.Name} to {evt.Type} at {evt.Position}");
                }
            }
        }

        private static bool IsPartyIdle(MobileParty party)
        {
            var component = party.PartyComponent as MilitiaPartyComponent;
            return component != null && component.CurrentOrder == null;
        }

        private static Settlement? FindBestVillageToRaid(MobileParty party, Vec2 center)
        {
            return StaticDataCache.Instance.AllVillages
                   .Where(s => s.MapFaction.IsAtWarWith(party.MapFaction))
                   .OrderBy(s => s.GatePosition.DistanceSquared(center))
                   .FirstOrDefault();
        }

        public static bool IsPartyWounded(MobileParty party)
        {
            if (party?.MemberRoster == null) return false;
            int total = party.MemberRoster.TotalManCount;
            int wounded = party.MemberRoster.TotalWoundedRegulars;
            return DecisionRules.IsWounded(total, wounded);
        }

        public static bool IsCommandActive(MilitiaPartyComponent component)
        {
            if (component.CurrentOrder == null) return false;
            if (component.OrderTimestamp == CampaignTime.Zero) return false;
            return (CampaignTime.Now - component.OrderTimestamp).ToHours < STRATEGIC_ORDER_DURATION;
        }

        private static void SetMovePatrol(MobileParty party, Vec2 point)
        {
            BanditMilitias.Systems.Dev.DevDataCollector.Instance.RecordPathfindingCall();
            CompatibilityLayer.SetMovePatrolAroundPoint(party, point);
        }

        private static void SetMovePatrol(MobileParty party, Settlement s)
        {
            BanditMilitias.Systems.Dev.DevDataCollector.Instance.RecordPathfindingCall();
            CompatibilityLayer.SetMovePatrolAroundSettlement(party, s);
        }

        private static void SetMoveRaid(MobileParty party, Settlement s)
        {
            BanditMilitias.Systems.Dev.DevDataCollector.Instance.RecordPathfindingCall();
            CompatibilityLayer.SetMoveRaidSettlement(party, s);
        }

        private static void SetMoveEngage(MobileParty party, MobileParty target)
        {
            BanditMilitias.Systems.Dev.DevDataCollector.Instance.RecordPathfindingCall();
            CompatibilityLayer.SetMoveEngageParty(party, target);
        }

        private static void SetMoveGoTo(MobileParty party, Vec2 point)
        {
            BanditMilitias.Systems.Dev.DevDataCollector.Instance.RecordPathfindingCall();
            CompatibilityLayer.SetMoveGoToPoint(party, point);
        }

        private static void SetMoveGoTo(MobileParty party, Settlement s)
        {
            BanditMilitias.Systems.Dev.DevDataCollector.Instance.RecordPathfindingCall();
            CompatibilityLayer.SetMoveGoToSettlement(party, s);
        }

        private static void SetMoveDefend(MobileParty party, Settlement s)
        {
            BanditMilitias.Systems.Dev.DevDataCollector.Instance.RecordPathfindingCall();
            CompatibilityLayer.SetMoveDefendSettlement(party, s);
        }
    }

    public static class DecisionRules
    {
        public static bool IsWounded(int totalTroops, int woundedTroops)
        {
            if (totalTroops <= 0 || woundedTroops <= 0) return false;
            int wounded = System.Math.Min(totalTroops, woundedTroops);
            return totalTroops < 12
                ? wounded >= System.Math.Max(1, (int)(totalTroops * 0.8f))
                : wounded / (float)totalTroops > 0.65f;
        }

        public static bool IsOverwhelmingThreat(float own, float enemy, float ratio)
            => enemy > 0f && (own <= 0f || enemy > own * ratio);

        public static bool IsAdvantageousEngagement(float own, float enemy, float minRatio)
            => own > 0f && (enemy <= 0f || own > enemy * minRatio);

        public static float GetChaseDistance(MilitiaPartyComponent mpc, float raiderDist = 25f, float guardianDist = 10f)
        {
            return GetChaseDistance(mpc.Role, mpc.Renown, raiderDist, guardianDist);
        }

        public static float GetChaseDistance(MilitiaPartyComponent.MilitiaRole role, float renown, float raiderDist = 25f, float guardianDist = 10f)
        {
            float baseDist = role == MilitiaPartyComponent.MilitiaRole.Raider ? raiderDist : guardianDist;
            float multiplier = (role == MilitiaPartyComponent.MilitiaRole.VeteranCaptain ? 1.5f : 1.0f) + (renown / 500f);
            return baseDist * multiplier;
        }
    }
}
