using BanditMilitias.Components;


using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.AI.Components;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Spawning;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Intelligence.Swarm
{

    public enum SwarmTactic
    {
        Idle,
        Patrol,
        Hunt,
        Pincer,
        Ambush,
        Defensive,
        Retreat,
        Envelopment
    }

    public enum FormationType
    {
        Loose,
        Wedge,
        Line,
        Circle,
        Pincer,
        Encirclement
    }

    [Serializable]
    public class SwarmGroup
    {
        public string Id { get; set; } = "";
        public string LeaderId { get; set; } = "";
        public List<string> PartyIds { get; set; } = new();
        public Vec2 Center { get; set; }
        public Vec2 MomentumVector { get; set; }
        public CampaignTime FormationTime { get; set; }
        public SwarmTactic CurrentTactic { get; set; } = SwarmTactic.Patrol;
        public FormationType Formation { get; set; } = FormationType.Loose;

        public Dictionary<string, float> TacticMemory { get; set; } = new();

        public Vec2? OrderPosition { get; set; }
        public string? OrderTargetId { get; set; }

        public int InitialPartyCount { get; set; }
        public int BattlesWon { get; set; }
        public int BattlesLost { get; set; }
        public float CohesionScore { get; set; } = 1f;

        // Lazy (Aptal) moddan anında uyandırmak için acil durum bayrağı
        [NonSerialized] public bool IsPriorityWake = false;
    }

    [Serializable]
    public class SwarmOrder
    {
        public SwarmTactic Tactic { get; set; }
        public FormationType Formation { get; set; }
        public Vec2 TargetPosition { get; set; }
        public string? TargetPartyId { get; set; }
        public int Priority { get; set; }
    }

    internal class AgentSteeringOutput
    {
        public Vec2 Desired { get; set; }
        public float Urgency { get; set; }
        public string Reason { get; set; } = "";
    }

    public class SwarmCoordinator : MilitiaModuleBase
    {

        private static SwarmCoordinator? _instance;
        public static SwarmCoordinator Instance => _instance ??= new SwarmCoordinator();

        public override string ModuleName => "SwarmCoordinator";
        public override bool IsEnabled => Settings.Instance?.EnableCustomAI ?? true;
        public override int Priority => 85;

        private const float COORDINATION_RADIUS = 40f;
        private const float SEPARATION_RADIUS = 8f;
        private const float ALIGNMENT_RADIUS = 25f;
        private const float COHESION_RADIUS = 35f;
        private const float THREAT_SCAN_RADIUS = 30f;

        private const float W_SEPARATION = 2.0f;
        private const float W_ALIGNMENT = 1.0f;
        private const float W_COHESION = 1.5f;
        private const float W_GOAL = 3.0f;

        private const float DISSOLUTION_LOSS_RATIO = 0.5f;
        private const int MIN_GROUP_SIZE = 2;
        private const float COHESION_DECAY_RATE = 0.02f;

        private List<SwarmGroup> _groups = new();

        [NonSerialized] private Dictionary<string, MobileParty> _partyCache = new();
        [NonSerialized] private Dictionary<string, SwarmGroup> _partyGroupMap = new();
        [NonSerialized] private Dictionary<string, SwarmOrder> _pendingOrders = new();

        [NonSerialized] private bool _pendingCacheRebuild = false;

        private SwarmCoordinator() { }

        private bool _isInitialized = false;

        public override void Initialize()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            _partyCache = new();
            _partyGroupMap = new();
            _pendingOrders = new();

            EventBus.Instance.Subscribe<MilitiaKilledEvent>(OnMilitiaKilled);
            EventBus.Instance.Subscribe<HideoutFormedEvent>(OnHideoutFormed);
            EventBus.Instance.Subscribe<StrategicCommandEvent>(OnStrategicCommand);
            EventBus.Instance.Subscribe<MilitiaSpawnedEvent>(OnMilitiaSpawned);
            EventBus.Instance.Subscribe<MilitiaDisbandedEvent>(OnMilitiaDisbanded);

            DebugLogger.Info("SwarmCoordinator", "Initialized.");
        }

        public override void RegisterCampaignEvents()
        {
            CampaignEvents.MapEventStarted.AddNonSerializedListener(this, OnBattleStarted);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnBattleEnded);

            // FIX (BUG-SwarmPostLoad): Save'den yükleme sonrası _partyCache ve
            // _partyGroupMap [NonSerialized] olduğu için boş kalır. İlk OnDailyTick'te
            // RebuildPartyCache() çalışsın diye bayrağı set et.
            _pendingCacheRebuild = true;

            DebugLogger.Info("SwarmCoordinator", "Campaign events registered, cache rebuild scheduled.");
        }

        public override void Cleanup()
        {
            EventBus.Instance.Unsubscribe<MilitiaKilledEvent>(OnMilitiaKilled);
            EventBus.Instance.Unsubscribe<HideoutFormedEvent>(OnHideoutFormed);
            EventBus.Instance.Unsubscribe<StrategicCommandEvent>(OnStrategicCommand);
            EventBus.Instance.Unsubscribe<MilitiaSpawnedEvent>(OnMilitiaSpawned);
            EventBus.Instance.Unsubscribe<MilitiaDisbandedEvent>(OnMilitiaDisbanded);

            CampaignEvents.MapEventStarted.ClearListeners(this);
            CampaignEvents.MapEventEnded.ClearListeners(this);

            _groups.Clear();
            _partyCache.Clear();
            _partyGroupMap.Clear();
            _isInitialized = false;
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled) return;
            if (_pendingCacheRebuild)
            {
                DebugLogger.Info("SwarmCoordinator", "Rebuilding party cache after load.");
            }
            RebuildPartyCache();
            _pendingCacheRebuild = false;
            DissolveFragmentedGroups();
            UpdateCohesionScores();
        }

        public override void OnHourlyTick()
        {
            if (!IsEnabled) return;
            DetectAndFormGroups();
            UpdateGroupTactics();
            ExecutePendingOrders();
        }

        public override void OnTick(float dt) { }

        private void DetectAndFormGroups()
        {
            var allActive = ModuleManager.Instance.ActiveMilitias
                .Where(p => p?.IsActive == true && p.MapFaction != null)
                .ToList();

            var ungrouped = allActive.Where(p => !_partyGroupMap.ContainsKey(p.StringId)).ToList();

            // Paging: Her saat sadece en eski 15 gruplanmamış birimi işle — O(N^2) patlamasını dağıtır.
            var seedsToProcess = ungrouped.Take(15).ToList();

            // candidates için O(1) üyelik kontrolü — List.Contains(O(n)) yerine
            var ungroupedSet = new HashSet<string>(ungrouped.Count);
            foreach (var p in ungrouped) ungroupedSet.Add(p.StringId);

            var visited = new HashSet<string>();
            foreach (var seed in seedsToProcess)
            {
                if (visited.Contains(seed.StringId)) continue;

                var cluster = GatherCluster(seed, ungroupedSet, visited, COORDINATION_RADIUS);
                if (cluster.Count < MIN_GROUP_SIZE) continue;

                FormGroup(cluster);
            }
        }

        private List<MobileParty> GatherCluster(
            MobileParty seed,
            HashSet<string> candidateIds,
            HashSet<string> visited,
            float radius)
        {
            var cluster = new List<MobileParty>();
            var queue = new Queue<MobileParty>();
            queue.Enqueue(seed);
            _ = visited.Add(seed.StringId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                cluster.Add(current);

                var nearby = new List<MobileParty>();
                BanditMilitias.Systems.Grid.SpatialGridSystem.Instance.QueryNearby(
                    CompatibilityLayer.GetPartyPosition(current), radius, nearby);

                foreach (var candidate in nearby)
                {
                    if (candidate == null || !candidate.IsActive) continue;
                    if (visited.Contains(candidate.StringId)) continue;
                    if (candidate.MapFaction != current.MapFaction) continue;
                    if (!candidateIds.Contains(candidate.StringId)) continue; // O(1)

                    _ = visited.Add(candidate.StringId);
                    queue.Enqueue(candidate);
                }
            }
            return cluster;
        }

        private void FormGroup(List<MobileParty> parties)
        {
            var leader = parties.OrderByDescending(p => CompatibilityLayer.GetTotalStrength(p)).FirstOrDefault();
            if (leader == null) return;

            var group = new SwarmGroup
            {
                Id = Guid.NewGuid().ToString(),
                LeaderId = leader.StringId,
                PartyIds = parties.Select(p => p.StringId).ToList(),
                Center = CalculateCenter(parties),
                FormationTime = CampaignTime.Now,
                CurrentTactic = SwarmTactic.Patrol,
                Formation = FormationType.Loose,
                InitialPartyCount = parties.Count,
                CohesionScore = 1f
            };

            _groups.Add(group);
            foreach (var p in parties)
                _partyGroupMap[p.StringId] = group;

            DebugLogger.Info("SwarmCoordinator",
                $"Group formed: {parties.Count} parties, leader={leader.Name}");
        }

        private void UpdateGroupTactics()
        {
            int currentHour = (int)CampaignTime.Now.ToHours;

            // _groups.ToList() kopyası gereksiz — iterasyon sırasında _groups değişmez
            for (int gi = _groups.Count - 1; gi >= 0; gi--)
            {
                var group = _groups[gi];
                // Staggered Ticks & Lazy Mode (Aptal Mod)
                // Her grubun kendine has bir "uyanma saati" var. O saat gelmediyse hesaplama YAPMA.
                int groupHash = Math.Abs(group.Id.GetHashCode());
                bool isTurnToThink = (groupHash % 3) == (currentHour % 3);

                if (!isTurnToThink && !group.IsPriorityWake)
                {
                    // Aptal Moddayız. Taktik hesaplanmadı, sadece son emri "PendingOrders" kuyruğuna besle.
                    var partiesSleep = ResolveParties(group);
                    if (partiesSleep.Count >= MIN_GROUP_SIZE)
                    {
                        // Uygulanan son taktik ile var olan harekete devam et.
                        // Order queue'sunda emri yenilemek için ApplyTactic körü körüne çağırılır (Threat hesaplanmaz).
                        ApplyTactic(group, partiesSleep, new List<MobileParty>(), group.CurrentTactic);
                    }
                    continue;
                }

                // PriorityWake veya Sıra Gelmişse Taktikleri Yenile
                group.IsPriorityWake = false; // Uyandı, flagi sıfırla.

                var parties = ResolveParties(group);
                if (parties.Count < MIN_GROUP_SIZE) continue;

                group.Center = CalculateCenter(parties);
                group.MomentumVector = CalculateMomentum(parties);

                var threats = ScanThreats(group, parties);
                var tactic = DecideTactic(group, parties, threats);

                if (tactic != group.CurrentTactic)
                {
                    DebugLogger.Info("SwarmCoordinator",
                        $"Group {(group.Id.Length > 6 ? group.Id.Substring(0, 6) : group.Id)}: {group.CurrentTactic}Â›{tactic}");
                    group.CurrentTactic = tactic;
                }

                ApplyTactic(group, parties, threats, tactic);
            }
        }

        private SwarmTactic DecideTactic(
            SwarmGroup group,
            List<MobileParty> parties,
            List<MobileParty> threats)
        {
            if (threats.Count == 0)
                return SwarmTactic.Patrol;

            float groupStrength = parties.Sum(p => CompatibilityLayer.GetTotalStrength(p));
            float threatStrength = threats.Sum(t => CompatibilityLayer.GetTotalStrength(t));
            float ratio = groupStrength / (threatStrength + 1f);
            bool isNight = Campaign.Current?.IsNight ?? false;
            bool goodAmbushSpot = IsAmbushFavorable(group.Center, isNight);

            float retreatScore = group.TacticMemory.TryGetValue("Retreat", out var rs) ? rs : 0.5f;
            float ambushScore = group.TacticMemory.TryGetValue("Ambush", out var ambs) ? ambs : 0.5f;
            float huntScore = group.TacticMemory.TryGetValue("Hunt", out var hs) ? hs : 0.5f;

            if (ratio < 0.4f && retreatScore >= 0.3f)
                return SwarmTactic.Retreat;

            if (ratio < 0.8f && goodAmbushSpot && ambushScore >= 0.4f)
                return SwarmTactic.Ambush;

            if (parties.Count >= 4 && ratio >= 1.2f)
                return SwarmTactic.Envelopment;

            if (parties.Count >= 2 && ratio >= 0.9f && huntScore >= 0.5f)
                return threats.Count == 1 ? SwarmTactic.Pincer : SwarmTactic.Hunt;

            if (ratio >= 1.5f)
                return SwarmTactic.Hunt;

            return SwarmTactic.Defensive;
        }

        private void ApplyTactic(
            SwarmGroup group,
            List<MobileParty> parties,
            List<MobileParty> threats,
            SwarmTactic tactic)
        {
            switch (tactic)
            {
                case SwarmTactic.Patrol:
                    ApplyPatrol(group, parties);
                    break;
                case SwarmTactic.Hunt:
                    ApplyHunt(group, parties, threats.FirstOrDefault());
                    break;
                case SwarmTactic.Pincer:
                    ApplyPincer(group, parties, threats.FirstOrDefault());
                    break;
                case SwarmTactic.Ambush:
                    ApplyAmbush(group, parties);
                    break;
                case SwarmTactic.Envelopment:
                    ApplyEnvelopment(group, parties, threats.FirstOrDefault());
                    break;
                case SwarmTactic.Defensive:
                    ApplyDefensive(group, parties);
                    break;
                case SwarmTactic.Retreat:
                    ApplyRetreat(group, parties, threats.FirstOrDefault());
                    break;
            }
        }

        private void ApplyPatrol(SwarmGroup group, List<MobileParty> parties)
        {
            group.Formation = FormationType.Loose;

            var homeHideout = FindNearestHideout(group.Center);
            if (homeHideout == null) return;

            var patrolPoints = GeneratePatrolPoints(CompatibilityLayer.GetSettlementPosition(homeHideout), 20f, parties.Count);

            for (int i = 0; i < parties.Count; i++)
            {
                var party = parties[i];
                var steer = CalculateFlockingVector(party, parties) * 0.5f;
                var goal = (patrolPoints[i % patrolPoints.Count] - CompatibilityLayer.GetPartyPosition(party)).Normalized();
                var final = (steer + goal * W_GOAL).Normalized();

                IssueMovementOrder(party, CompatibilityLayer.GetPartyPosition(party) + final * 10f);
            }

            group.OrderPosition = homeHideout != null ? CompatibilityLayer.GetSettlementPosition(homeHideout) : group.Center;
        }

        private void ApplyHunt(SwarmGroup group, List<MobileParty> parties, MobileParty? target)
        {
            if (target == null) return;
            group.Formation = FormationType.Wedge;
            group.OrderTargetId = target.StringId;

            var targetPos = CompatibilityLayer.GetPartyPosition(target);

            for (int i = 0; i < parties.Count; i++)
            {
                var party = parties[i];
                var flock = CalculateFlockingVector(party, parties);
                var toGoal = (targetPos - CompatibilityLayer.GetPartyPosition(party)).Normalized() * W_GOAL;

                if (i > 0)
                {
                    float angle = (i % 2 == 0 ? 1f : -1f) * (15f + i * 5f) * MathF.PI / 180f;
                    toGoal = RotateVec2(toGoal, angle);
                }

                var final = (flock + toGoal).Normalized();
                IssueMovementOrder(party, CompatibilityLayer.GetPartyPosition(party) + final * 15f);
            }

            group.OrderPosition = targetPos;
        }

        private void ApplyPincer(SwarmGroup group, List<MobileParty> parties, MobileParty? target)
        {
            if (target == null) { ApplyHunt(group, parties, null); return; }
            group.Formation = FormationType.Pincer;
            group.OrderTargetId = target.StringId;

            var targetPos = CompatibilityLayer.GetPartyPosition(target);
            var toTarget = (targetPos - group.Center).Normalized();
            var perpendicular = new Vec2(-toTarget.Y, toTarget.X);

            var leftArm = parties.Take(parties.Count / 2).ToList();
            var rightArm = parties.Skip(parties.Count / 2).ToList();

            IssueArmMovement(leftArm, targetPos, perpendicular * 15f);
            IssueArmMovement(rightArm, targetPos, perpendicular * -15f);

            group.OrderPosition = targetPos;
        }

        private void IssueArmMovement(List<MobileParty> arm, Vec2 target, Vec2 offset)
        {
            var approachPoint = target + offset;
            foreach (var p in arm)
            {
                var flock = CalculateFlockingVector(p, arm) * 0.3f;
                var goal = (approachPoint - CompatibilityLayer.GetPartyPosition(p)).Normalized() * W_GOAL;
                IssueMovementOrder(p, CompatibilityLayer.GetPartyPosition(p) + (flock + goal).Normalized() * 12f);
            }
        }

        private void ApplyAmbush(SwarmGroup group, List<MobileParty> parties)
        {
            group.Formation = FormationType.Circle;

            foreach (var party in parties)
            {
                var flock = CalculateFlockingVector(party, parties);
                var toCenter = (group.Center - CompatibilityLayer.GetPartyPosition(party)).Normalized() * W_GOAL;
                var final = (flock * 0.3f + toCenter).Normalized();

                float distSq = CompatibilityLayer.GetPartyPosition(party).DistanceSquared(group.Center);
                if (distSq > 25f)
                    IssueMovementOrder(party, CompatibilityLayer.GetPartyPosition(party) + final * 3f);
            }

            group.OrderPosition = group.Center;
        }

        private void ApplyEnvelopment(SwarmGroup group, List<MobileParty> parties, MobileParty? target)
        {
            if (target == null) { ApplyHunt(group, parties, null); return; }
            group.Formation = FormationType.Encirclement;
            group.OrderTargetId = target.StringId;

            var center = CompatibilityLayer.GetPartyPosition(target);
            float radius = 10f;
            int n = parties.Count;

            for (int i = 0; i < n; i++)
            {
                float angle = (2f * MathF.PI / n) * i;
                var ringPos = new Vec2(
                    center.X + MathF.Cos(angle) * radius,
                    center.Y + MathF.Sin(angle) * radius);

                var flock = CalculateFlockingVector(parties[i], parties) * 0.2f;
                var goal = (ringPos - CompatibilityLayer.GetPartyPosition(parties[i])).Normalized() * W_GOAL;
                IssueMovementOrder(parties[i], CompatibilityLayer.GetPartyPosition(parties[i]) + (flock + goal).Normalized() * 12f);
            }
        }

        private void ApplyDefensive(SwarmGroup group, List<MobileParty> parties)
        {
            group.Formation = FormationType.Line;
            foreach (var p in parties)
            {
                var flock = CalculateFlockingVector(p, parties);
                var toCenter = (group.Center - CompatibilityLayer.GetPartyPosition(p)).Normalized() * 2f;
                var final = (flock + toCenter).Normalized();
                IssueMovementOrder(p, CompatibilityLayer.GetPartyPosition(p) + final * 5f);
            }
        }

        private void ApplyRetreat(SwarmGroup group, List<MobileParty> parties, MobileParty? threat)
        {
            group.Formation = FormationType.Loose;

            var nearestHideout = FindNearestHideout(group.Center);
            Vec2 safePoint = nearestHideout != null ? CompatibilityLayer.GetSettlementPosition(nearestHideout) : group.Center;

            foreach (var p in parties)
            {
                Vec2 awayFromThreat = threat != null
                    ? (CompatibilityLayer.GetPartyPosition(p) - CompatibilityLayer.GetPartyPosition(threat)).Normalized() * W_GOAL
                    : Vec2.Zero;

                Vec2 toSafety = (safePoint - CompatibilityLayer.GetPartyPosition(p)).Normalized() * W_GOAL;
                Vec2 final = (awayFromThreat * 1.5f + toSafety).Normalized();

                IssueMovementOrder(p, CompatibilityLayer.GetPartyPosition(p) + final * 20f);
            }

            group.OrderPosition = safePoint;
        }

        private Vec2 CalculateFlockingVector(MobileParty self, List<MobileParty> neighbors)
        {
            var separation = Vec2.Zero;
            var alignment = Vec2.Zero;
            var cohesion = Vec2.Zero;

            int sepCount = 0, aliCount = 0, cohCount = 0;

            foreach (var other in neighbors)
            {
                if (other == self || !other.IsActive) continue;
                var delta = CompatibilityLayer.GetPartyPosition(self) - CompatibilityLayer.GetPartyPosition(other);
                float dist = delta.Length;

                if (dist < SEPARATION_RADIUS && dist > 0.01f)
                {
                    separation += delta.Normalized() * (SEPARATION_RADIUS / dist);
                    sepCount++;
                }

                if (dist < ALIGNMENT_RADIUS)
                {
                    alignment += (CompatibilityLayer.GetPartyPosition(other) - CompatibilityLayer.GetPartyPosition(self)).Normalized();
                    aliCount++;
                }

                if (dist < COHESION_RADIUS)
                {
                    cohesion += CompatibilityLayer.GetPartyPosition(other);
                    cohCount++;
                }
            }

            var result = Vec2.Zero;
            if (sepCount > 0) result += (separation / sepCount).Normalized() * W_SEPARATION;
            if (aliCount > 0) result += (alignment / aliCount).Normalized() * W_ALIGNMENT;
            if (cohCount > 0)
            {
                var cohCenter = cohesion / cohCount;
                result += (cohCenter - CompatibilityLayer.GetPartyPosition(self)).Normalized() * W_COHESION;
            }

            return result;
        }

        private static Vec2 CalculateMomentum(List<MobileParty> parties)
            => CalculateCenter(parties);

        private void IssueMovementOrder(MobileParty party, Vec2 targetPos)
        {
            var clamped = ClampToMapBounds(targetPos);
            _pendingOrders[party.StringId] = new SwarmOrder
            {
                Tactic = SwarmTactic.Hunt,
                TargetPosition = clamped,
                Priority = 5
            };
        }

        private void ExecutePendingOrders()
        {
            foreach (var kvp in _pendingOrders)
            {
                if (!_partyCache.TryGetValue(kvp.Key, out var party)) continue;
                if (!party.IsActive || party.MapEvent != null) continue;

                var order = kvp.Value;
                try
                {
                    CompatibilityLayer.SetMoveGoToPoint(party, order.TargetPosition);
                }
                catch (Exception ex)
                {
                    DebugLogger.Error("SwarmCoordinator", $"Order exec failed for {party.Name}: {ex.Message}");
                }
            }
            _pendingOrders.Clear();
        }

        public bool TryGetOrder(MobileParty party, out SwarmOrder order)
        {
            order = new SwarmOrder();
            if (!_partyGroupMap.TryGetValue(party.StringId, out var group)) return false;

            var tactic = group.CurrentTactic;
            if (group.CohesionScore < 0.35f && IsOffensiveTactic(tactic))
            {
                // Low-cohesion swarms should not hard-force aggressive maneuvers.
                tactic = SwarmTactic.Patrol;
            }

            int priority = ComputeOrderPriority(tactic, group.CohesionScore);
            if (priority <= 0) return false;

            order = new SwarmOrder
            {
                Tactic = tactic,
                Formation = group.Formation,
                TargetPosition = group.OrderPosition ?? group.Center,
                TargetPartyId = group.OrderTargetId,
                Priority = priority
            };
            return true;
        }

        private static bool IsOffensiveTactic(SwarmTactic tactic)
            => tactic is SwarmTactic.Hunt or SwarmTactic.Pincer or SwarmTactic.Ambush or SwarmTactic.Envelopment;

        private static int ComputeOrderPriority(SwarmTactic tactic, float cohesion)
        {
            int basePriority = tactic switch
            {
                SwarmTactic.Retreat => 5,
                SwarmTactic.Envelopment => 4,
                SwarmTactic.Pincer => 4,
                SwarmTactic.Hunt => 4,
                SwarmTactic.Ambush => 3,
                SwarmTactic.Defensive => 3,
                SwarmTactic.Patrol => 2,
                _ => 1
            };

            if (cohesion < 0.25f) basePriority -= 2;
            else if (cohesion < 0.45f) basePriority -= 1;

            return Math.Max(0, basePriority);
        }

        public bool IsInSwarm(MobileParty party) => _partyGroupMap.ContainsKey(party.StringId);

        public SwarmGroup? GetGroup(MobileParty party)
        {
            _ = _partyGroupMap.TryGetValue(party.StringId, out var g);
            return g;
        }

        public IReadOnlyList<SwarmGroup> AllGroups => _groups.AsReadOnly();

        private void DissolveFragmentedGroups()
        {
            var toRemove = new List<SwarmGroup>();
            foreach (var group in _groups)
            {
                var active = ResolveParties(group);
                float survivorRatio = active.Count / (float)Math.Max(1, group.InitialPartyCount);
                if (active.Count < MIN_GROUP_SIZE || survivorRatio < (1f - DISSOLUTION_LOSS_RATIO))
                {
                    toRemove.Add(group);
                    string shortId = group.Id.Length > 6 ? group.Id.Substring(0, 6) : group.Id;
                    DebugLogger.Info("SwarmCoordinator",
                        $"Group {shortId} dissolved ({active.Count}/{group.InitialPartyCount} survived).");
                }
            }
            foreach (var g in toRemove)
            {
                foreach (var id in g.PartyIds) _ = _partyGroupMap.Remove(id);
                _ = _groups.Remove(g);
            }
        }

        private void UpdateCohesionScores()
        {
            foreach (var group in _groups)
            {
                var parties = ResolveParties(group);
                if (parties.Count < 2) continue;

                float totalDist = 0f;
                int pairs = 0;
                for (int i = 0; i < parties.Count; i++)
                    for (int j = i + 1; j < parties.Count; j++)
                    {
                        totalDist += CompatibilityLayer.GetPartyPosition(parties[i]).Distance(CompatibilityLayer.GetPartyPosition(parties[j]));
                        pairs++;
                    }
                float avgDist = pairs > 0 ? totalDist / pairs : 0f;
                group.CohesionScore = MathF.Clamp(1f - avgDist / COORDINATION_RADIUS, 0f, 1f);
            }
        }

        private void RecordTacticOutcomes()
        {
            foreach (var group in _groups)
            {
                if (group.BattlesWon + group.BattlesLost == 0) continue;
                float winRate = group.BattlesWon / (float)(group.BattlesWon + group.BattlesLost);
                string tacKey = group.CurrentTactic.ToString();
                float prev = group.TacticMemory.TryGetValue(tacKey, out var val) ? val : 0.5f;

                group.TacticMemory[tacKey] = prev * 0.8f + winRate * 0.2f;
            }
        }

        private void OnBattleStarted(MapEvent mapEvent, PartyBase attackerParty, PartyBase defenderParty)
        {
            if (!IsEnabled || mapEvent == null) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;

            try
            {

                var involvedMilitias = new List<MobileParty>();

                var allSides = new[] { mapEvent.AttackerSide, mapEvent.DefenderSide };
                foreach (var side in allSides)
                {
                    if (side?.Parties == null) continue;
                    foreach (var party in side.Parties)
                    {
                        if (party?.Party?.MobileParty?.PartyComponent is MilitiaPartyComponent)
                            involvedMilitias.Add(party.Party.MobileParty);
                    }
                }

                if (involvedMilitias.Count == 0) return;

                foreach (var militia in involvedMilitias)
                {
                    ApplyBattleTactics(militia, mapEvent);
                }

                var groupsInBattle = involvedMilitias
                    .Where(m => _partyGroupMap.ContainsKey(m.StringId))
                    .Select(m => _partyGroupMap[m.StringId])
                    .Distinct()
                    .ToList();

                foreach (var group in groupsInBattle)
                {
                    // Acil Durum Uyanması: Çatışma çıktı, grubu aptal moddan (sleep mode) acil çıkar!
                    group.IsPriorityWake = true;

                    int partiesInBattle = group.PartyIds
                        .Count(id => involvedMilitias.Any(m => m.StringId == id));

                    if (group.CurrentTactic == SwarmTactic.Pincer && partiesInBattle >= 2)
                    {
                        foreach (var militia in involvedMilitias
                            .Where(m => group.PartyIds.Contains(m.StringId)))
                        {
                            ApplyMoraleBoost(militia, 15f, "Pincer coordination");
                        }
                    }
                    else if (group.CurrentTactic == SwarmTactic.Envelopment && partiesInBattle >= 3)
                    {
                        foreach (var militia in involvedMilitias
                            .Where(m => group.PartyIds.Contains(m.StringId)))
                        {
                            ApplyMoraleBoost(militia, 25f, "Envelopment coordination");
                        }
                    }
                }

                if (Settings.Instance?.TestingMode == true)
                    DebugLogger.Info("SwarmCoordinator",
                        $"Battle started: {involvedMilitias.Count} militias coordinated. Groups in battle: {groupsInBattle.Count}");
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("SwarmCoordinator", $"OnBattleStarted error: {ex.Message}");
            }
        }

        private void ApplyBattleTactics(MobileParty militia, MapEvent mapEvent)
        {
            if (!_partyGroupMap.TryGetValue(militia.StringId, out var group))
            {

                militia.Aggressiveness = 1.0f;
                return;
            }

            switch (group.CurrentTactic)
            {
                case SwarmTactic.Pincer:

                    militia.Aggressiveness = 1.0f;
                    ApplyMoraleBoost(militia, 10f, "Pincer tactic");
                    break;

                case SwarmTactic.Ambush:

                    militia.Aggressiveness = 1.0f;
                    ApplyMoraleBoost(militia, 20f, "Ambush advantage");
                    break;

                case SwarmTactic.Envelopment:

                    militia.Aggressiveness = 1.0f;
                    ApplyMoraleBoost(militia, 25f, "Envelopment dominance");
                    break;

                case SwarmTactic.Hunt:

                    militia.Aggressiveness = 1.0f;
                    break;

                case SwarmTactic.Defensive:

                    militia.Aggressiveness = 0.6f;
                    ApplyMoraleBoost(militia, 5f, "Defensive stance");
                    break;

                case SwarmTactic.Retreat:

                    militia.Aggressiveness = 0.3f;
                    break;

                default:
                    militia.Aggressiveness = 0.8f;
                    break;
            }
        }

        private static void ApplyMoraleBoost(MobileParty militia, float moraleBonus, string reason)
        {
            try
            {
                if (militia?.MemberRoster == null) return;

                float current = militia.Morale;
                float boosted = Math.Min(current + moraleBonus, 100f);
                militia.RecentEventsMorale += moraleBonus;

                if (Settings.Instance?.TestingMode == true)
                    DebugLogger.Info("SwarmCoordinator",
                        $"[Battle] {militia.Name}: Morale {current:F0}Â›{boosted:F0} ({reason})");
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("SwarmCoordinator", $"MoraleBoost failed: {ex.Message}");
            }
        }

        private void OnBattleEnded(MapEvent mapEvent)
        {
            if (!IsEnabled || mapEvent == null) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;

            try
            {

                var allSides = new[] { mapEvent.AttackerSide, mapEvent.DefenderSide };
                foreach (var side in allSides)
                {
                    if (side?.Parties == null) continue;
                    foreach (var partyBase in side.Parties)
                    {
                        var militia = partyBase?.Party?.MobileParty;
                        if (militia?.PartyComponent is not MilitiaPartyComponent) continue;
                        if (!_partyGroupMap.TryGetValue(militia.StringId, out var group)) continue;

                        bool attackerWon = mapEvent.WinningSide == BattleSideEnum.Attacker;
                        bool thisSideWon = (side.MissionSide == BattleSideEnum.Attacker) == attackerWon;

                        // NEW: Nam (Renown) Hesaplama Sistemi
                        if (militia.PartyComponent is Components.MilitiaPartyComponent mpc)
                        {
                            try {
                                float mySideStrength = side.Parties.Sum(p => (p.Party?.MobileParty != null) ? BanditMilitias.Infrastructure.CompatibilityLayer.GetTotalStrength(p.Party.MobileParty) : 0f);
                                float enemySideStrength = mapEvent.GetMapEventSide(side.MissionSide.GetOppositeSide()).Parties.Sum(p => (p.Party?.MobileParty != null) ? BanditMilitias.Infrastructure.CompatibilityLayer.GetTotalStrength(p.Party.MobileParty) : 0f);
                                float strengthRatio = enemySideStrength / Math.Max(1f, mySideStrength);
                                
                                float renownChange = thisSideWon ? (3f * strengthRatio) : (-2f / strengthRatio);
                                mpc.Renown = Math.Max(0f, mpc.Renown + renownChange);
                                
                                if (thisSideWon) mpc.BattlesWon++;
                                else mpc.BattlesLost++;

                                if (Settings.Instance?.TestingMode == true)
                                    DebugLogger.Info("SwarmCoordinator", $"[Renown] {militia.Name}: {renownChange:F1} puan. Toplam: {mpc.Renown:F1}");
                            } catch (Exception ex) {
                                DebugLogger.Warning("SwarmCoordinator", $"Renown calculation failed: {ex.Message}");
                            }
                        }

                        if (!_partyGroupMap.TryGetValue(militia.StringId, out var swGroup)) continue;

                        string tacKey = swGroup.CurrentTactic.ToString();
                        float prev = swGroup.TacticMemory.TryGetValue(tacKey, out var val) ? val : 0.5f;
                        float outcome = thisSideWon ? 1.0f : 0.0f;

                        swGroup.TacticMemory[tacKey] = prev * 0.8f + outcome * 0.2f;

                        if (thisSideWon)
                        {
                            group.BattlesWon++;
                        }
                        else group.BattlesLost++;

                        if (Settings.Instance?.TestingMode == true)
                            DebugLogger.Info("SwarmCoordinator",
                                $"[Battle] {militia.Name}: {(thisSideWon ? "WIN" : "LOSS")} with {group.CurrentTactic}. Memory: {group.TacticMemory[tacKey]:F2}");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("SwarmCoordinator", $"OnBattleEnded error: {ex.Message}");
            }
        }

        private void OnMilitiaKilled(MilitiaKilledEvent evt)
        {
            if (evt?.Party == null) return;
            string partyId = evt.Party.StringId;

            if (_partyGroupMap.TryGetValue(partyId, out var group))
            {
                _ = group.PartyIds.Remove(partyId);
                group.BattlesLost++;
                _ = _partyGroupMap.Remove(partyId);
            }
            _ = _partyCache.Remove(partyId);
        }

        private void OnHideoutFormed(HideoutFormedEvent evt)
        {
            if (evt?.Hideout == null) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;
            DebugLogger.Info("SwarmCoordinator",
                $"Hideout formed at {evt.Hideout.Name} Â— scanning for nearby militias.");

            var nearby = ModuleManager.Instance.ActiveMilitias
                .Where(p => p.IsActive && CompatibilityLayer.GetPartyPosition(p).DistanceSquared(CompatibilityLayer.GetSettlementPosition(evt.Hideout)) < COORDINATION_RADIUS * COORDINATION_RADIUS)
                .ToList();

            if (nearby.Count >= MIN_GROUP_SIZE)
                FormGroup(nearby);
        }

        private void OnMilitiaSpawned(MilitiaSpawnedEvent evt)
        {
            if (evt?.Party == null || !evt.Party.IsActive) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;
            if (_partyGroupMap.ContainsKey(evt.Party.StringId)) return;

            var pos = CompatibilityLayer.GetPartyPosition(evt.Party);
            if (!pos.IsValid) return;

            var homeSettlement = (evt.Party.PartyComponent as MilitiaPartyComponent)
                ?.GetHomeSettlement();

            SwarmGroup? target = null;
            if (homeSettlement != null)
            {
                target = _groups.FirstOrDefault(g =>
                    g.PartyIds.Count < 8 &&
                    g.PartyIds.Any(id =>
                    {
                        _ = _partyCache.TryGetValue(id, out var peer);
                        return (peer?.PartyComponent as MilitiaPartyComponent)
                            ?.GetHomeSettlement()?.StringId == homeSettlement.StringId;
                    }));
            }

            if (target != null)
            {
                target.PartyIds.Add(evt.Party.StringId);
                _partyGroupMap[evt.Party.StringId] = target;
            }
            else if (homeSettlement != null)
            {
                var sameHideoutCluster = ModuleManager.Instance.ActiveMilitias
                    .Where(p => p != null &&
                                p.IsActive &&
                                !_partyGroupMap.ContainsKey(p.StringId) &&
                                (p.PartyComponent as MilitiaPartyComponent)?.GetHomeSettlement()?.StringId == homeSettlement.StringId &&
                                CompatibilityLayer.GetPartyPosition(p).DistanceSquared(pos) <= COORDINATION_RADIUS * COORDINATION_RADIUS)
                    .Take(6)
                    .ToList();

                if (sameHideoutCluster.Count >= MIN_GROUP_SIZE)
                {
                    FormGroup(sameHideoutCluster);
                }
            }
            _partyCache[evt.Party.StringId] = evt.Party;

            if (Settings.Instance?.TestingMode == true)
                DebugLogger.Info("SwarmCoordinator",
                    $"[Spawn] {evt.Party.Name} â†’ {(target != null ? $"grup eklendi" : "periyodik gruplama bekliyor")}");
        }

        private void OnMilitiaDisbanded(MilitiaDisbandedEvent evt)
        {
            if (evt?.Party == null) return;
            string pid = evt.Party.StringId;

            if (_partyGroupMap.TryGetValue(pid, out var group))
            {
                _ = group.PartyIds.Remove(pid);
                _ = _partyGroupMap.Remove(pid);

                if (group.PartyIds.Count < MIN_GROUP_SIZE)
                {
                    foreach (var id in group.PartyIds)
                        _ = _partyGroupMap.Remove(id);
                    _ = _groups.Remove(group);

                    if (Settings.Instance?.TestingMode == true)
                        DebugLogger.Info("SwarmCoordinator",
                            $"[Disband] Grup daÄŸÄ±tÄ±ldÄ± â€” minimum boyutun altÄ±na dÃ¼ÅŸtÃ¼.");
                }
            }
            _ = _partyCache.Remove(pid);
        }

        private void OnStrategicCommand(StrategicCommandEvent evt)
        {
            if (evt?.IssuedBy == null || evt.Command == null) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;


            bool issuedByBrain = string.Equals(evt.IssuedBy, "BanditBrain", StringComparison.OrdinalIgnoreCase);

            var affectedGroups = _groups.Where(g =>
                ResolveParties(g).Any(p =>
                {
                    var comp = p.PartyComponent as MilitiaPartyComponent;
                    if (comp?.WarlordId == null) return false;

                    if (issuedByBrain) return true;

                    var warlord = WarlordSystem.Instance.GetWarlord(comp.WarlordId);
                    if (warlord == null) return false;

                    return string.Equals(evt.IssuedBy, "Warlord:" + warlord.Name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(evt.IssuedBy, warlord.StringId, StringComparison.OrdinalIgnoreCase);
                })).ToList();

            foreach (var g in affectedGroups)
            {
                // Komut geldi! Aptal moddan uyan.
                g.IsPriorityWake = true;

                g.CurrentTactic = evt.Command.Type switch
                {
                    CommandType.Raid => SwarmTactic.Hunt,
                    CommandType.Retreat => SwarmTactic.Retreat,
                    CommandType.Defend => SwarmTactic.Defensive,
                    CommandType.Patrol => SwarmTactic.Patrol,
                    CommandType.Ambush => SwarmTactic.Ambush,
                    CommandType.Hunt => SwarmTactic.Hunt,
                    CommandType.Engage => SwarmTactic.Hunt,
                    _ => g.CurrentTactic
                };

                if (evt.Command.TargetParty != null)
                {
                    g.OrderTargetId = evt.Command.TargetParty.StringId;
                }
            }
        }

        private void RebuildPartyCache()
        {
            _partyCache.Clear();
            foreach (var p in ModuleManager.Instance.ActiveMilitias)
                if (p?.IsActive == true)
                    _partyCache[p.StringId] = p;

            _partyGroupMap.Clear();
            foreach (var group in _groups)
            {
                foreach (var id in group.PartyIds)
                {
                    if (_partyCache.ContainsKey(id))
                        _partyGroupMap[id] = group;
                }
            }
        }

        private List<MobileParty> ResolveParties(SwarmGroup group)
        {
            var result = new List<MobileParty>();
            foreach (var id in group.PartyIds)
                if (_partyCache.TryGetValue(id, out var p) && p.IsActive)
                    result.Add(p);
            return result;
        }

        private static Vec2 CalculateCenter(List<MobileParty> parties)
        {
            if (parties.Count == 0) return Vec2.Zero;
            var sum = Vec2.Zero;
            foreach (var p in parties) sum += CompatibilityLayer.GetPartyPosition(p);
            return sum / parties.Count;
        }

        private List<MobileParty> ScanThreats(SwarmGroup group, List<MobileParty> parties)
        {
            var threats = new List<MobileParty>();
            var faction = parties.First().MapFaction;
            if (faction == null) return threats;

            var nearby = new List<MobileParty>();
            MilitiaSmartCache.Instance.GetNearbyParties(group.Center, THREAT_SCAN_RADIUS, nearby);

            foreach (var t in nearby)
                if (t.MapFaction?.IsAtWarWith(faction) == true && !threats.Contains(t))
                    threats.Add(t);

            threats.Sort((a, b) =>
                CompatibilityLayer.GetPartyPosition(a).DistanceSquared(group.Center).CompareTo(CompatibilityLayer.GetPartyPosition(b).DistanceSquared(group.Center)));

            return threats;
        }

        private bool IsAmbushFavorable(Vec2 pos, bool isNight)
        {
            if (isNight) return true;
            try
            {
                var face = Campaign.Current.MapSceneWrapper.GetFaceIndex(
                                  CompatibilityLayer.CreateCampaignVec2(pos, true));
                if (!face.IsValid()) return false;
                var terrain = Campaign.Current.MapSceneWrapper.GetFaceTerrainType(face);
                return terrain == TerrainType.Forest || terrain == TerrainType.Snow;
            }
            catch (Exception ex)
            {
                ExceptionMonitor.Capture(
                    "SwarmCoordinator.IsAmbushFavorable",
                    ex,
                    userVisible: true,
                    notifyCooldownMinutes: 60);
                return false;
            }
        }

        private static Settlement? FindNearestHideout(Vec2 pos)
        {
            Settlement? nearest = null;
            float minDistSq = float.MaxValue;

            foreach (var s in BanditMilitias.Infrastructure.ModuleManager.Instance.HideoutCache)
            {
                if (s == null || !s.IsActive) continue;
                float dSq = CompatibilityLayer.GetSettlementPosition(s).DistanceSquared(pos);
                if (dSq < minDistSq) { minDistSq = dSq; nearest = s; }
            }
            return nearest;
        }

        private static List<Vec2> GeneratePatrolPoints(Vec2 center, float radius, int count)
        {
            var pts = new List<Vec2>();
            for (int i = 0; i < count; i++)
            {
                float angle = (2f * MathF.PI / count) * i;
                pts.Add(new Vec2(center.X + MathF.Cos(angle) * radius,
                                 center.Y + MathF.Sin(angle) * radius));
            }
            return pts;
        }

        private static Vec2 RotateVec2(Vec2 v, float radians)
        {
            float cos = MathF.Cos(radians), sin = MathF.Sin(radians);
            return new Vec2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
        }

        private static Vec2 ClampToMapBounds(Vec2 pos)
        {
            const float MAP_LIMIT = 490f;
            return new Vec2(
                MathF.Clamp(pos.X, -MAP_LIMIT, MAP_LIMIT),
                MathF.Clamp(pos.Y, -MAP_LIMIT, MAP_LIMIT));
        }

        public override void SyncData(IDataStore dataStore)
        {
            _ = dataStore.SyncData("sc_groups_BM", ref _groups);

            if (dataStore.IsLoading)
            {

                _groups ??= new();
                _pendingCacheRebuild = true;
                DebugLogger.Info("SwarmCoordinator",
                    $"SyncData load complete Â— {_groups.Count} groups queued for cache rebuild.");
            }
        }

        public override string GetDiagnostics()
        {
            int total = _groups.Sum(g => g.PartyIds.Count);
            var tactics = _groups.GroupBy(g => g.CurrentTactic)
                .Select(gr => $"{gr.Key}:{gr.Count()}");
            return $"Groups:{_groups.Count} Parties:{total} Tactics:[{string.Join(",", tactics)}]";
        }
    }
}
