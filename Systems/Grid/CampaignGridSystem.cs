using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BanditMilitias.Systems.Grid
{
    public class CampaignGridSystem
    {
        private static List<Settlement>? _cachedVillages;
        private static CampaignTime _lastCacheTime = CampaignTime.Zero;

        // Önbellek artık WorldMemory.Bedrock üzerinden yönetiliyor
        public static Settlement? FindMostVulnerableTarget(MobileParty warlordParty, float maxRadius)
        {
            if (warlordParty == null) return null;

            TaleWorlds.Library.Vec2 partyPos = Infrastructure.CompatibilityLayer.GetPartyPosition(warlordParty);
            
            // WorldMemory Bedrock üzerinden optimize edilmiş kNN ve Spatial sorgu
            var nearbySettlements = Core.Memory.WorldMemory.Bedrock.GetNearest(partyPos, 15, maxRadius);

            Settlement? bestTarget = null;
            float highestVulnerabilityScore = 0f;

            foreach (var settlement in nearbySettlements)
            {
                if (!settlement.IsVillage || !settlement.IsActive) continue;

                // Haritacılık/Hafıza Değeri: Bölgesel refah ve stratejik zeka verisi (Mapping Value)
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
            // Refah (hearth) / (savunma + 1)
            float hearth = settlement.Village?.Hearth ?? 0f;
            // Milis gücü + sabit bir "zorluk" çarpanı
            return hearth / (settlement.Militia + 5f); 
        }

        public static void ResetCache()
        {
            _cachedVillages = null;
            _lastCacheTime = CampaignTime.Zero;
        }
    }
}

