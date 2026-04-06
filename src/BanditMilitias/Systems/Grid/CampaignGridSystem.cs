using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BanditMilitias.Systems.Grid
{
    public class CampaignGridSystem
    {
        // Haydutların hedeflerini bulurken tüm haritayı taramasını engeller
        public static Settlement? FindMostVulnerableTarget(MobileParty warlordParty, float maxRadius)
        {
            Settlement? bestTarget = null;
            float highestVulnerabilityScore = 0f;

            // Campaign.Current.Settlements, motorun kendi optimize edilmiş listesidir.
            foreach (Settlement settlement in Settlement.All)
            {
                // Uzaklık hesaplamasını oyunun kendi modeli üzerinden yapıyoruz (Unity Vector3 DEĞİL)
                TaleWorlds.Library.Vec2 partyPos = BanditMilitias.Infrastructure.CompatibilityLayer.GetPartyPosition(warlordParty);
                TaleWorlds.Library.Vec2 settlementPos = BanditMilitias.Infrastructure.CompatibilityLayer.GetSettlementPosition(settlement);
                float distance = partyPos.Distance(settlementPos);
                
                if (distance <= maxRadius && settlement.IsVillage)
                {
                    // Savunma gücü ve refah seviyesine göre kendi algoritmanızı burada çalıştırın
                    float score = CalculateVulnerability(settlement); 
                    if (score > highestVulnerabilityScore)
                    {
                        highestVulnerabilityScore = score;
                        bestTarget = settlement;
                    }
                }
            }
            return bestTarget;
        }

        private static float CalculateVulnerability(Settlement settlement)
        {
            // Basit bir örnek: Militia sayısı azsa ve refah (hearth) yüksekse saldır!
            float hearth = settlement.Village != null ? settlement.Village.Hearth : 0f;
            return hearth / (settlement.Militia + 1f); 
        }
    }
}
