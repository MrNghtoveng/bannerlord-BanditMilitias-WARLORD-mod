using BanditMilitias.Components;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Localization;

namespace BanditMilitias.Models
{
    // ── ModPartySizeLimitModel ─────────────────────────────────────────
    public class ModPartySizeLimitModel : DefaultPartySizeLimitModel
    {
        public override ExplainedNumber GetPartyMemberSizeLimit(PartyBase party, bool includeDlc = false)
        {
            var result = base.GetPartyMemberSizeLimit(party, includeDlc);

            if (party.MobileParty != null && (party.MobileParty.IsBandit || party.MobileParty.PartyComponent is MilitiaPartyComponent))
            {
                var mult = Settings.Instance?.BanditSizeMultiplier ?? 1.0f;
                // [FIX] Avoid +0% spam in tooltip
                if (Math.Abs(mult - 1.0f) > 0.01f)
                {
                    result.AddFactor(mult - 1.0f, new TaleWorlds.Localization.TextObject("{=BanditExpansion}Bandit Horde"));
                }
            }

            return result;
        }
    }

    // ── MilitiaSpeedModel ─────────────────────────────────────────
    public class MilitiaSpeedModel : DefaultPartySpeedCalculatingModel
    {
        public override ExplainedNumber CalculateFinalSpeed(MobileParty mobileParty, ExplainedNumber finalSpeed)
        {
            ExplainedNumber result = base.CalculateFinalSpeed(mobileParty, finalSpeed);

            if (mobileParty?.PartyComponent is MilitiaPartyComponent component)
            {
                // Temel Vur-Kaç Taktiği Bonusu (%10 Raider, %5 Guardian)
                float roleSpeedFactor = component.Role == MilitiaPartyComponent.MilitiaRole.Raider ? 0.10f : 0.05f;
                result.AddFactor(roleSpeedFactor, new TextObject("{=BM_Speed_Tactics}Hit & Run Tactics"));

                // KERVAN TAKİBİ BONUSU: Kervanları yakalayabilmeleri için ekstra hız
                if (mobileParty.TargetParty != null && mobileParty.TargetParty.IsCaravan)
                {
                    result.AddFactor(0.25f, new TextObject("{=BM_Speed_Caravan}Caravan Hunter Focus"));
                }
                
                // PREDATORY AI BONUSU: Zayıf milisleri kovalarken hızlanma
                else if (mobileParty.TargetParty != null && mobileParty.TargetParty.PartyComponent is MilitiaPartyComponent)
                {
                    result.AddFactor(0.15f, new TextObject("{=BM_Speed_Predatory}Predatory Instincts"));
                }
                
                // SCAVENGE BONUSU: Savaş meydanına yetişme hızı
                else if (mobileParty.TargetParty != null && mobileParty.TargetParty.MapEvent != null)
                {
                    result.AddFactor(0.10f, new TextObject("{=BM_Speed_Scavenge}Scavenge Rush"));
                }

                // LORD SPEED BONUS: Rütbe yükseldikçe artan stratejik hız (User Request)
                // OPTIMIZATION: O(N) GetWarlordForParty yerine cached O(1) AssignedWarlord kullanılıyor
                var warlord = component?.AssignedWarlord;
                if (warlord != null && warlord.SpeedBonus > 0)
                {
                    result.AddFactor(warlord.SpeedBonus, new TextObject("{=BM_Speed_Warlord}Warlord Leadership"));
                }

                // MEVSİMSEL HIZ ÇARPANI: Yaz +%15, Kış -%12
                try
                {
                    float seasonalMult = BanditMilitias.Systems.Seasonal.SeasonalEffectsSystem.Instance.SpeedMultiplier;
                    if (Math.Abs(seasonalMult - 1f) > 0.001f)
                    {
                        result.AddFactor(seasonalMult - 1f, new TextObject("{=BM_Speed_Season}Seasonal Conditions"));
                    }
                }
                catch { /* SeasonalSystem hazır değilse atla */ }
            }

            return result;
        }
    }

    // ── ModBanditDensityModel ─────────────────────────────────────────
    public class ModBanditDensityModel : BanditDensityModel
    {
        private readonly DefaultBanditDensityModel _default = new DefaultBanditDensityModel();

        public override int NumberOfMaximumHideoutsAtEachBanditFaction
        {
            get
            {

                var sub = Settings.Instance?.BanditDensityMultiplier ?? 1.0f;

                int result = (int)(_default.NumberOfMaximumHideoutsAtEachBanditFaction * sub);

                return Math.Max(3, result);
            }
        }

        public override int NumberOfInitialHideoutsAtEachBanditFaction => _default.NumberOfInitialHideoutsAtEachBanditFaction;
        public int NumberOfMaximumLooterParties
        {
            get
            {
                var mult = Settings.Instance?.BanditDensityMultiplier ?? 1.0f;

                return (int)(300 * mult);
            }
        }

        public override int NumberOfMaximumBanditPartiesAroundEachHideout
        {
            get
            {
                var mult = Settings.Instance?.BanditDensityMultiplier ?? 1.0f;

                // PERFORMANCE GUARD: Throttle vanilla bandit spawning if world is over-populated
                if (Campaign.Current != null)
                {
                    // OPTIMIZATION: O(N) MobileParties.Count yerine cached deÄŸeri kullan
                    int totalParties = Infrastructure.ModuleManager.Instance.CachedTotalParties;
                    if (totalParties > 1800)
                    {
                        // Scale multiplier down as we approach the limit
                        float threshold = 1800f;
                        float limit = 2200f;
                        float penaltyFactor = Math.Min(1.0f, (totalParties - threshold) / (limit - threshold));
                        mult *= (1.0f - (0.75f * penaltyFactor)); // Up to 75% reduction
                    }
                }

                return Math.Max(1, (int)(_default.NumberOfMaximumBanditPartiesAroundEachHideout * mult));
            }
        }

        public override int NumberOfMaximumBanditPartiesInEachHideout
        {
            get
            {
                var mult = Settings.Instance?.BanditDensityMultiplier ?? 1.0f;

                if (Campaign.Current != null && Campaign.Current.MobileParties.Count > 2000)
                    mult *= 0.5f;

                return Math.Max(1, (int)(_default.NumberOfMaximumBanditPartiesInEachHideout * mult));
            }
        }
        public override int NumberOfMaximumTroopCountForBossFightInHideout => _default.NumberOfMaximumTroopCountForBossFightInHideout;
        public override int NumberOfMaximumTroopCountForFirstFightInHideout => _default.NumberOfMaximumTroopCountForFirstFightInHideout;
        public override int NumberOfMinimumBanditPartiesInAHideoutToInfestIt => _default.NumberOfMinimumBanditPartiesInAHideoutToInfestIt;
        public override int NumberOfMinimumBanditTroopsInHideoutMission => _default.NumberOfMinimumBanditTroopsInHideoutMission;
        public override float SpawnPercentageForFirstFightInHideoutMission => _default.SpawnPercentageForFirstFightInHideoutMission;

        public override int GetMaxSupportedNumberOfLootersForClan(Clan clan) => _default.GetMaxSupportedNumberOfLootersForClan(clan);
        public override int GetMaximumTroopCountForHideoutMission(MobileParty party, bool isAssault)
            => _default.GetMaximumTroopCountForHideoutMission(party, isAssault);

        public override int GetMinimumTroopCountForHideoutMission(MobileParty party, bool isAssault)
            => _default.GetMinimumTroopCountForHideoutMission(party, isAssault);

#if v120

        public override bool IsPositionInsideNavalSafeZone(CampaignVec2 position) => false;
#else
        public override bool IsPositionInsideNavalSafeZone(CampaignVec2 position) => _default.IsPositionInsideNavalSafeZone(position);
#endif
    }

}