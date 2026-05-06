using BanditMilitias.Components;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Party;

namespace BanditMilitias.Patches
{
    // v1.3.15 FIX: HarmonyPatch disabled to prevent double-application of speed bonuses.
    // The logic is already handled by MilitiaSpeedModel (GameModels.cs) which overrides CalculateFinalSpeed.
    // v1.3.15 FIX: HarmonyPatch disabled to prevent double-application conflicts 
    // with MilitiaSpeedModel (GameModels.cs) which already overrides this method.
    // [HarmonyPatch(typeof(DefaultPartySpeedCalculatingModel), "CalculateFinalSpeed")]
    public class MilitiaSpeedPatch
    {
        public static void Postfix(MobileParty mobileParty, ref ExplainedNumber __result)
        {
            if (mobileParty == null || !mobileParty.IsActive) return;

            // Sadece Bandit Militias partileri için çalış
            var component = mobileParty.PartyComponent as MilitiaPartyComponent;
            if (component == null) return;

            // 1. Kervan Takibi (%25)
            if (mobileParty.TargetParty != null && mobileParty.TargetParty.IsCaravan)
            {
                __result.AddFactor(0.25f, new TaleWorlds.Localization.TextObject("Kervan Avcısı Bonusu"));
                return;
            }

            // 2. Milis Avcılığı (%15 - Predatory AI)
            if (mobileParty.TargetParty != null && mobileParty.TargetParty.PartyComponent is MilitiaPartyComponent)
            {
                __result.AddFactor(0.15f, new TaleWorlds.Localization.TextObject("Milis Avcısı Bonusu"));
                return;
            }

            // 3. Savaş Meydanı Leşçiliği (%10 - Scavenging)
            // Eğer bir hedef partiye doğru gidiyorsa ve o parti savaş halindeyse
            if (mobileParty.TargetParty != null && mobileParty.TargetParty.MapEvent != null)
            {
                __result.AddFactor(0.10f, new TaleWorlds.Localization.TextObject("Leşçi Hızı"));
                return;
            }
        }
    }
}
