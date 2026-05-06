using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Systems.Enhancement;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Economy
{
    public enum EconomicStage
    {
        Survival = 0,    // Tier 0-1: Basic survival, merging
        Predatory = 1,   // Tier 2: Hunting weaker targets, taxing
        Feudal = 2,      // Tier 3: Lordship, propaganda, investments
        Sovereign = 3,   // Tier 4-5: Kingdom level, luxe spending
        Royal = 4        // Tier 6: King economy, vassal taxes
    }

    [BanditMilitias.Core.Components.AutoRegister]
    public class WarlordEconomySystem : MilitiaModuleBase
    {
        public override string ModuleName => "WarlordEconomy";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 95;

        private static readonly Lazy<WarlordEconomySystem> _instance = new(() => new WarlordEconomySystem());
        public static WarlordEconomySystem Instance => _instance.Value;

        private WarlordEconomySystem() { }

        public override void Initialize()
        {
            DebugLogger.Info("Economy", "WarlordEconomySystem initialized.");
        }

        private const float baseWage = 15f;
        private const float tierMultiplier = 1.15f;

        // —— Global Metrics ——————————————————————————————
        public float CalculateGlobalProsperityAvg()
        {
            if (Campaign.Current == null) return 0f;
            float total = 0f;
            int count = 0;
            foreach (var s in Settlement.All)
            {
                if (s.IsTown)
                {
                    total += s.Town.Prosperity;
                    count++;
                }
                else if (s.IsVillage)
                {
                    total += s.Village.Hearth;
                    count++;
                }
            }
            return count > 0 ? total / count : 0f;
        }

        public float CalculateRegionalProsperity(Settlement? center, float radius = 75f)
        {
            if (center == null) return CalculateGlobalProsperityAvg();
            float total = 0f;
            int count = 0;
            foreach (var s in Settlement.All)
            {
                if (Infrastructure.CompatibilityLayer.GetSettlementPosition(s).Distance(Infrastructure.CompatibilityLayer.GetSettlementPosition(center)) < radius)
                {
                    if (s.IsTown) { total += s.Town.Prosperity; count++; }
                    else if (s.IsVillage) { total += s.Village.Hearth; count++; }
                }
            }
            return count > 0 ? total / count : CalculateGlobalProsperityAvg();
        }

        // —— Financial Capability ————————————————————————————
        public bool HasSufficientFundsForMounts(Warlord w)
        {
            if (w == null) return false;
            // Lord'un 5000 altından az parası varsa yeni at alımı durur (User Request)
            return w.Gold >= 5000;
        }

        // —— Stage Determination ——————————————————————————————
        public EconomicStage GetStage(CareerTier tier) => tier switch
        {
            CareerTier.Eskiya or CareerTier.Rebel => EconomicStage.Survival,
            CareerTier.FamousBandit => EconomicStage.Predatory,
            CareerTier.Warlord => EconomicStage.Feudal,
            CareerTier.Taninmis => EconomicStage.Sovereign,
            CareerTier.Fatih => EconomicStage.Royal,
            _ => EconomicStage.Survival
        };

        // —— Daily Outcome Calculus ———————————————————————————
        public float CalculateDailyOutcome(Warlord w, CareerTier tier)
        {
            // FIX #11: Aktivasyon gecikmesi süresince ekonomi hesaplamalarını durdur
            if (CompatibilityLayer.IsGameplayActivationDelayed())
                return 0f;

            var stage = GetStage(tier);
            float income = CalculateIncome(w, stage);
            float expenses = CalculateExpenses(w, stage);

            // Economic Brain Decision: If gold is too high/low, adjust
            ApplyEconomicBrain(w, stage, income - expenses);

            return income - expenses;
        }

        private float CalculateIncome(Warlord w, EconomicStage stage)
        {
            float baseIncome = 150f;
            float militiaContribution = w.CommandedMilitias.Count * 170f;

            // Stage Multipliers
            float stageMulti = stage switch
            {
                EconomicStage.Survival => 0.8f,  // Harder to get passive income
                EconomicStage.Predatory => 1.2f, // Taxing starts
                EconomicStage.Feudal => 1.5f,    // Black Market synergy
                EconomicStage.Sovereign => 3.5f, // Sovereign wealth
                EconomicStage.Royal => 5.0f,     // King's Treasury
                _ => 1f
            };

            // Legitimacy Bonus
            float legitimacy = Progression.WarlordLegitimacySystem.Instance.GetPoints(w.StringId);
            float legitBonus = 1f + (legitimacy / 5000f);

            // Regional Prosperity Multiplier (User Request - REAL DATA)
            float regionalProsperity = CalculateRegionalProsperity(w.AssignedHideout);
            float globalAvg = CalculateGlobalProsperityAvg();
            float prosperityMulti = 1f;
            if (globalAvg > 0)
                prosperityMulti = 0.8f + (regionalProsperity / globalAvg * 0.4f); // 0.8x to 1.2x range

            // NEW: True Taxation System (Conquest Economy)
            float settlementTaxIncome = 0f;
            if (w.OwnedSettlement != null && w.OwnedSettlement.IsTown)
            {
                // %30 of City/Town Taxes goes to Warlord Treasury
                settlementTaxIncome += w.OwnedSettlement.Town.Prosperity * 0.12f * 0.30f;
            }

            float villageTaxIncome = 0f;
            if (w.InfluencedVillages != null)
            {
                foreach (var village in w.InfluencedVillages)
                {
                    if (village?.Village != null)
                    {
                        // Hearth * 0.15 as Tax Income
                        villageTaxIncome += village.Village.Hearth * 0.15f;
                    }
                }
            }

            // Workshop/Vassal Income Integration
            float extraIncome = 0f;
            try
            {
                extraIncome = Systems.Workshop.WarlordWorkshopSystem.Instance.GetTotalDailyProduction(w.StringId);
                // Vassal Tax: %15 of vassal income
                var vassals = WarlordSystem.Instance.GetAllWarlords().Where(v => v.VassalOf == w.StringId);
                foreach (var v in vassals) 
                {
                    extraIncome += CalculateIncome(v, GetStage(WarlordCareerSystem.Instance.GetTier(v.StringId))) * 0.15f;
                }
            }
            catch { }

            return (((baseIncome + militiaContribution) * stageMulti * legitBonus) * prosperityMulti) + settlementTaxIncome + villageTaxIncome + extraIncome;
        }

        private float CalculateExpenses(Warlord w, EconomicStage stage)
        {
            float totalWage = 0f;
            int totalTroops = 0;

            foreach (var m in w.CommandedMilitias)
            {
                if (m?.Party == null) continue;
                totalWage += GetMilitiaWage(m, stage);
                totalTroops += m.MemberRoster?.TotalManCount ?? 0;
            }

            // NEW: Food & Logistics Cost (0.4 gold per man)
            float foodCost = totalTroops * 0.4f;

            // Wealth Tax (Corruption) - FIX: Excess-based instead of Total-based
            float corruption = 0f;
            if (w.Gold > 50000) corruption = (w.Gold - 50000) * 0.05f; // 5% daily on EXCESS gold only

            // NEW: Scalable Administrative Cost (Bureaucracy)
            // Scales with controlled villages and owned settlements
            int territoryCount = (w.InfluencedVillages?.Count ?? 0) + (w.OwnedSettlement != null ? 1 : 0);
            
            float baseAdmin = stage switch
            {
                EconomicStage.Feudal => 1000f,
                EconomicStage.Sovereign => 3000f,
                _ => 200f
            };

            float bureaucracyCost = territoryCount * 150f; // 150 gold per controlled settlement

            return totalWage + foodCost + corruption + baseAdmin + bureaucracyCost;
        }

        public float GetMilitiaWage(MobileParty party, EconomicStage stage)
        {
            if (party == null) return 100f;

            // Tier based wage
            float avgTier = 1f;
            if (party.MemberRoster?.TotalHealthyCount > 0)
            {
                avgTier = GetAverageTier(party.MemberRoster);
            }

            float total = baseWage * tierMultiplier * (party.MemberRoster?.TotalHealthyCount ?? 0);

            // NEW: Apply Warlord Wage Discount (User Request)
            var warlord = WarlordSystem.Instance.GetWarlordForParty(party);
            if (warlord != null && warlord.WageDiscount > 0)
            {
                total *= (1f - warlord.WageDiscount);
            }

            return total;
        }

        // —— Economic Brain (AI) ——————————————————————————————
        private void ApplyEconomicBrain(Warlord w, EconomicStage stage, float netProfit)
        {
            // Case 1: Gold Critical (Bankruptcy Risk)
            if (w.Gold < 5000 && netProfit < 0)
            {
                SetAggressiveHunting(w, true);
                DebugLogger.Info("EconomyAI", $"[CRISIS] {w.Name} is going Predatory to fix budget.");
            }
            // Case 2: Wealthy (Investment)
            else if (w.Gold > 30000 && stage >= EconomicStage.Predatory)
            {
                InvestInMilitary(w, stage);
            }
        }

        private void SetAggressiveHunting(Warlord w, bool active)
        {
            foreach (var m in w.CommandedMilitias)
            {
                if (m == null) continue;
                // AI hunting logic trigger
                m.Aggressiveness = active ? 1.0f : 0.4f;
            }
        }

        private void InvestInMilitary(Warlord w, EconomicStage stage)
        {
            if (w.Gold < 10000) return;

            // Spend ~10% of EXCESS wealth on upgrades daily (Safety threshold of 30k protected)
            float budget = Math.Max(0, (w.Gold - 30000) * 0.10f);
            w.Gold -= budget;

            // Map EconomicStage to LegitimacyLevel
            LegitimacyLevel level = stage switch
            {
                EconomicStage.Survival => LegitimacyLevel.Rebel,
                EconomicStage.Predatory => LegitimacyLevel.FamousBandit,
                EconomicStage.Feudal => LegitimacyLevel.Warlord,
                EconomicStage.Sovereign => LegitimacyLevel.Recognized,
                _ => LegitimacyLevel.Outlaw
            };

            foreach (var m in w.CommandedMilitias)
            {
                if (m == null || !m.IsActive) continue;
                if (m.PartyComponent is MilitiaPartyComponent comp)
                {
                    // Trigger Enhancement with budget
                    BanditEnhancementSystem.Instance.EnhanceWarlordParty(m, level, budget);
                }
            }
        }

        // —— Merging Logic (Tier 0-1) ——————————————————————————
        public void ProcessMerging(Warlord w)
        {
            if (GetStage(WarlordCareerSystem.Instance.GetTier(w.StringId)) != EconomicStage.Survival) return;

            var smallParties = w.CommandedMilitias
                .Where(m => m.IsActive && m.Party.MemberRoster.TotalHealthyCount < 15)
                .ToList();

            if (smallParties.Count < 2) return;

            for (int i = 0; i < smallParties.Count - 1; i += 2)
            {
                var p1 = smallParties[i];
                var p2 = smallParties[i+1];

                var pos1 = BanditMilitias.Infrastructure.CompatibilityLayer.GetPartyPosition(p1);
                var pos2 = BanditMilitias.Infrastructure.CompatibilityLayer.GetPartyPosition(p2);
                if (pos1.DistanceSquared(pos2) < 100f) // Close enough (10 units)
                {
                    var comp1 = p1.PartyComponent as MilitiaPartyComponent;
                    var comp2 = p2.PartyComponent as MilitiaPartyComponent;
                    if (comp1 != null && comp2 != null)
                        MergeParties(comp1, comp2);
                }
            }
        }

        private void MergeParties(MilitiaPartyComponent p1, MilitiaPartyComponent p2)
        {
            DebugLogger.Info("Economy", $"[MERGE] Merging {p2.Party.Name} into {p1.Party.Name}");
            
            p1.MobileParty.MemberRoster.Add(p2.MobileParty.MemberRoster);
            p1.MobileParty.PrisonRoster.Add(p2.MobileParty.PrisonRoster);
            
            // Sync gold
            p1.Gold += p2.Gold;
            
            // Remove empty party
            BanditMilitias.Infrastructure.CompatibilityLayer.DestroyParty(p2.MobileParty);
            EventBus.Instance.Publish(new WarlordFallenEvent { Warlord = null }); // Trigger cleanup
        }

        // —— Starting Capital (Walkthrough Fix) ————————————————
        public float GetStartingGold(CareerTier tier, bool isCaptain)
        {
            if (isCaptain) return 1000f;
            if (tier <= CareerTier.Rebel) return 300f;
            
            return 500f + ((int)tier * 500f); // Higher tiers start with more
        }

        private float GetAverageTier(TaleWorlds.CampaignSystem.Roster.TroopRoster roster)
        {
            if (roster == null || roster.TotalManCount == 0) return 1f;
            float total = 0f;
            int count = 0;
            foreach (var element in roster.GetTroopRoster())
            {
                if (element.Character != null)
                {
                    total += element.Character.Tier * element.Number;
                    count += element.Number;
                }
            }
            return count > 0 ? total / count : 1f;
        }
    }
}
