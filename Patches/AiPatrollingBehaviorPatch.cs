// ══════════════════════════════════════════════════════════════════════════════
// DEPRECATED: This patch has been replaced by MilitiaBehavior.AiHourlyTick
// The [HarmonyPatch] attribute is removed so Harmony's PatchAll ignores this class.
// File kept for reference and to avoid breaking test source scanners.
// ══════════════════════════════════════════════════════════════════════════════

namespace BanditMilitias.Patches
{
    /// <summary>
    /// [STUB] Formerly intercepted vanilla AiPatrollingBehavior.AiHourlyTick
    /// to inject custom militia AI decisions. Now handled natively by
    /// <see cref="Behaviors.MilitiaBehavior.AiHourlyTick"/>.
    /// </summary>
    internal static class AiPatrollingBehaviorPatch
    {
        // Intentionally empty — logic moved to MilitiaBehavior.AiHourlyTick
    }
}
