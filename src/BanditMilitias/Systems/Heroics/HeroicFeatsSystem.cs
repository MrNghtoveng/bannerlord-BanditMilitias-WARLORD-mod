using BanditMilitias.Core.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace BanditMilitias.Systems.Heroics
{
    // ── HeroicFeatsSystem ─────────────────────────────────────────

    [AutoRegister]
    public class HeroicFeatsSystem : MilitiaModuleBase
    {
        private static readonly Lazy<HeroicFeatsSystem> _instance =
            new Lazy<HeroicFeatsSystem>(() => new HeroicFeatsSystem());

        public static HeroicFeatsSystem Instance => _instance.Value;

        private HeroicFeatsSystem() { }

        public override string ModuleName => "HeroicFeatsSystem";
        public override bool IsEnabled => Settings.Instance?.EnableHeroicFeats ?? true;
        public override int Priority => 40;

        private List<ItemObject>? _cachedRewardItems;

        public override void Initialize()
        {
            _cachedRewardItems ??= new List<ItemObject>();
        }

        public override void Cleanup()
        {
            _cachedRewardItems?.Clear();
            _cachedRewardItems = null;
        }

        public override string GetDiagnostics()
        {
            return $"Active (Cached Items: {_cachedRewardItems?.Count ?? 0})";
        }

        public float CalculatePowerRatio(PartyBase enemyParty, PartyBase playerParty)
        {
            if (enemyParty == null || playerParty == null) return 1.0f;
            float enemyPower = enemyParty.MemberRoster?.TotalManCount ?? 0;
            float playerPower = playerParty.MemberRoster?.TotalManCount ?? 0;
            if (playerPower <= 0) return 10.0f;
            return enemyPower / playerPower;
        }

        public float GetUnderdogMultiplier(float powerRatio)
        {
            if (Settings.Instance == null || !Settings.Instance.EnableHeroicFeats) return 1.0f;

            float baseMultiplier = Settings.Instance.HardBattleBonusMultiplier;

            if (powerRatio >= 3.0f)
                return baseMultiplier * 3.0f;
            else if (powerRatio >= 2.0f)
                return baseMultiplier * 2.0f;
            else if (powerRatio >= 1.5f)
                return baseMultiplier * 1.5f;
            else
                return 1.0f;
        }

        public int CalculateGoldWithUnderdogBonus(int baseGold, float powerRatio)
        {
            if (Settings.Instance == null) return baseGold;
            float baseMultiplier = Settings.Instance.EnhancedBandits ? Settings.Instance.GoldRewardMultiplier : 1.0f;
            return (int)(baseGold * baseMultiplier * GetUnderdogMultiplier(powerRatio));
        }

        public float CalculateRenownWithUnderdogBonus(float baseRenown, float powerRatio)
        {
            if (Settings.Instance == null) return baseRenown;
            float baseMultiplier = Settings.Instance.EnhancedBandits ? Settings.Instance.RenownRewardMultiplier : 1.0f;
            return baseRenown * baseMultiplier * GetUnderdogMultiplier(powerRatio);
        }

        public void TryAwardAttributePoint(Hero hero, float powerRatio)
        {
            if (Settings.Instance?.EnableHeroicAttributes != true) return;
            if (powerRatio < 5.0f) return;
            if (hero?.HeroDeveloper == null) return;

            // FIX S3: Sınırsız attribute birikimini önlemek için yumuşak tavan.
            const int MAX_EXTRA_ATTRS = 5;
            if (hero.HeroDeveloper.UnspentAttributePoints >= MAX_EXTRA_ATTRS) return;

            float chance = (powerRatio - 4f) * 0.02f;
            if (chance > 0.2f) chance = 0.2f;

            if (MBRandom.RandomFloat < chance)
            {
                hero.HeroDeveloper.UnspentAttributePoints++;
                InformationManager.DisplayMessage(new InformationMessage(
                    $"⭐ GODLIKE VICTORY! {hero.Name} gained an Attribute Point! ⭐", Colors.Magenta));

                if (Mission.Current != null)
                    _ = SoundEvent.PlaySound2D("event:/ui/mission/horns/attack");
            }
        }

        public void TryAwardFocusPoint(Hero hero, float powerRatio)
        {
            if (Settings.Instance?.EnableHeroicAttributes != true) return;
            if (powerRatio < 3.0f) return;
            if (hero?.HeroDeveloper == null) return;

            const int MAX_EXTRA_FOCUS = 10;
            if (hero.HeroDeveloper.UnspentFocusPoints >= MAX_EXTRA_FOCUS) return;

            float chance = (powerRatio - 2f) * 0.1f;
            if (chance > 0.5f) chance = 0.5f;

            if (MBRandom.RandomFloat < chance)
            {
                hero.HeroDeveloper.UnspentFocusPoints++;
                InformationManager.DisplayMessage(new InformationMessage(
                    $"🌟 KAHRAMANLIK! {hero.Name} fazladan bir Odak Puanı kazandı!", Colors.Yellow));

                if (Mission.Current != null)
                    _ = SoundEvent.PlaySound2D("event:/ui/mission/horns/retreat");
            }
        }

        public void TryAwardHeroItem(Hero hero, float powerRatio)
        {
            if (Settings.Instance == null || !Settings.Instance.HeroItemRewards) return;
            if (powerRatio < 2.0f) return;

            float chance = powerRatio >= 5.0f ? 1.0f : (powerRatio >= 3.0f ? 0.5f : 0.25f);

            if (MBRandom.RandomFloat < chance)
            {
                ItemObject? rewardItem = GetRandomRewardItem(powerRatio);
                if (rewardItem != null)
                {
                    _ = MobileParty.MainParty.ItemRoster.AddToCounts(rewardItem, 1);

                    var color = powerRatio >= 5.0f ? Colors.Magenta : Colors.Green;
                    var title = powerRatio >= 5.0f ? "EFSANEVİ EŞYA" : "Destansı Ganimet";

                    InformationManager.DisplayMessage(new InformationMessage($"🎁 {title}: {rewardItem.Name} bulundu!", color));
                }
            }
        }

        public void ShowUnderdogMessage(float powerRatio, int gold, float renown)
        {
            if (powerRatio < 1.5f || Settings.Instance?.TestingMode != true) return;
            InformationManager.DisplayMessage(new InformationMessage(
                $"🏆 Underdog Bonus Applied! Power Ratio: {powerRatio:F1}x | Gold: +{gold} | Renown: +{renown:F1}", Colors.Cyan));
        }

        public void AwardWarlordVictoryRewards(Hero playerHero, BanditMilitias.Systems.Progression.LegitimacyLevel defeatedLevel)
        {
            if (playerHero == null) return;
            if (!IsEnabled) return;
            if (defeatedLevel < BanditMilitias.Systems.Progression.LegitimacyLevel.Rebel) return;

            float rewardMultiplier = Settings.Instance?.WarlordRewardMultiplier ?? 1.0f;

            switch (defeatedLevel)
            {
                case BanditMilitias.Systems.Progression.LegitimacyLevel.Rebel:
                    {
                        int gold = (int)(2000 * rewardMultiplier);
                        GiveGoldReward(gold);
                        InformationManager.DisplayMessage(new InformationMessage(
                            $"🏴‍☠️ İSYANCI LİDERİ YOK EDİLDİ! +{gold} dinar ödül!", Colors.Green));
                    }
                    break;

                case BanditMilitias.Systems.Progression.LegitimacyLevel.Warlord:
                case BanditMilitias.Systems.Progression.LegitimacyLevel.Recognized:
                    {
                        int gold = (int)(defeatedLevel == BanditMilitias.Systems.Progression.LegitimacyLevel.Warlord ? 5000 * rewardMultiplier : 15000 * rewardMultiplier);
                        float renown = (defeatedLevel == BanditMilitias.Systems.Progression.LegitimacyLevel.Warlord ? 50f : 150f) * rewardMultiplier;
                        
                        GiveGoldReward(gold);
                        GiveRenownReward(renown);
                        TryAwardHeroItem(playerHero, defeatedLevel == BanditMilitias.Systems.Progression.LegitimacyLevel.Warlord ? 3.0f : 8.0f);

                        if (defeatedLevel == BanditMilitias.Systems.Progression.LegitimacyLevel.Recognized)
                        {
                            TryAwardAttributePoint(playerHero, 12.0f);
                            TryAwardFocusPoint(playerHero, 8.0f);
                        }

                        string titleTier = defeatedLevel == BanditMilitias.Systems.Progression.LegitimacyLevel.Warlord ? "SAVAŞ LORDU YENİLDİ" : "HÜKÜMDAR DÜŞTÜ";
                        InformationManager.DisplayMessage(new InformationMessage(
                            $"[{titleTier}] +{gold} dinar, +{renown:F0} ün ve ganimet!", 
                            defeatedLevel == BanditMilitias.Systems.Progression.LegitimacyLevel.Warlord ? Colors.Yellow : Colors.Magenta));

                        if (Mission.Current != null)
                            _ = SoundEvent.PlaySound2D("event:/ui/mission/horns/attack");
                    }
                    break;
            }
        }

        private void GiveGoldReward(int gold)
        {
            try { Hero.MainHero?.ChangeHeroGold(gold); }
            catch { }
        }

        private void GiveRenownReward(float renown)
        {
            try { Hero.MainHero?.Clan?.AddRenown(renown); }
            catch { }
        }

        private ItemObject? GetRandomRewardItem(float powerRatio)
        {
            if (_cachedRewardItems == null || _cachedRewardItems.Count == 0)
            {
                InitializeRewardCache();
            }

            if (_cachedRewardItems == null || _cachedRewardItems.Count == 0) return null;

            // Simplified: higher power ratio gives better items (not fully implemented here but stubbed)
            return _cachedRewardItems[MBRandom.RandomInt(_cachedRewardItems.Count)];
        }

        private void InitializeRewardCache()
        {
            _cachedRewardItems = new List<ItemObject>();
            foreach (var item in MBObjectManager.Instance.GetObjectTypeList<ItemObject>())
            {
                if (item != null && item.Tier >= ItemObject.ItemTiers.Tier5 && (item.WeaponComponent != null || item.ArmorComponent != null))
                {
                    _cachedRewardItems.Add(item);
                }
            }
        }
    }
}
