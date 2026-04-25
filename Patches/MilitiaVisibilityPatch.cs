using BanditMilitias.Components;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.GameComponents;
// Bazı versiyonlarda SandBox içinde olabilir
// using SandBox.GameComponents; 

namespace BanditMilitias.Patches
{
    [HarmonyPatch("TaleWorlds.CampaignSystem.GameComponents.DefaultPartyVisibilityModel", "GetPartyVisibilityRange")]
    public class MilitiaVisibilityPatch
    {
        private static void Postfix(MobileParty party, ref float __result)
        {
            if (party == null || party.PartyComponent is not MilitiaPartyComponent) return;

            // 4. Rapor Revize: Görünmezlik / Lay Low Modu
            // Çok küçük partiler (< 12) haritada "Düşük Profil" (Low Profile) moduna girer.
            if (party.MemberRoster.TotalManCount < 12)
            {
                // Görünürlük menzilini %40 azalt (Lordlar onları daha zor fark eder)
                __result *= 0.6f;
            }
        }
    }
}
