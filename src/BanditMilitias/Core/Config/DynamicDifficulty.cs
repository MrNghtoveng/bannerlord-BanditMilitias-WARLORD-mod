using TaleWorlds.CampaignSystem.Settlements;

namespace BanditMilitias.Core.Config
{
    public static class DynamicDifficulty
    {
        public static float CalculateSpawnMultiplier()
            => BanditMilitias.DynamicDifficulty.CalculateSpawnMultiplier();

        public static int CalculateOptimalMilitiaCount()
            => BanditMilitias.DynamicDifficulty.CalculateOptimalMilitiaCount();

        public static float CalculateAdjustedSpawnChance(float baseChance)
            => BanditMilitias.DynamicDifficulty.CalculateAdjustedSpawnChance(baseChance);

        public static float CalculateMilitiaPowerMultiplier()
            => BanditMilitias.DynamicDifficulty.CalculateMilitiaPowerMultiplier();

        public static float CalculateWarMultiplier(Settlement hideout)
            => BanditMilitias.DynamicDifficulty.CalculateWarMultiplier(hideout);

        public static float CalculateTradeRouteMultiplier(Settlement hideout)
            => BanditMilitias.DynamicDifficulty.CalculateTradeRouteMultiplier(hideout);
    }
}
