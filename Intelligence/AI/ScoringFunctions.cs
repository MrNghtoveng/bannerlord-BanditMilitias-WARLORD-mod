using BanditMilitias.Components;
using BanditMilitias.Intelligence.AI.Components;
using BanditMilitias.Intelligence.Strategic;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BanditMilitias.Intelligence.AI
{

    public static class ScoringFunctions
    {

        private const float TROOP_COUNT_WEIGHT = 0.4f;
        private const float TROOP_QUALITY_WEIGHT = 0.3f;
        private const float MORALE_WEIGHT = 0.2f;
        private const float EQUIPMENT_WEIGHT = 0.1f;
        private static readonly Dictionary<MobileParty, (float strength, float timestamp)> _strengthCache = new();
        private const float CACHE_DURATION = 1f;

        // OPTIMIZASYON: Yüksek frekanslı AI kararları için [ThreadStatic] bufferlar
        [ThreadStatic] private static List<MobileParty>? _nearbyBuffer;
        
        // Temizlik işlemleri için statik bufferlar (düşük frekans ama güvenli)
        private static readonly List<MobileParty> _strengthKeysBuffer = new(128);
        private static readonly List<AttackScoreKey> _attackKeysBuffer = new(128);

        public static float CalculatePartyStrength(MobileParty party)
        {
            if (party == null || !party.IsActive) return 0f;

            float currentTime = (float)TaleWorlds.CampaignSystem.CampaignTime.Now.ToHours;

            if (_strengthCache.TryGetValue(party, out var cached))
            {
                if (currentTime - cached.timestamp < CACHE_DURATION)
                {
                    return cached.strength;
                }
            }

            float troopScore = CalculateTroopScore(party);
            float qualityScore = CalculateQualityScoreOptimized(party);
            float moraleScore = CalculateMoraleScore(party);
            float equipmentScore = CalculateEquipmentScore(party);

            float strength = (troopScore * TROOP_COUNT_WEIGHT) +
                           (qualityScore * TROOP_QUALITY_WEIGHT) +
                           (moraleScore * MORALE_WEIGHT) +
                           (equipmentScore * EQUIPMENT_WEIGHT);

            _strengthCache[party] = (strength, currentTime);

            return strength;
        }

        private static float CalculateTroopScore(MobileParty party)
        {
            int troopCount = party.MemberRoster?.TotalManCount ?? 0;

            float score = (float)Math.Pow(troopCount, 1.2f);

            return Core.MathUtils.Clamp(score / 2.5f, 0f, 100f);
        }

        private static float CalculateQualityScoreOptimized(MobileParty party)
        {
            if (party.MemberRoster == null || party.MemberRoster.TotalManCount == 0)
                return 0f;

            float totalTier = 0f;
            int count = 0;

            foreach (var element in party.MemberRoster.GetTroopRoster())
            {
                if (element.Character != null)
                {
                    totalTier += element.Character.Tier * element.Number;
                    count += element.Number;
                }
            }

            if (count == 0) return 0f;

            float avgTier = totalTier / count;
            return Core.MathUtils.Clamp(avgTier / 7f * 100f, 0f, 100f);
        }

        private static float CalculateMoraleScore(MobileParty party)
        {
            // MilitiaMoraleSystem'den gerçek moral puanı al; vanilla party.Morale fallback
            try
            {
                if (party.PartyComponent is BanditMilitias.Components.MilitiaPartyComponent)
                {
                    float customMorale = Systems.Combat.MilitiaMoraleSystem.Instance.GetMorale(party.StringId);
                    return Core.MathUtils.Clamp(customMorale, 0f, 100f);
                }
            }
            catch { }
            return Core.MathUtils.Clamp(party.Morale, 0f, 100f);
        }

        private static float CalculateEquipmentScore(MobileParty party)
        {
            if (party.PartyComponent is BanditMilitias.Components.MilitiaPartyComponent)
            {
                bool enhanced = Settings.Instance?.EnhancedBandits ?? true;
                return enhanced ? 60f : 30f;
            }
            return 50f;
        }

        private struct AttackScoreKey
        {

            public readonly MobileParty Attacker;
            public readonly Settlement Target;

            public AttackScoreKey(MobileParty attacker, Settlement target)
            {
                Attacker = attacker;
                Target = target;
            }

            public readonly override bool Equals(object? obj)
            {
                if (obj is AttackScoreKey other)
                    return Attacker == other.Attacker && Target == other.Target;
                return false;
            }

            public readonly override int GetHashCode()
            {

                return (Attacker.GetHashCode() * 397) ^ Target.GetHashCode();
            }
        }

        private static readonly Dictionary<AttackScoreKey, (float score, float timestamp)> _attackScoreCache = new();

        public static float CalculateAttackScore(MobileParty attacker, Settlement target)
        {
            if (attacker == null || target == null) return 0f;

            float currentTime = (float)TaleWorlds.CampaignSystem.CampaignTime.Now.ToHours;
            AttackScoreKey key = new AttackScoreKey(attacker, target);

            if (_attackScoreCache.TryGetValue(key, out var cached))
            {
                if (currentTime - cached.timestamp < CACHE_DURATION)
                {
                    return cached.score;
                }
            }

            float powerRatio = CalculatePowerRatio(attacker, target);
            float resourceGain = EstimateResourceGain(target);
            float riskFactor = CalculateRiskOptimized(attacker, target);

            float score = (powerRatio * 0.5f) +
                         (resourceGain * 0.3f) -
                         (riskFactor * 0.2f);

            if (TaleWorlds.CampaignSystem.Campaign.Current != null && TaleWorlds.CampaignSystem.Campaign.Current.IsNight)
            {
                score += Settings.Instance?.NightAmbushBonus ?? 15f;
            }

            if (TaleWorlds.CampaignSystem.Campaign.Current != null && TaleWorlds.CampaignSystem.Campaign.Current.MapSceneWrapper != null)
            {
                TaleWorlds.Library.Vec2 targetPos = new TaleWorlds.Library.Vec2(target.GatePosition.X, target.GatePosition.Y);
                TaleWorlds.CampaignSystem.Map.IMapScene mapScene = TaleWorlds.CampaignSystem.Campaign.Current.MapSceneWrapper;

                TaleWorlds.Library.PathFaceRecord face = mapScene.GetFaceIndex(BanditMilitias.Infrastructure.CompatibilityLayer.CreateCampaignVec2(targetPos, true));

                if (face.IsValid())
                {
                    var terrain = mapScene.GetFaceTerrainType(face);
                    if (terrain == TaleWorlds.Core.TerrainType.Forest)
                    {
                        score += Settings.Instance?.TerrainAdvantageBonus ?? 10f;
                    }
                }
            }

            float patrolPenalty = PatrolDetection.CalculatePatrolPenalty(target, attacker);

            if (attacker.LeaderHero != null && attacker.LeaderHero.GetSkillValue(TaleWorlds.Core.DefaultSkills.Leadership) >= 150)
            {

                patrolPenalty *= 0.1f;
                score += 20f;
            }

            score += patrolPenalty;

            if (powerRatio > 150f)
            {
                score += 15f;
            }

            score = Core.MathUtils.Clamp(score, 0f, 100f);

            _attackScoreCache[key] = (score, currentTime);

            return score;
        }

        public static float CalculateAttackScore(MobileParty attacker, MobileParty target)
        {
            if (attacker == null || target == null || !attacker.IsActive || !target.IsActive) return 0f;

            float powerRatio = CalculatePowerRatio(attacker, target);
            if (powerRatio < 80f) return 0f;

            float lootScore = EstimateTargetLoot(target);

            float speedFactor = attacker.Speed > target.Speed ? 10f : -20f;

            float score = (powerRatio * 0.4f) + (lootScore * 0.2f) + speedFactor;

            if (target.IsMainParty)
            {
                var home = (attacker.PartyComponent as BanditMilitias.Components.MilitiaPartyComponent)?.GetHomeSettlement();
                if (home != null)
                {
                    var rep = BanditMilitias.Systems.Tracking.PlayerTracker.Instance.GetReputation(home);
                    if (rep != null)
                    {

                        if (rep.IsArchEnemy) score += 50f;
                        else if (rep.IsNemesis) score += 30f;
                        else if (rep.IsInfamous) score += 15f;
                    }

                    var warlord = WarlordSystem.Instance.GetWarlordForHideout(home);
                    if (warlord != null && warlord.IsAlive)
                    {

                        if (target.MemberRoster.TotalManCount > 50) score += 10f;

                        if (warlord.Personality == PersonalityType.Aggressive) score += 15f;
                    }
                }
            }

            if (target.IsCaravan) score += 20f;
            if (target.IsVillager) score += 10f;

            return Core.MathUtils.Clamp(score, 0f, 100f);
        }

        private static float CalculatePowerRatio(MobileParty attacker, MobileParty target)
        {
            float myStr = CalculatePartyStrength(attacker);
            float targetStr = CalculatePartyStrength(target);
            if (targetStr < 1f) return 100f;

            float ratio = myStr / targetStr;
            return Core.MathUtils.Sigmoid((ratio - 1.0f) * 2f) * 100f;
        }

        private static float CalculatePowerRatio(MobileParty attacker, Settlement target)
        {
            float attackerPower = CalculatePartyStrength(attacker);
            float defenderPower = CalculateSettlementDefense(target);

            if (defenderPower < 1f) return 100f;

            float ratio = attackerPower / defenderPower;
            return Core.MathUtils.Sigmoid((ratio - 1.5f) * 2f) * 100f;
        }

        public static float CalculateSettlementDefense(Settlement settlement)
        {
            if (settlement == null) return 0f;
            float defense = 0f;

            if (settlement.Town != null && settlement.Town.GarrisonParty != null)
            {
                defense += CalculatePartyStrength(settlement.Town.GarrisonParty);
            }

            foreach (var party in settlement.Parties)
            {
                if (party.IsMilitia || party.IsGarrison)
                {
                    defense += CalculatePartyStrength(party);
                }
            }

            if (settlement.IsTown || settlement.IsCastle)
            {
                defense *= 1.5f;
            }

            return defense;
        }

        public static float EstimateResourceGain(Settlement target)
        {
            if (target == null) return 0f;

            if (target.IsVillage)
            {
                float prosperity = target.Village?.Hearth ?? 100f;
                return Core.MathUtils.Sigmoid((prosperity - 300f) / 150f) * 100f;
            }
            else if (target.IsTown)
            {

                float prosperity = target.Town?.Prosperity ?? 1000f;
                return Core.MathUtils.Sigmoid((prosperity - 2000f) / 1000f) * 150f;
            }
            else if (target.IsCastle)
            {
                return 50f;
            }
            return 0f;
        }

        public static float EstimateTargetLoot(MobileParty targetParty)
        {
            if (targetParty == null) return 0f;

            if (targetParty.IsCaravan) return 120f;
            if (targetParty.IsVillager) return 30f;

            if (targetParty.LeaderHero != null) return 50f;

            return 10f;
        }

        private static float CalculateRiskOptimized(MobileParty attacker, Settlement target)
        {
            Vec2 targetPos = new(target.GatePosition.X, target.GatePosition.Y);
            int nearbyEnemies = 0;
            float totalEnemyStrength = 0f;

            // OPTIMIZASYON: Buffer kullan, her seferinde new List() yapma
            _nearbyBuffer ??= new List<MobileParty>(64);
            _nearbyBuffer.Clear();
            MilitiaSmartCache.Instance.GetNearbyParties(targetPos, 15f, _nearbyBuffer);

            foreach (var party in _nearbyBuffer)
            {
                if (party == null || !party.IsActive || party == attacker) continue;
                if (party.MapFaction == null || attacker.MapFaction == null) continue;
                if (!party.MapFaction.IsAtWarWith(attacker.MapFaction)) continue;

                nearbyEnemies++;
                totalEnemyStrength += CalculatePartyStrength(party);
            }

            if (nearbyEnemies == 0) return 10f;

            return Core.MathUtils.Clamp(nearbyEnemies * 15f + totalEnemyStrength * 0.3f, 0f, 100f);
        }

        public static float CalculatePatrolValue(Vec2 position, Settlement homeHideout)
        {
            if (homeHideout == null || !homeHideout.IsActive) return 0f;

            Vec2 homePos = new(homeHideout.GatePosition.X, homeHideout.GatePosition.Y);
            float distanceFromHome = position.Distance(homePos);

            float optimalDistance = 30f;
            float distanceScore = 100f - Math.Abs(distanceFromHome - optimalDistance) * 2f;

            int nearbyVillages = 0;

            var villages = BanditMilitias.Infrastructure.ModuleManager.Instance.VillageCache;
            foreach (var settlement in villages)
            {
                if (settlement != null && settlement.IsActive)
                {
                    Vec2 villagePos = new(settlement.GatePosition.X, settlement.GatePosition.Y);

                    if (position.DistanceSquared(villagePos) < 20f * 20f)
                    {
                        nearbyVillages++;
                    }
                }
            }

            float resourceScore = nearbyVillages * 20f;

            float tradeIntensity = BanditMilitias.Systems.Tracking.CaravanActivityTracker.Instance.GetTradeIntensity(position);
            float tradeBonus = tradeIntensity * 10f;

            return Core.MathUtils.Clamp((distanceScore * 0.5f + resourceScore * 0.3f + tradeBonus * 0.2f), 0f, 100f);
        }

        public static float CalculateDefenseScore(MobileParty defender, Settlement homeHideout)
        {
            if (defender == null || homeHideout == null || !homeHideout.IsActive) return 0f;

            Vec2 homePos = new(homeHideout.GatePosition.X, homeHideout.GatePosition.Y);

            // OPTIMIZASYON: Buffer kullan
            _nearbyBuffer ??= new List<MobileParty>(64);
            _nearbyBuffer.Clear();
            MilitiaSmartCache.Instance.GetNearbyParties(homePos, 30f, _nearbyBuffer);

            if (_nearbyBuffer.Count == 0) return 0f;

            float totalEnemyStrength = 0f;
            float maxEnemyStrength = 0f;
            bool enemiesAreClose = false;

            foreach (var enemy in _nearbyBuffer)
            {
                if (enemy == defender) continue;
                if (enemy.MapFaction != null && defender.MapFaction != null && enemy.MapFaction.IsAtWarWith(defender.MapFaction))
                {
                    float str = GetPartyStrengthCached(enemy);
                    totalEnemyStrength += str;
                    if (str > maxEnemyStrength) maxEnemyStrength = str;

                    Vec2 enemyPos = BanditMilitias.Infrastructure.CompatibilityLayer.GetPartyPosition(enemy);
                    if (enemyPos.DistanceSquared(homePos) < 15f * 15f)
                    {
                        enemiesAreClose = true;
                    }
                }
            }

            if (totalEnemyStrength < 10f) return 0f;

            float ourStrength = GetPartyStrengthCached(defender);

            foreach (var party in homeHideout.Parties)
            {
                if (party != defender && party.IsActive)
                {
                    ourStrength += GetPartyStrengthCached(party);
                }
            }

            if (enemiesAreClose)
            {

                if (defender.LeaderHero == null && totalEnemyStrength > ourStrength * 4f)
                {
                    return 10f;
                }
                return 95f;
            }

            if (ourStrength > totalEnemyStrength * 0.4f)
            {
                return 60f;
            }

            return 0f;
        }

        private static float GetPartyStrengthCached(MobileParty p)
        {
            return CalculatePartyStrength(p);
        }

        public static void CleanStaleCaches()
        {

            List<MobileParty> strengthKeysToRemove = new();
            foreach (var key in _strengthCache.Keys)
            {
                if (key == null || !key.IsActive)
                {
                    strengthKeysToRemove.Add(key!);
                }
            }
            foreach (var key in strengthKeysToRemove)
            {
                _ = _strengthCache.Remove(key);
            }

            List<AttackScoreKey> attackKeysToRemove = new();
            foreach (var key in _attackScoreCache.Keys)
            {
                if (key.Attacker == null || !key.Attacker.IsActive ||
                    key.Target == null || !key.Target.IsActive)
                {
                    attackKeysToRemove.Add(key);
                }
            }
            foreach (var key in attackKeysToRemove)
            {
                _ = _attackScoreCache.Remove(key);
            }
        }

        public static void OnPartyDestroyed(MobileParty party)
        {
            _ = _strengthCache.Remove(party);

            List<AttackScoreKey> keysToRemove = new();
            foreach (var key in _attackScoreCache.Keys)
            {
                if (key.Attacker == party)
                {
                    keysToRemove.Add(key);
                }
            }
            foreach (var key in keysToRemove)
            {
                _ = _attackScoreCache.Remove(key);
            }
        }

        public static List<Settlement> GetNearbyVillages(Vec2 position, float radius)
        {
            List<Settlement> nearby = new List<Settlement>();
            GetNearbyVillages(position, radius, nearby);
            return nearby;
        }

        public static void GetNearbyVillages(Vec2 position, float radius, List<Settlement> results)
        {
            if (results == null) return;
            float radiusSq = radius * radius;

            var villages = BanditMilitias.Infrastructure.ModuleManager.Instance.VillageCache;
            foreach (var settlement in villages)
            {
                if (settlement != null && settlement.IsActive)
                {
                    Vec2 villagePos = new(settlement.GatePosition.X, settlement.GatePosition.Y);
                    if (position.DistanceSquared(villagePos) <= radiusSq)
                    {
                        results.Add(settlement);
                    }
                }
            }
        }

        public static float CalculateVillageRaidScore(MobileParty attacker, Settlement village)
        {
            return CalculateAttackScore(attacker, village);
        }

        public static List<MobileParty> GetNearbyEnemies(Vec2 position, float radius)
        {
            List<MobileParty> results = new List<MobileParty>();
            GetNearbyEnemies(position, radius, results);
            return results;
        }

        public static void GetNearbyEnemies(Vec2 position, float radius, List<MobileParty> results)
        {
            if (results == null) return;
            MilitiaSmartCache.Instance.GetNearbyParties(position, radius, results);
        }

        public static float CalculateCombatScore(MobileParty attacker, MobileParty defender)
        {
            return CalculateAttackScore(attacker, defender);
        }
    }
}