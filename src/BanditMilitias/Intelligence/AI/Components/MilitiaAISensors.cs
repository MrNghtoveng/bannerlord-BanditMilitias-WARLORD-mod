using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BanditMilitias.Intelligence.AI.Components
{

    public class MilitiaAISensors
    {
        private readonly MobileParty _party;
        private readonly Vec2 _position;
        private readonly float _currentTime;

        private List<MobileParty>? _nearbyEnemies;
        private List<MobileParty>? _nearbyFriendlies;
        private List<Settlement>? _nearbyVillages;
        private List<BattleSite>? _nearbyBattleSites;
        private float _detectionRadius;

        public MilitiaAISensors(MobileParty party, float detectionRadius = 40f)
        {
            _party = party;
            _position = CompatibilityLayer.GetPartyPosition(party);
            _currentTime = (float)CampaignTime.Now.ToHours;
            _detectionRadius = detectionRadius;
        }

        public List<MobileParty> GetNearbyEnemies()
        {
            if (_nearbyEnemies != null) return _nearbyEnemies;

            _nearbyEnemies = new List<MobileParty>();
            var allNearby = new List<MobileParty>();

            MilitiaSmartCache.Instance.GetNearbyParties(_position, _detectionRadius, allNearby);

            foreach (var p in allNearby)
            {
                if (p == _party || !p.IsActive) continue;
                if (p.MapFaction == null || _party.MapFaction == null) continue;
                if (p.MapFaction.IsAtWarWith(_party.MapFaction))
                    _nearbyEnemies.Add(p);
            }
            return _nearbyEnemies;
        }

        public List<MobileParty> GetNearbyFriendlies()
        {
            if (_nearbyFriendlies != null) return _nearbyFriendlies;

            _nearbyFriendlies = new List<MobileParty>();
            var allNearby = new List<MobileParty>();
            MilitiaSmartCache.Instance.GetNearbyParties(_position, _detectionRadius, allNearby);

            foreach (var p in allNearby)
            {
                if (p == _party || !p.IsActive) continue;
                if (p.MapFaction == _party.MapFaction)
                    _nearbyFriendlies.Add(p);
            }
            return _nearbyFriendlies;
        }

        public List<Settlement> GetNearbyVillages(float searchRadius = 60f)
        {
            if (_nearbyVillages != null) return _nearbyVillages;

            _nearbyVillages = new List<Settlement>();
            var villages = StaticDataCache.Instance.AllVillages;
            float radiusSq = searchRadius * searchRadius;

            foreach (var v in villages)
            {
                if (v == null || !v.IsActive || v.IsUnderRaid) continue;

                var vPos = CompatibilityLayer.GetSettlementPosition(v);
                if (_position.DistanceSquared(vPos) <= radiusSq)
                    _nearbyVillages.Add(v);
            }
            return _nearbyVillages;
        }

        public List<BattleSite> GetNearbyBattleSites(float searchRadius = 50f)
        {
            if (_nearbyBattleSites != null) return _nearbyBattleSites;

            _nearbyBattleSites = new List<BattleSite>();
            float radiusSq = searchRadius * searchRadius;

            try
            {
                var territorySystem = ModuleManager.Instance.GetModule<BanditMilitias.Systems.Territory.TerritorySystem>();
                if (territorySystem != null)
                {
                    var allBattleSites = territorySystem.GetRecentBattleSites();
                    if (allBattleSites != null)
                    {
                        foreach (var site in allBattleSites)
                        {
                            if (site == null) continue;
                            if (_position.DistanceSquared(site.Position) <= radiusSq)
                            {
                                _nearbyBattleSites.Add(site);
                            }
                        }
                    }
                }
            }
            catch
            {

            }

            return _nearbyBattleSites;
        }

        public List<MobileParty> GetNearbyMilitias(float radius = 40f)
        {
            var result = new List<MobileParty>();
            var allNearby = new List<MobileParty>();
            MilitiaSmartCache.Instance.GetNearbyParties(_position, radius, allNearby);

            foreach (var p in allNearby)
            {
                if (p == _party || !p.IsActive) continue;
                if (p.PartyComponent is BanditMilitias.Components.MilitiaPartyComponent)
                    result.Add(p);
            }
            return result;
        }

        public bool IsWounded()
        {
            if (_party.MemberRoster == null) return false;
            return _party.MemberRoster.TotalWoundedRegulars > _party.MemberRoster.TotalManCount * 0.4f;
        }

        public bool IsNight() => Campaign.Current.IsNight;

        public bool IsInForest()
        {
            if (Campaign.Current?.MapSceneWrapper == null) return false;

            var face = Campaign.Current.MapSceneWrapper.GetFaceIndex(BanditMilitias.Infrastructure.CompatibilityLayer.CreateCampaignVec2(_position, true));
            if (!face.IsValid()) return false;

            var terrain = Campaign.Current.MapSceneWrapper.GetFaceTerrainType(face);
            return terrain == TaleWorlds.Core.TerrainType.Forest;
        }

        public Vec2 Position => _position;
    }
}