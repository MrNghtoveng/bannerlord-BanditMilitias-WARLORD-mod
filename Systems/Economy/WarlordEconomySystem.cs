using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Systems.Enhancement;
using BanditMilitias.Core.Neural;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;

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

        private System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Intelligence.Strategic.Warlord>>? _vassalsByLord;
        
        /// <summary>
        /// Gold Bridge Heartbeat: Son başarılı altın senkronizasyon zamanı.
        /// </summary>
        public CampaignTime LastGoldSyncTime { get; private set; } = CampaignTime.Zero;

        private WarlordEconomySystem() { }

        public override void Initialize()
        {
            DebugLogger.Info("Economy", "WarlordEconomySystem initialized.");
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled) return;
            if (Campaign.Current == null) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;

            // Vassal dict'i O(N) tek geçişte oluştur — CalculateIncome içindeki O(N²) GetAllWarlords().Where(vassal)'ı önler
            var allWarlords = WarlordSystem.Instance.GetAllWarlords();
            _vassalsByLord = new System.Collections.Generic.Dictionary<string,
                System.Collections.Generic.List<Intelligence.Strategic.Warlord>>(allWarlords.Count);
            foreach (var w in allWarlords)
            {
                if (!string.IsNullOrEmpty(w.VassalOf))
                {
                    if (!_vassalsByLord!.TryGetValue(w.VassalOf!, out var vl))
                    { vl = new System.Collections.Generic.List<Intelligence.Strategic.Warlord>(); _vassalsByLord![w.VassalOf!] = vl; }
                    vl.Add(w);
                }
            }

            foreach (var warlord in allWarlords)
            {
                UpdateInfluencedVillages(warlord);
                SellPrisonersAtHideout(warlord);
                SellLootAtVillage(warlord); // 3. Rapor Revize: Shadow Trade
                TryPurchaseEquipmentUpgrades(warlord);
                UpdateAnimalProgression(warlord); // 6. Rapor Revize: Hayvan Gelişimi
                UpdateInventoryProgression(warlord); // 7. Rapor Revize: Envanter Evrimi
                SyncPartyTradeGold(warlord); // NEW: Gold Bridge to bypass upgrade bottleneck
            }
        }

        private void UpdateAnimalProgression(Warlord w)
        {
            if (w.Gold < 5000) return;

            foreach (var party in w.CommandedMilitias)
            {
                if (party == null || !party.IsActive) continue;

                int totalMen = party.MemberRoster.TotalManCount;
                int currentHorses = party.ItemRoster.Where(i => i.EquipmentElement.Item?.IsMountable == true).Sum(i => i.Amount);

                // Süvari Oranı Hedefi: Tier yükseldikçe artar
                var tier = WarlordCareerSystem.Instance.GetTier(w.StringId);
                float targetRatio = tier switch
                {
                    CareerTier.Eskiya => 0.15f,
                    CareerTier.Rebel => 0.25f,
                    CareerTier.FamousBandit => 0.40f,
                    CareerTier.Warlord => 0.60f,
                    _ => 0.80f
                };

                int neededHorses = (int)(totalMen * targetRatio) - currentHorses;
                if (neededHorses > 5)
                {
                    // At satın al
                    ItemObject horse = (tier >= CareerTier.Warlord) 
                        ? (MBObjectManager.Instance.GetObject<ItemObject>("war_horse") ?? MBObjectManager.Instance.GetObject<ItemObject>("midlands_palfrey"))
                        : MBObjectManager.Instance.GetObject<ItemObject>("sumpter_horse");

                    int cost = (int)(neededHorses * (horse.Value * 0.8f));
                    if (w.Gold >= cost)
                    {
                        w.Gold -= cost;
                        _ = party.ItemRoster.AddToCounts(horse, neededHorses);
                        
                        if (Settings.Instance?.TestingMode == true)
                            DebugLogger.TestLog($"[ECONOMY] {w.Name} birliğine {neededHorses} at satın aldı: -{cost} Altın.");
                    }
                }
                
                // Lojistik Sürüler (Gıda kaynağı)
                if (party.ItemRoster.TotalFood < totalMen * 2)
                {
                    var sheep = MBObjectManager.Instance.GetObject<ItemObject>("sheep");
                    if (sheep != null && w.Gold > 500)
                    {
                        w.Gold -= 200;
                        _ = party.ItemRoster.AddToCounts(sheep, 5);
                    }
                }
            }
        }

        private void UpdateInventoryProgression(Warlord w)
        {
            foreach (var party in w.CommandedMilitias)
            {
                if (party == null || !party.IsActive) continue;
                if (party.PartyComponent is not MilitiaPartyComponent comp) continue;

                // 7. Rapor Revize: Loot-to-Equip (Ganimetten Donanıma)
                float totalLootValue = 0;
                int scrapCount = 0;

                for (int i = 0; i < party.ItemRoster.Count; i++)
                {
                    var item = party.ItemRoster.GetElementCopyAtIndex(i);
                    if (item.EquipmentElement.Item == null) continue;

                    if (item.EquipmentElement.Item.Value > 500)
                    {
                        totalLootValue += item.Amount * item.EquipmentElement.Item.Value;
                    }
                    else if (!item.EquipmentElement.Item.IsFood && !item.EquipmentElement.Item.IsMountable)
                    {
                        scrapCount += item.Amount;
                    }
                }

                if (totalLootValue > 2000)
                {
                    // Ekipman kalitesini artır (Max 2.5)
                    float boost = totalLootValue / 100000f; // 100k'lık ganimet +1.0 kalite verir
                    comp.EquipmentQuality = Math.Min(2.5f, comp.EquipmentQuality + boost);
                    
                    // Ganimetin bir kısmını "kuşanmış" sayarak envanterden çıkar
                    // (Gerçekte silmiyoruz, sadece simülasyon)
                }

                // Scrap Melting (Hurda Eritme)
                if (scrapCount > 20)
                {
                    int goldGain = scrapCount * 2;
                    w.Gold += goldGain;
                    // Envanter temizliği
                    // party.ItemRoster.Remove(lowValueItems...); // Bu kısım karmaşık olabilir, şimdilik basitleştirelim
                }
            }
        }

        private void SellLootAtVillage(Warlord w)
        {
            if (w.InfluencedVillages == null || w.InfluencedVillages.Count == 0) return;

            foreach (var party in w.CommandedMilitias)
            {
                if (party == null || !party.IsActive || party.CurrentSettlement == null || !party.CurrentSettlement.IsVillage) continue;

                // İlişkili bir köydeyse ganimetleri nakite çevir (Shadow Trade)
                if (w.InfluencedVillages.Contains(party.CurrentSettlement))
                {
                    int lootValue = 0;
                    for (int i = 0; i < party.ItemRoster.Count; i++)
                    {
                        var item = party.ItemRoster.GetElementCopyAtIndex(i);
                        if (item.EquipmentElement.Item != null && !item.EquipmentElement.Item.IsFood)
                        {
                            lootValue += (int)(item.Amount * item.EquipmentElement.Item.Value * 0.4f); // %40 fiyatla sat
                        }
                    }

                    if (lootValue > 0)
                    {
                        w.Gold += lootValue;
                        party.ItemRoster.Clear(); // Ganimetleri temizle
                        
                        if (Settings.Instance?.TestingMode == true)
                            DebugLogger.TestLog($"[ECONOMY] {w.Name} köyde ganimetleri sattı (Shadow Trade): +{lootValue} Altın.");
                    }
                }
            }
        }

        private void SellPrisonersAtHideout(Warlord w)
        {
            if (w.AssignedHideout == null) return;
            
            foreach (var party in w.CommandedMilitias)
            {
                if (party == null || !party.IsActive || CompatibilityLayer.IsTroopRosterEmpty(party.PrisonRoster)) continue;
                
                float distSq = CompatibilityLayer.GetPartyPosition(party).DistanceSquared(CompatibilityLayer.GetSettlementPosition(w.AssignedHideout));
                if (distSq < 10f * 10f) // Sığınakta/Yakınındaysa
                {
                    // Esirleri "karaborsada" sat
                    int prisonerValue = 0;
                    for (int i = 0; i < party.PrisonRoster.Count; i++)
                    {
                        var element = party.PrisonRoster.GetElementCopyAtIndex(i);
                        prisonerValue += element.Number * (element.Character.Tier * 15 + 20); // Tier bazlı fiyat
                    }

                    w.Gold += prisonerValue;
                    party.PrisonRoster.Clear();
                    
                    if (Settings.Instance?.TestingMode == true)
                        DebugLogger.TestLog($"[ECONOMY] {w.Name} esirleri sığınakta sattı: +{prisonerValue} Altın.");
                }
            }
        }

        private void SyncPartyTradeGold(Warlord w)
        {
            if (w.Gold < 1000) return; // Warlord is too poor to share

            foreach (var party in w.CommandedMilitias)
            {
                if (party == null || !party.IsActive) continue;

                // Minimum trade gold target based on tier
                var tier = WarlordCareerSystem.Instance.GetTier(w.StringId);
                int targetGold = tier switch
                {
                    CareerTier.Eskiya => 500,
                    CareerTier.Rebel => 1000,
                    CareerTier.FamousBandit => 2500,
                    CareerTier.Warlord => 5000,
                    _ => 10000
                };

                if (party.PartyTradeGold < targetGold)
                {
                    int needed = targetGold - party.PartyTradeGold;
                    int actualTransfer = (int)Math.Min(needed, w.Gold * 0.1f); // Max 10% of treasury per party per tick

                    if (actualTransfer > 0)
                    {
                        w.Gold -= actualTransfer;
                        party.PartyTradeGold += actualTransfer;
                        
                        // Heartbeat Update
                        LastGoldSyncTime = CampaignTime.Now;

                        if (Settings.Instance?.TestingMode == true && actualTransfer > 500)
                            DebugLogger.TestLog($"[BRIDGE] {w.Name} sent {actualTransfer} gold to {party.Name} for upgrades.");
                    }
                }
            }
        }

        private void TryPurchaseEquipmentUpgrades(Warlord w)
        {
            // 3. Rapor Revize: Tier 0-1-2 için Altın-Ekipman dönüşümü
            var tier = WarlordCareerSystem.Instance.GetTier(w.StringId);
            if (tier > CareerTier.FamousBandit && w.Gold < 30000) return; // Yüksek tierler InvestInMilitary'i bekler

            float goal = (tier <= CareerTier.Rebel) ? 1000f : 3000f;
            
            if (w.Gold >= goal)
            {
                float spend = w.Gold * 0.4f; // Paranın %40'ını ekipmana yatır
                w.Gold -= spend;
                
                foreach (var party in w.CommandedMilitias)
                {
                    if (party == null || !party.IsActive) continue;
                    // Doğrudan ekipman geliştirme tetikle
                    BanditEnhancementSystem.Instance.EnhanceParty(party);
                }
                
                if (Settings.Instance?.TestingMode == true)
                    DebugLogger.Info("Economy", $"[UPGRADE] {w.Name} {goal} altın hedefine ulaştı. Ekipmanlar yükseltildi. Harcanan: {spend:F0}");
            }
        }

        private void UpdateInfluencedVillages(Warlord w)
        {
            if (w.AssignedHideout == null) return;

            w.InfluencedVillages ??= new List<Settlement>();
            w.InfluencedVillages.Clear();

            var hideoutPos = Infrastructure.CompatibilityLayer.GetSettlementPosition(w.AssignedHideout);
            // Settlement.All (tüm harita) yerine ModuleManager.VillageCache (sadece köyler)
            foreach (var s in Infrastructure.ModuleManager.Instance.VillageCache)
            {
                if (Infrastructure.CompatibilityLayer.GetSettlementPosition(s).DistanceSquared(hideoutPos) < 625f) // 25²
                    w.InfluencedVillages.Add(s);
            }
        }

        private const float baseWage = 15f;
        private const float tierMultiplier = 1.15f;

        // —— Global Metrics ——————————————————————————————
        public float CalculateGlobalProsperityAvg()
        {
            if (Campaign.Current == null) return 0f;
            float total = 0f;
            int count = 0;
            var mm = Infrastructure.ModuleManager.Instance;
            foreach (var s in mm.TownCache)    { total += s.Town.Prosperity;  count++; }
            foreach (var s in mm.VillageCache) { total += s.Village.Hearth;   count++; }
            return count > 0 ? total / count : 0f;
        }

        public float CalculateRegionalProsperity(Settlement? center, float radius = 75f)
        {
            if (center == null) return CalculateGlobalProsperityAvg();
            float total = 0f;
            int count = 0;
            float radiusSq = radius * radius;
            var centerPos = Infrastructure.CompatibilityLayer.GetSettlementPosition(center);
            var mm = Infrastructure.ModuleManager.Instance;
            foreach (var s in mm.TownCache)
            {
                if (Infrastructure.CompatibilityLayer.GetSettlementPosition(s).DistanceSquared(centerPos) < radiusSq)
                { total += s.Town.Prosperity; count++; }
            }
            foreach (var s in mm.VillageCache)
            {
                if (Infrastructure.CompatibilityLayer.GetSettlementPosition(s).DistanceSquared(centerPos) < radiusSq)
                { total += s.Village.Hearth; count++; }
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
                // FIX: Workshop income is handled by WarlordWorkshopSystem independently.
                // Including it here causes double-counting because WarlordSystem adds this outcome to w.Gold.
                // extraIncome = Systems.Workshop.WarlordWorkshopSystem.Instance.GetTotalDailyProduction(w.StringId);
                
                // Vassal Tax: %15 of vassal income
                var vassals = (_vassalsByLord != null && _vassalsByLord.TryGetValue(w.StringId, out var _vl2))
                    ? (System.Collections.Generic.IEnumerable<Intelligence.Strategic.Warlord>)_vl2
                    : WarlordSystem.Instance.GetAllWarlords().Where(v => v.VassalOf == w.StringId);
                foreach (var v in vassals) 
                {
                    extraIncome += CalculateIncome(v, GetStage(WarlordCareerSystem.Instance.GetTier(v.StringId))) * 0.15f;
                }
            }
            catch (Exception ex)
            {
                BanditMilitias.Debug.DebugLogger.Warning("WarlordEconomy", $"Vassal income calc failed: {ex.Message}");
            }

            return (((baseIncome + militiaContribution) * stageMulti * legitBonus) * prosperityMulti) + settlementTaxIncome + villageTaxIncome + extraIncome;
        }

        private float CalculateExpenses(Warlord w, EconomicStage stage)
        {
            float totalWage = 0f;
            float totalFoodCost = 0f;

            foreach (var m in w.CommandedMilitias)
            {
                if (m == null || !m.IsActive) continue;
                
                totalWage += GetMilitiaWage(m, stage);
                
                // 2. Rapor Revize: Lojistik İkmal (Sığınak yakınında yiyecek tüketimi azalır)
                int troops = m.MemberRoster?.TotalManCount ?? 0;
                float partyFoodCost = troops * 0.4f;
                
                if (w.AssignedHideout != null)
                {
                    float distSq = CompatibilityLayer.GetPartyPosition(m).DistanceSquared(CompatibilityLayer.GetSettlementPosition(w.AssignedHideout));
                    if (distSq < 20f * 20f) // 20 birim menzil
                    {
                        partyFoodCost *= 0.5f; // %50 tasarruf
                    }
                }
                totalFoodCost += partyFoodCost;
            }

            // Wealth Tax (Corruption) - FIX: Excess-based instead of Total-based
            float corruption = 0f;
            if (w.Gold > 50000) corruption = (w.Gold - 50000) * 0.05f; // 5% daily on EXCESS gold only

            // NEW: Scalable Administrative Cost (Bureaucracy)
            int territoryCount = (w.InfluencedVillages?.Count ?? 0) + (w.OwnedSettlement != null ? 1 : 0);
            
            float baseAdmin = stage switch
            {
                EconomicStage.Feudal => 1000f,
                EconomicStage.Sovereign => 3000f,
                _ => 200f
            };

            float bureaucracyCost = territoryCount * 150f; // 150 gold per controlled settlement

            // NEW: Vassal Tax Deduction (Fixing desync)
            float vassalTax = 0f;
            if (!string.IsNullOrEmpty(w.VassalOf))
            {
                vassalTax = CalculateIncome(w, stage) * 0.15f;
            }

            return totalWage + totalFoodCost + corruption + baseAdmin + bureaucracyCost + vassalTax;
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
            NeuralEventRouter.Instance.Publish(new WarlordFallenEvent { Warlord = null }); // Trigger cleanup
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
            for (int i = 0; i < roster.Count; i++)
            {
                var element = roster.GetElementCopyAtIndex(i);
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
