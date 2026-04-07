using BanditMilitias.Debug;
using BanditMilitias.Intelligence.Strategic;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace BanditMilitias.Patches
{
    [HarmonyPatch]
    public static class BanditAiPatch
    {
        private static readonly string[] CandidateTypeNames =
        {
            "TaleWorlds.CampaignSystem.CampaignBehaviors.BanditsCampaignBehavior",
            "TaleWorlds.CampaignSystem.CampaignBehaviors.BanditPartiesCampaignBehavior",
            "TaleWorlds.CampaignSystem.SandBox.CampaignBehaviors.BanditsCampaignBehavior"
        };

        private static readonly string[] CandidateMethodNames =
        {
            "ThinkAboutBanditParty",
            "AiHourlyTick",
            "HourlyTick",
            "TickParty",
            "MakeBanditsWaitAroundHideouts",
            "TickMilitia"
        };

        [HarmonyPrepare]
        private static bool Prepare()
        {
            var targets = ResolveTargetMethods().ToList();
            if (targets.Count == 0)
            {
                DebugLogger.Warning("BanditAiPatch",
                    "No compatible bandit AI target method found. Patch will be skipped and vanilla AI will continue.");
                return false;
            }

            DebugLogger.Info("BanditAiPatch", $"Resolved {targets.Count} compatible target method(s).");
            return true;
        }

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods() => ResolveTargetMethods();

        private static IEnumerable<MethodBase> ResolveTargetMethods()
        {
            var resolved = new List<MethodBase>(4);

            foreach (string typeName in CandidateTypeNames)
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null) continue;

                foreach (string methodName in CandidateMethodNames)
                {
                    var withThinkParams = AccessTools.Method(type, methodName,
                        new[] { typeof(MobileParty), typeof(PartyThinkParams) });
                    if (withThinkParams != null)
                    {
                        resolved.Add(withThinkParams);
                    }

                    var withOnlyParty = AccessTools.Method(type, methodName, new[] { typeof(MobileParty) });
                    if (withOnlyParty != null)
                    {
                        resolved.Add(withOnlyParty);
                    }
                }
            }

            return resolved.Distinct();
        }

        [HarmonyPrefix]
        public static bool Prefix([HarmonyArgument(0)] MobileParty banditParty)
        {
            if (banditParty == null) return true;

            try
            {
                var warlordSystem = WarlordSystem.Instance;
                var careerSystem = Systems.Progression.WarlordCareerSystem.Instance;
                if (warlordSystem == null || careerSystem == null) return true;

                var warlord = warlordSystem.GetWarlordForParty(banditParty);
                if (warlord == null) return true;

                var tier = careerSystem.GetTier(warlord.StringId);
                bool handled = HTNEngine.ExecutePlan(banditParty, tier);

                return !handled;
            }
            catch (Exception ex)
            {
                DebugLogger.Error("BanditAiPatch",
                    $"Custom bandit AI failed for {banditParty.StringId ?? banditParty.Name?.ToString() ?? "unknown"}: {ex.Message}. Falling back to vanilla AI.");
                return true;
            }
        }
    }
}
