using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BanditMilitias.Systems.Grid
{
    public class CampaignGridSystem
    {
        private static CampaignTime _lastCacheTime = CampaignTime.Zero;


        public static Settlement? FindMostVulnerableTarget(MobileParty warlordParty, float maxRadius)
        {
            if (warlordParty == null) return null;

            TaleWorlds.Library.Vec2 partyPos = Infrastructure.CompatibilityLayer.GetPartyPosition(warlordParty);


            var nearbySettlements = Core.Memory.WorldMemory.Bedrock.GetNearest(partyPos, 15, maxRadius);

            Settlement? bestTarget = null;
            float highestVulnerabilityScore = 0f;

            foreach (var settlement in nearbySettlements)
            {
                if (!settlement.IsVillage || !settlement.IsActive) continue;


                float mappingValue = Core.Memory.WorldMemory.Geology.GetRegionalProsperity(settlement.StringId);
                float score = CalculateVulnerability(settlement) * (1.0f + (mappingValue * 0.05f));

                if (score > highestVulnerabilityScore)
                {
                    highestVulnerabilityScore = score;
                    bestTarget = settlement;
                }
            }
            return bestTarget;
        }

        private static float CalculateVulnerability(Settlement settlement)
        {


            float hearth = settlement.Village?.Hearth ?? 0f;


            return hearth / (settlement.Militia + 5f);
        }

        public static void ResetCache()
        {
            _lastCacheTime = CampaignTime.Zero;
        }
    }
}
