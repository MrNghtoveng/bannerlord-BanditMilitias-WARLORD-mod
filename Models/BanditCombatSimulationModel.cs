using BanditMilitias.Components;
using BanditMilitias.Infrastructure;
using HarmonyLib;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace BanditMilitias.Models
{
    // Bannerlord versiyonları arasındaki imza değişikliklerini (MobileParty vs MapEventParty vs PartyBase)
    // tolere etmek için override yerine Harmony yaması kullanıyoruz.
    [HarmonyPatch]
    public class BanditCombatSimulationPatch
    {
        // ── GetSimulationDamage Yaması ──────────────────────────────────
        [HarmonyTargetMethod]
        public static System.Reflection.MethodBase TargetMethod1()
        {
            return AccessTools.Method("TaleWorlds.CampaignSystem.GameComponents.DefaultCombatSimulationModel:GetSimulationDamage")
                ?? AccessTools.Method("TaleWorlds.CampaignSystem.Models.CombatSimulationModel:GetSimulationDamage");
        }

        [HarmonyPostfix]
        public static void PostfixDamage(ref int __result, object attackerParty, object defenderParty)
        {
            // Parametreler farklı tiplerde (MobileParty/MapEventParty/PartyBase) gelebileceği için object olarak alıp çözüyoruz.
            PartyBase? attacker = ResolveParty(attackerParty);
            PartyBase? defender = ResolveParty(defenderParty);

            if (attacker == null || defender == null) return;

            var attackerMobile = attacker.MobileParty;
            var defenderMobile = defender.MobileParty;

            float attackMult = 1.0f;
            if (attackerMobile?.PartyComponent is MilitiaPartyComponent attackerComp)
            {
                if (attackerComp.CurrentOrder?.Type == Intelligence.Strategic.CommandType.Ambush)
                    attackMult += 0.75f;

                int attackerCount = attackerMobile.MemberRoster?.TotalManCount ?? 0;
                int defenderCount = defenderMobile?.MemberRoster?.TotalManCount ?? 1;
                if (attackerCount > defenderCount * 1.5f)
                    attackMult += 0.20f;

                if (Campaign.Current?.IsNight == true)
                    attackMult += 0.25f;
            }

            float defenseMult = 1.0f;
            if (defenderMobile?.PartyComponent is MilitiaPartyComponent && defenderMobile.MemberRoster != null)
            {
                float t1Ratio = GetLowTierRatio(defenderMobile);
                if (t1Ratio > 0.5f)
                    defenseMult = 1.0f - (t1Ratio * 0.35f);
            }

            __result = (int)(__result * attackMult * defenseMult);
        }

        // ── GetSimulationCasualties Yaması ──────────────────────────────
        [HarmonyTargetMethod]
        public static System.Reflection.MethodBase TargetMethod2()
        {
            return AccessTools.Method("TaleWorlds.CampaignSystem.GameComponents.DefaultCombatSimulationModel:GetSimulationCasualties")
                ?? AccessTools.Method("TaleWorlds.CampaignSystem.Models.CombatSimulationModel:GetSimulationCasualties");
        }

        [HarmonyPostfix]
        public static void PostfixCasualties(ref int __result, object attackerParty, object defenderParty)
        {
            PartyBase? defender = ResolveParty(defenderParty);
            if (defender == null) return;

            var defenderMobile = defender.MobileParty;
            if (defenderMobile?.PartyComponent is MilitiaPartyComponent && defenderMobile.MemberRoster != null)
            {
                float t1Ratio = GetLowTierRatio(defenderMobile);
                if (t1Ratio > 0.5f)
                {
                    float reduction = t1Ratio * 0.40f;
                    __result = (int)(__result * (1f - reduction));
                }
            }
        }

        // ── Yardımcılar ────────────────────────────────────────────────
        private static PartyBase? ResolveParty(object obj)
        {
            if (obj is PartyBase pb) return pb;
            if (obj is MobileParty mp) return mp.Party;
            if (obj is MapEventParty mep) return mep.Party;
            return null;
        }

        private static float GetLowTierRatio(MobileParty party)
        {
            int total = party.MemberRoster.TotalManCount;
            if (total <= 0) return 0f;

            int lowTierCount = 0;
            foreach (var troop in party.MemberRoster.GetTroopRoster())
            {
                if (troop.Character != null && troop.Character.Tier <= 2)
                    lowTierCount += troop.Number;
            }
            return (float)lowTierCount / total;
        }
    }
}
