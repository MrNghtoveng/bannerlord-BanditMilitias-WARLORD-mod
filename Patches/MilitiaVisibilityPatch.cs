using BanditMilitias.Components;
using BanditMilitias.Infrastructure;
using HarmonyLib;
using System.Reflection;
using TaleWorlds.CampaignSystem.Party;

namespace BanditMilitias.Patches
{
    /// <summary>
    /// Patches MobileParty.IsVisible to support mod-specific visibility rules.
    /// This restores the "Militia Markers" and "Track Size" features which lack a native GameModel override.
    /// </summary>
    [HarmonyPatch(typeof(MobileParty), "IsVisible", MethodType.Getter)]
    public class MilitiaVisibilityPatch
    {
        /// <summary>
        /// Pre-flight guard: Harmony calls this before attempting to patch.
        /// Returns false if MobileParty.IsVisible getter no longer exists in this game version,
        /// so the patch is skipped gracefully instead of throwing and entering Degraded mode.
        /// </summary>
        [HarmonyPrepare]
        static bool Prepare()
        {
            var getter = AccessTools.PropertyGetter(typeof(MobileParty), "IsVisible");
            if (getter == null)
            {
                FileLogger.LogWarning(
                    "[MilitiaVisibilityPatch] MobileParty.IsVisible getter not found in this " +
                    "game version — patch skipped gracefully. Markers/TrackSize features disabled.");
                return false;
            }
            FileLogger.Log("[MilitiaVisibilityPatch] Target getter verified — patch will be applied.");
            return true;
        }

        public static void Postfix(MobileParty __instance, ref bool __result)
        {
            if (__instance == null || !__instance.IsMilitia())
                return;

            // 1. Force visibility if Markers are enabled
            if (Settings.Instance?.MilitiaMarkers == true)
            {
                __result = true;
                return;
            }

            // 2. Hide small parties based on TrackSize threshold
            int troopCount = __instance.MemberRoster?.TotalManCount ?? 0;
            if (troopCount < (Settings.Instance?.TrackSize ?? 10))
            {
                __result = false;
            }
        }
    }
}
