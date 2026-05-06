using BanditMilitias.Infrastructure;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BanditMilitias.Intelligence.AI.Components
{
    public static class MilitiaActionExecutor
    {
        public static void ExecuteRaid(MobileParty party, Settlement target)
        {
            if (target == null) return;
            CompatibilityLayer.SetMoveRaidSettlement(party, target);
            party.Aggressiveness = 2.0f;
        }

        public static void ExecuteAmbush(MobileParty party)
        {
            if (Campaign.Current?.MapSceneWrapper != null)
            {
                Vec2 currentPos = CompatibilityLayer.GetPartyPosition(party);
                var mapScene = Campaign.Current.MapSceneWrapper;
                Vec2 targetPos = currentPos;
                bool foundTerrain = false;

                for (int i = 0; i < 6; i++)
                {
                    float angle = i * ((float)System.Math.PI * 2f / 6f);
                    float r = 5f;
                    Vec2 checkPos = currentPos + new Vec2((float)System.Math.Cos(angle) * r, (float)System.Math.Sin(angle) * r);

                    if (checkPos.IsValid)
                    {
                        var campaignCheckPos = CompatibilityLayer.CreateCampaignVec2(checkPos);
                        var point = mapScene.GetAccessiblePointNearPosition(campaignCheckPos, 1f);
                        if (!float.IsNaN(point.X) && !float.IsNaN(point.Y))
                        {
                            var faceIndex = mapScene.GetFaceIndex(point);
                            if (faceIndex.IsValid())
                            {
                                var terrainType = mapScene.GetFaceTerrainType(faceIndex);
                                if (terrainType == TaleWorlds.Core.TerrainType.Forest ||
                                    terrainType == TaleWorlds.Core.TerrainType.Canyon ||
                                    terrainType == TaleWorlds.Core.TerrainType.Steppe)
                                {
                                    targetPos = new Vec2(point.X, point.Y);
                                    foundTerrain = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (foundTerrain)
                {
                    CompatibilityLayer.SetMoveGoToPoint(party, targetPos);
                }
                else
                {
                    CompatibilityLayer.SetMoveGoToPoint(party, currentPos);
                }
            }
            else
            {
                CompatibilityLayer.SetMoveGoToPoint(party, CompatibilityLayer.GetPartyPosition(party));
            }

            party.Aggressiveness = 1.0f;
        }

        public static void ExecuteFlee(MobileParty party, MobileParty threat)
        {
            if (threat == null) return;

            Vec2 partyPos = CompatibilityLayer.GetPartyPosition(party);
            Vec2 threatPos = CompatibilityLayer.GetPartyPosition(threat);

            Vec2 fleeDir = (partyPos - threatPos).Normalized();
            Vec2 fleeDest = partyPos + fleeDir * 50f;

            CompatibilityLayer.SetMoveGoToPoint(party, fleeDest);
            party.Aggressiveness = 0.0f;
        }

        public static void ExecuteEngage(MobileParty party, MobileParty target)
        {
            if (target == null) return;
            CompatibilityLayer.SetMoveEngageParty(party, target);
            party.Aggressiveness = 1.5f;
        }

        public static void ExecutePatrol(MobileParty party, Settlement? home, Vec2? patrolPoint = null)
        {
            if (patrolPoint.HasValue)
            {
                CompatibilityLayer.SetMoveGoToPoint(party, patrolPoint.Value);
            }
            else if (home != null)
            {

                CompatibilityLayer.SetMovePatrolAroundSettlement(party, home);
            }

            party.Aggressiveness = 1.0f;
        }
    }
}