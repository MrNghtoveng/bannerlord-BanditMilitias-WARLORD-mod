using BanditMilitias.Components;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BanditMilitias.Intelligence.AI
{

    public static class PatrolDetection
    {
        private const float DEFAULT_PATROL_RADIUS = 30f;
        private const float HEAVY_PATROL_THRESHOLD = 3f;

        [ThreadStatic]
        private static List<MobileParty>? _resultBuffer;
        private static List<MobileParty> ResultBuffer => _resultBuffer ??= new List<MobileParty>(16);

        [ThreadStatic]
        private static List<MobileParty>? _nearbyBuffer;
        private static List<MobileParty> NearbyBuffer => _nearbyBuffer ??= new List<MobileParty>(24);

        public static void RefreshPatrolCache()
        {
            // SpatialGridSystem authoritative source; no manual patrol cache to refresh.
        }

        public static float GetPatrolDensity(Settlement settlement, float radius = DEFAULT_PATROL_RADIUS)
        {
            if (settlement == null || !settlement.IsActive) return 0f;

            int patrolCount = GetNearbyPatrolCount(
                BanditMilitias.Infrastructure.CompatibilityLayer.GetSettlementPosition(settlement),
                radius
            );

            float area = (radius * radius) / 1000f;
            return patrolCount / area;
        }

        public static int GetNearbyPatrolCount(Vec2 position, float radius = DEFAULT_PATROL_RADIUS)
        {
            var nearby = NearbyBuffer;
            nearby.Clear();
            BanditMilitias.Systems.Grid.SpatialGridSystem.Instance.QueryNearby(position, radius, nearby);

            int count = 0;
            for (int i = 0; i < nearby.Count; i++)
            {
                if (IsPatrolParty(nearby[i]))
                {
                    count++;
                }
            }

            return count;
        }

        public static List<MobileParty> GetNearbyPatrols(Vec2 position, float radius = DEFAULT_PATROL_RADIUS)
        {
            var buffer = ResultBuffer;
            buffer.Clear();

            var nearby = NearbyBuffer;
            nearby.Clear();
            BanditMilitias.Systems.Grid.SpatialGridSystem.Instance.QueryNearby(position, radius, nearby);

            for (int i = 0; i < nearby.Count; i++)
            {
                if (IsPatrolParty(nearby[i]))
                {
                    buffer.Add(nearby[i]);
                }
            }

            return buffer;
        }

        public static bool IsHeavilyPatrolled(Settlement settlement)
        {
            if (settlement == null) return false;

            int count = GetNearbyPatrolCount(
                BanditMilitias.Infrastructure.CompatibilityLayer.GetSettlementPosition(settlement),
                DEFAULT_PATROL_RADIUS
            );

            return count >= HEAVY_PATROL_THRESHOLD;
        }

        private static bool IsPatrolParty(MobileParty party)
        {
            if (party == null || !party.IsActive) return false;

            if (party.PartyComponent is MilitiaPartyComponent) return false;

            if (party.IsGarrison) return true;

            if (party.IsMilitia) return true;

            if (party.IsLordParty && party.TargetSettlement != null)
            {

                if (party.TargetSettlement.OwnerClan == party.LeaderHero?.Clan)
                {
                    return true;
                }
            }

            if (party.IsVillager) return true;

            return false;
        }

        public static float CalculatePatrolPenalty(Settlement settlement, MobileParty attacker)
        {
            float density = GetPatrolDensity(settlement);

            if (density < 0.1f) return 0f;

            float aggressiveThreshold = Settings.Instance?.AggressivePatrolThreshold ?? 1.5f;

            if (aggressiveThreshold > 0f && attacker != null)
            {

                float ourStrength = BanditMilitias.Intelligence.AI.ScoringFunctions.CalculatePartyStrength(attacker);
                float patrolStrength = 0f;

                var patrols = GetNearbyPatrols(
                    BanditMilitias.Infrastructure.CompatibilityLayer.GetSettlementPosition(settlement),
                    DEFAULT_PATROL_RADIUS
                );

                foreach (var patrol in patrols)
                {
                    patrolStrength += BanditMilitias.Intelligence.AI.ScoringFunctions.CalculatePartyStrength(patrol);
                }

                if (patrolStrength > 0f)
                {
                    float strengthRatio = ourStrength / patrolStrength;

                    if (strengthRatio >= aggressiveThreshold)
                    {

                        float bonus = density * 15f;
                        bonus = Math.Min(bonus, 40f);

                        if (Settings.Instance?.TestingMode == true && bonus > 10f)
                        {
                            BanditMilitias.Debug.DebugLogger.TestLog(
                                $"[PatrolEngage] {attacker.Name} vs {settlement.Name} | Ratio: {strengthRatio:F1}x | BONUS: +{bonus:F0}",
                                TaleWorlds.Library.Colors.Green
                            );
                        }

                        return bonus;
                    }
                }
            }

            float penalty = density * -10f;
            penalty = Math.Max(penalty, -50f);
            return penalty;
        }

        public static int GetPatrolCount(Settlement settlement, float radius = DEFAULT_PATROL_RADIUS)
        {
            if (settlement == null) return 0;

            return GetNearbyPatrolCount(
                BanditMilitias.Infrastructure.CompatibilityLayer.GetSettlementPosition(settlement),
                radius
            );
        }

        public static float GetPatrolHeatAtPosition(Vec2 position, float radius = DEFAULT_PATROL_RADIUS)
        {
            int count = GetNearbyPatrolCount(position, radius);

            float heat = count / 10f;
            return Math.Min(heat, 1.0f);
        }

        public static void Cleanup()
        {
            _resultBuffer?.Clear();
            _nearbyBuffer?.Clear();
        }
    }
}
