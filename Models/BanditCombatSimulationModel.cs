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


    [HarmonyPatch]
    public class BanditCombatSimulationDamagePatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            var target = TargetMethod();
            if (target == null)
            {
                BanditMilitias.Infrastructure.FileLogger.LogWarning("[BanditCombatSimulationDamagePatch] Target method 'GetSimulationDamage' not found. Patch skipped.");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        public static System.Reflection.MethodBase TargetMethod()
        {
            // Bannerlord 1.2+ uses out parameter and void return.
            // 1.1 and older used int return.
            var type = AccessTools.TypeByName("TaleWorlds.CampaignSystem.GameComponents.DefaultCombatSimulationModel")
                    ?? AccessTools.TypeByName("TaleWorlds.CampaignSystem.Models.CombatSimulationModel");
            
            if (type == null) return null;

            return AccessTools.Method(type, "GetSimulationDamage");
        }

        [HarmonyPostfix]
        public static void PostfixDamage(PartyBase attackerParty, PartyBase defenderParty, ref int damage)
        {
            if (attackerParty == null || defenderParty == null) return;

            var attackerMobile = attackerParty.MobileParty;
            var defenderMobile = defenderParty.MobileParty;

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

            damage = (int)(damage * attackMult * defenseMult);
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

    [HarmonyPatch]
    public class BanditCombatSimulationCasualtiesPatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            var target = TargetMethod();
            if (target == null)
            {
                BanditMilitias.Infrastructure.FileLogger.LogWarning("[BanditCombatSimulationCasualtiesPatch] Target method 'GetSimulationCasualties' not found. Patch skipped.");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        public static System.Reflection.MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("TaleWorlds.CampaignSystem.GameComponents.DefaultCombatSimulationModel")
                    ?? AccessTools.TypeByName("TaleWorlds.CampaignSystem.Models.CombatSimulationModel");
            
            if (type == null) return null;

            return AccessTools.Method(type, "GetSimulationCasualties");
        }

        [HarmonyPostfix]
        public static void PostfixCasualties(PartyBase attackerParty, PartyBase defenderParty, ref int casualties)
        {
            if (defenderParty == null) return;

            var defenderMobile = defenderParty.MobileParty;
            if (defenderMobile?.PartyComponent is MilitiaPartyComponent && defenderMobile.MemberRoster != null)
            {
                float t1Ratio = GetLowTierRatio(defenderMobile);
                if (t1Ratio > 0.5f)
                {
                    float reduction = t1Ratio * 0.40f;
                    casualties = (int)(casualties * (1f - reduction));
                }
            }
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
