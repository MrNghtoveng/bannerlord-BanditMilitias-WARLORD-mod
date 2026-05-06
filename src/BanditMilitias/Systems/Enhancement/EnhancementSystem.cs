using BanditMilitias.Core.Components;
using BanditMilitias.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Economy;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BanditMilitias.Systems.Enhancement
{
    // ── BanditEnhancementSystem ─────────────────────────────────────────
    public class BanditEnhancementSystem : BanditMilitias.Core.Components.MilitiaModuleBase
    {

        private static readonly Lazy<BanditEnhancementSystem> _instance =
            new Lazy<BanditEnhancementSystem>(() => new BanditEnhancementSystem());

        public static BanditEnhancementSystem Instance => _instance.Value;

        private BanditEnhancementSystem() { }

        public override string ModuleName => "BanditEnhancementSystem";
        public override bool IsEnabled => Settings.Instance?.EnhancedBandits ?? true;
        public override int Priority => 50;

        public override string GetDiagnostics()
        {
            return "Active";
        }

        public void EnhanceParty(MobileParty party)
        {
            if (Settings.Instance == null || !Settings.Instance.EnhancedBandits) return;
            if (party == null || !party.IsBandit) return;
            var roster = party.MemberRoster;
            if (roster == null || roster.TotalManCount == 0) return;

            int qualityLevel = Settings.Instance.EquipmentQuality;
            int totalUpgrades = 1 + qualityLevel;

            // NEW: Nam ve Rol Bazlı Asker Gelişimi
            if (party.PartyComponent is MilitiaPartyComponent mpc)
            {
                int renownBonus = (int)(mpc.Renown / 40f); // Her 40 Nam'da +1 gelişim turu
                totalUpgrades += renownBonus;

                if (mpc.Role == MilitiaPartyComponent.MilitiaRole.VeteranCaptain)
                    totalUpgrades += 2;
            }

            // YENİ: Erken oyunda yüksek tier asker sorununu çözmek için upgrade kısıtlaması
            float elapsedDays = 0f;
            if (Campaign.Current != null)
            {
                var startTime = BanditMilitias.Infrastructure.CompatibilityLayer.GetCampaignStartTime();
                if (startTime.ToHours > 0.0) elapsedDays = (float)(CampaignTime.Now - startTime).ToDays;
                if (elapsedDays < 0f) elapsedDays = 0f;
            }
            if (elapsedDays < 15f) totalUpgrades = 0; // İlk 15 gün: Sadece standart çapulcu/haydut (Tier 1-2)
            else if (elapsedDays < 30f) totalUpgrades = 1; // 15-30 gün: Max 1 upgrade (Tier 2-3)
            else if (elapsedDays < 60f) totalUpgrades = Math.Min(totalUpgrades, 2); // 30-60 gün: Max 2 upgrade

            List<(CharacterObject Source, CharacterObject Target, int Count)> upgradeBuffer = new();

            for (int i = 0; i < totalUpgrades; i++)
            {
                UpgradeTroops(party, upgradeBuffer, LegitimacyLevel.Outlaw);
            }

            int skillBoost = Settings.Instance.BanditSkillBoost;
            if (elapsedDays < 15f) skillBoost = 0; // İlk 15 gün ekstra tecrübe yok
            else if (elapsedDays < 30f) skillBoost = (int)(skillBoost * 0.3f); // %30 bonus
            else if (elapsedDays < 60f) skillBoost = (int)(skillBoost * 0.6f); // %60 bonus
            if (skillBoost > 0)
            {
                int count = party.MemberRoster.Count;
                for (int i = 0; i < count; i++)
                {

                    if (i >= party.MemberRoster.Count) break;

                    var element = party.MemberRoster.GetElementCopyAtIndex(i);
                    if (element.Character != null)
                    {

                        int baseXp = Settings.Instance?.UpgradeXp ?? 500;
                        int extraXp = skillBoost * 20;

                        // NEW: Apply Warlord XP multiplier
                        var warlord = WarlordSystem.Instance.GetWarlordForParty(party);
                        if (warlord != null)
                        {
                            extraXp = (int)(extraXp * warlord.XpMultiplier);
                        }

                        int xpToAdd = baseXp + extraXp;
                        xpToAdd = (int)(xpToAdd * (0.8f + MBRandom.RandomFloat * 0.4f));

                        if (xpToAdd > 0)
                        {
                            party.MemberRoster.AddXpToTroop(element.Character, xpToAdd);

                            TelemetryBridge.LogEvent("TroopXpGain", new
                            {
                                party = party.Name?.ToString(),
                                troop = element.Character.Name?.ToString(),
                                xp = xpToAdd
                            });
                        }
                    }
                }
            }

            GiveHorsesAndFood(party);

            // NEW: Kaptanlar ve Veteranlar için de lider ekipmanını güncelle
            if (party.PartyComponent is MilitiaPartyComponent mpc2 && 
                (mpc2.Role == MilitiaPartyComponent.MilitiaRole.VeteranCaptain || mpc2.Renown > 100))
            {
                // Veteran veya yüksek namlı kaptanlar 'Rebel' seviyesinde ekipman alır
                UpgradeLeaderEquipment(party, mpc2.Role == MilitiaPartyComponent.MilitiaRole.VeteranCaptain ? LegitimacyLevel.Rebel : LegitimacyLevel.Outlaw);
            }
        }

        private void GiveHorsesAndFood(MobileParty party)
        {
            if (party == null || party.ItemRoster == null) return;

            var economy = WarlordEconomySystem.Instance;
            var warlord = WarlordSystem.Instance.GetWarlordForParty(party);
            bool canAfford = warlord == null || economy.HasSufficientFundsForMounts(warlord);

            if (canAfford)
            {
                var comp = party.PartyComponent as MilitiaPartyComponent;
                int tier = comp != null ? (int)WarlordCareerSystem.Instance.GetTier(comp.WarlordId ?? string.Empty) : 0;

                string horseId = tier switch
                {
                    >= 4 => "noble_horse",
                    >= 2 => "war_horse",
                    _ => "sumpter_horse"
                };

                var horse = TaleWorlds.Core.Game.Current.ObjectManager.GetObject<ItemObject>(horseId)
                            ?? TaleWorlds.Core.Game.Current.ObjectManager.GetObject<ItemObject>("sumpter_horse");

                if (horse != null)
                {
                    // User Request: Cavalry focus from Tier 1 (35% start)
                    float cavRatio = 0.35f + (tier * 0.05f);
                    int horsesNeeded = (int)(party.MemberRoster.TotalManCount * cavRatio);

                    int currentHorses = party.ItemRoster.GetItemNumber(horse);
                    if (horsesNeeded > currentHorses)
                    {
                        int diff = horsesNeeded - currentHorses;
                        _ = party.ItemRoster.AddToCounts(horse, diff);
                        
                        if (warlord != null) 
                            warlord.Gold -= diff * 20; // Nominal logistic cost
                    }
                }
            }

            var grain = TaleWorlds.Core.Game.Current.ObjectManager.GetObject<ItemObject>("grain");
            if (grain != null)
            {
                int foodNeeded = (party.MemberRoster.TotalManCount / 10) + 1;
                _ = party.ItemRoster.AddToCounts(grain, foodNeeded);
            }

            party.RecentEventsMorale += 20f;
        }

        private void UpgradeTroops(MobileParty party, List<(CharacterObject Source, CharacterObject Target, int Count)> buffer, LegitimacyLevel level = LegitimacyLevel.Outlaw)
        {
            var roster = party.MemberRoster;
            buffer.Clear();

            for (int i = 0; i < roster.Count; i++)
            {
                var element = roster.GetElementCopyAtIndex(i);
                var character = element.Character;

                if (character == null) continue;

                if (character.UpgradeTargets != null && character.UpgradeTargets.Length > 0)
                {
                    CharacterObject? target = null;

                    if (level >= LegitimacyLevel.Warlord)
                    {
                        foreach (var t in character.UpgradeTargets)
                        {
                            if (t.Occupation != Occupation.Bandit)
                            {
                                target = t;
                                break;
                            }
                        }
                    }

                    target ??= character.UpgradeTargets.GetRandomElement();

                    if (target != null)
                    {
                        buffer.Add((character, target, element.Number));
                    }
                }
            }

            foreach (var upgrade in buffer)
            {
                if (upgrade.Source == null || upgrade.Target == null || upgrade.Count <= 0) continue;

                if (roster.GetTroopCount(upgrade.Source) < upgrade.Count) continue;

                _ = roster.AddToCounts(upgrade.Source, -upgrade.Count);
                _ = roster.AddToCounts(upgrade.Target, upgrade.Count);

                TelemetryBridge.LogEvent("TroopUpgrade", new
                {
                    party = party.Name?.ToString(),
                    source = upgrade.Source.Name?.ToString(),
                    target = upgrade.Target.Name?.ToString(),
                    count = upgrade.Count
                });
            }
        }

        public void EnhanceWarlordParty(MobileParty party, LegitimacyLevel level, float budget = 0f, bool upgradeLeader = false)
        {
            if (party?.MemberRoster == null || party.MemberRoster.TotalManCount == 0) return;
            if (level == LegitimacyLevel.Outlaw) return;

            var upgradeBuffer = new List<(CharacterObject Source, CharacterObject Target, int Count)>();

            int upgradeIterations = level switch
            {
                LegitimacyLevel.Rebel => 3,
                LegitimacyLevel.FamousBandit => 5,
                LegitimacyLevel.Warlord => 8,
                LegitimacyLevel.Recognized => 10,
                _ => 1
            };

            // NEW: Budget-based extra iterations (Luxe Spending)
            if (budget > 5000) upgradeIterations += 5;
            if (budget > 15000) upgradeIterations += 10;

            for (int i = 0; i < upgradeIterations; i++)
            {
                UpgradeTroops(party, upgradeBuffer, level);
            }

            int warlordXpBoost = level switch
            {
                LegitimacyLevel.Rebel => 800,
                LegitimacyLevel.FamousBandit => 800,
                LegitimacyLevel.Warlord => 2000,
                LegitimacyLevel.Recognized => 8000,
                _ => 0
            };

            // NEW: Apply Warlord XP multiplier (already found warlord above if needed)
            var currentWarlord = WarlordSystem.Instance.GetWarlordForParty(party);
            if (currentWarlord != null)
            {
                warlordXpBoost = (int)(warlordXpBoost * currentWarlord.XpMultiplier);
            }

            if (warlordXpBoost > 0)
            {
                for (int i = 0; i < party.MemberRoster.Count; i++)
                {
                    if (i >= party.MemberRoster.Count) break;
                    var element = party.MemberRoster.GetElementCopyAtIndex(i);
                    if (element.Character != null)
                    {
                        int xp = (int)(warlordXpBoost * (0.8f + MBRandom.RandomFloat * 0.4f));
                        party.MemberRoster.AddXpToTroop(element.Character, xp);
                    }
                }
            }

            float moraleBoost = level switch
            {
                LegitimacyLevel.Rebel => 15f,
                LegitimacyLevel.FamousBandit => 30f,
                LegitimacyLevel.Warlord => 50f,
                LegitimacyLevel.Recognized => 70f,
                _ => 0f
            };
            party.RecentEventsMorale += moraleBoost;

            if (level >= LegitimacyLevel.FamousBandit)
            {
                // Logic already handled in GiveHorsesAndFood loop for all militias
                // but we gift a few extra high-tier mounts here for elite units
                string eliteHorseId = level >= LegitimacyLevel.Recognized ? "noble_horse" : "war_horse";
                var eliteMount = Game.Current?.ObjectManager?.GetObject<ItemObject>(eliteHorseId);
                
                if (eliteMount != null && party.ItemRoster != null)
                {
                    _ = party.ItemRoster.AddToCounts(eliteMount, 5); // Elite core mounts
                }
            }

            // NEW: Leader Gear Evolution (User Request)
            if (upgradeLeader)
            {
                UpgradeLeaderEquipment(party, level);
            }

            // NEW: Special Ammo / Luxe Items (Luxe Spending)
            if (budget > 2500 && party.ItemRoster != null)
            {
                var specialArrow = Game.Current?.ObjectManager?.GetObject<ItemObject>("bodkin_arrows_b") 
                                ?? Game.Current?.ObjectManager?.GetObject<ItemObject>("pierced_arrows");
                if (specialArrow != null)
                {
                    _ = party.ItemRoster.AddToCounts(specialArrow, party.MemberRoster.TotalManCount / 10);
                }
            }

            WarlordTacticsSystem.Instance.ApplyCounterTactics(party, level);

            if (Settings.Instance?.TestingMode == true)
            {
                int totalTroops = party.MemberRoster.TotalManCount;
                float avgTier = 0f;
                for (int i = 0; i < party.MemberRoster.Count; i++)
                {
                    var el = party.MemberRoster.GetElementCopyAtIndex(i);
                    if (el.Character != null)
                        avgTier += el.Character.Tier * el.Number;
                }
                if (totalTroops > 0) avgTier /= totalTroops;

                DebugLogger.Info("Enhancement",
                    $"?? Warlord Army [{level}]: {totalTroops} troops, avg tier {avgTier:F1}, " +
                    $"morale +{moraleBoost}, XP boost +{warlordXpBoost}");
            }
        }

        private void UpgradeLeaderEquipment(MobileParty party, LegitimacyLevel level)
        {
            if (party?.ItemRoster == null) return;

            string horseId = "sumpter_horse";
            string armorId = "leather_armor";
            string weaponId = "iron_spatha";

            switch (level)
            {
                case LegitimacyLevel.Rebel:
                    horseId = "midlands_palfrey";
                    armorId = "padded_leather_armor";
                    weaponId = "heavy_iron_spatha";
                    break;
                case LegitimacyLevel.FamousBandit:
                    horseId = "war_horse";
                    armorId = "mail_armor";
                    weaponId = "scimitar";
                    break;
                case LegitimacyLevel.Warlord:
                    horseId = "noble_horse";
                    armorId = "plate_armor";
                    weaponId = "noble_sword";
                    break;
                case LegitimacyLevel.Recognized:
                    horseId = "noble_horse";
                    armorId = "heavy_plate_armor";
                    weaponId = "executioners_axe";
                    break;
            }

            var horse = Game.Current?.ObjectManager?.GetObject<ItemObject>(horseId);
            var armor = Game.Current?.ObjectManager?.GetObject<ItemObject>(armorId);
            var weapon = Game.Current?.ObjectManager?.GetObject<ItemObject>(weaponId);

            if (horse != null) _ = party.ItemRoster.AddToCounts(horse, 1);
            if (armor != null) _ = party.ItemRoster.AddToCounts(armor, 1);
            if (weapon != null) _ = party.ItemRoster.AddToCounts(weapon, 1);

            if (Settings.Instance?.TestingMode == true)
                DebugLogger.Info("Enhancement", $"[LEADER-UPGRADE] {party.Name} leader gear upgraded to {level} tier.");
        }
    }




    // ── WarlordTacticsSystem ─────────────────────────────────────────

    [BanditMilitias.Core.Components.AutoRegister]
    public class WarlordTacticsSystem : MilitiaModuleBase
    {

        private static readonly Lazy<WarlordTacticsSystem> _instance =
            new Lazy<WarlordTacticsSystem>(() => new WarlordTacticsSystem());
        public static WarlordTacticsSystem Instance => _instance.Value;

        private WarlordTacticsSystem() { }

        public override string ModuleName => "WarlordTacticsSystem";
        public override bool IsEnabled => Settings.Instance?.EnableWarlordTactics ?? true;
        public override int Priority => 55;

        public override string GetDiagnostics() => $"Tactics Active: {IsEnabled}";
        public override void Initialize() { _troopCache.Clear(); }
        public override void Cleanup() { _troopCache.Clear(); }

        private static readonly Dictionary<(int tier, bool mounted, bool ranged), CharacterObject?>
            _troopCache = new();

        private ArmyComposition _cachedPlayerArmy = ArmyComposition.Default;
        private CampaignTime _lastAnalysisTime = CampaignTime.Zero;

        public override void OnDailyTick()
        {
            if (!IsEnabled) return;
            _cachedPlayerArmy = AnalyzePlayerArmyInternal();
            _lastAnalysisTime = CampaignTime.Now;

            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("WarlordTacticsSystem", "[Tactics] Daily player army analysis updated.");
            }
        }

        public ArmyComposition AnalyzePlayerArmy()
        {

            if (_lastAnalysisTime != CampaignTime.Zero && (CampaignTime.Now - _lastAnalysisTime).ToDays < 1.0f)
                return _cachedPlayerArmy;

            return AnalyzePlayerArmyInternal();
        }

        private ArmyComposition AnalyzePlayerArmyInternal()
        {
            var player = MobileParty.MainParty;
            if (player?.MemberRoster == null)
                return ArmyComposition.Default;

            var result = new ArmyComposition();
            var roster = player.MemberRoster;

            for (int i = 0; i < roster.Count; i++)
            {
                var element = roster.GetElementCopyAtIndex(i);
                if (element.Character == null || element.Number <= 0) continue;

                int count = element.Number;
                result.TotalCount += count;
                result.AverageTier += element.Character.Tier * count;

                if (element.Character.IsMounted && element.Character.IsRanged)
                {
                    result.HorseArcherCount += count;
                }
                else if (element.Character.IsMounted)
                {
                    result.CavalryCount += count;
                }
                else if (element.Character.IsRanged)
                {
                    result.RangedCount += count;
                }
                else
                {
                    result.InfantryCount += count;
                }
            }

            if (result.TotalCount > 0)
                result.AverageTier /= result.TotalCount;

            return result;
        }

        public CounterTactic DetermineCounterTactic(ArmyComposition playerArmy)
        {
            if (playerArmy.TotalCount == 0) return CounterTactic.Balanced;

            float cavalryRatio = (float)(playerArmy.CavalryCount + playerArmy.HorseArcherCount) / playerArmy.TotalCount;
            float rangedRatio = (float)(playerArmy.RangedCount + playerArmy.HorseArcherCount) / playerArmy.TotalCount;
            float infantryRatio = (float)playerArmy.InfantryCount / playerArmy.TotalCount;

            if (cavalryRatio >= 0.40f) return CounterTactic.AntiCavalry;
            if (rangedRatio >= 0.40f) return CounterTactic.AntiRanged;
            if (infantryRatio >= 0.50f) return CounterTactic.AntiInfantry;
            return CounterTactic.Balanced;
        }

        public void ApplyCounterTactics(MobileParty warlordParty, LegitimacyLevel level)
        {
            if (!IsEnabled) return;
            if (warlordParty?.MemberRoster == null) return;

            var playerArmy = AnalyzePlayerArmy();
            var tactic = DetermineCounterTactic(playerArmy);

            float bonusRatio = level switch
            {
                LegitimacyLevel.Outlaw => 0.0f,
                LegitimacyLevel.Rebel => 0.10f,
                LegitimacyLevel.FamousBandit => 0.20f,
                LegitimacyLevel.Warlord => 0.35f,
                LegitimacyLevel.Recognized => 0.50f,
                _ => 0.0f
            };

            if (bonusRatio <= 0f) return;

            int currentSize = warlordParty.MemberRoster.TotalManCount;
            int bonusTroops = Math.Max(3, (int)(currentSize * bonusRatio));

            AddCounterTroops(warlordParty, tactic, bonusTroops, level);

            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("WarlordTactics",
                    $"[{level}] Tactic: {tactic} | Player: Cav={playerArmy.CavalryCount} " +
                    $"Ran={playerArmy.RangedCount} Inf={playerArmy.InfantryCount} | " +
                    $"Bonus: +{bonusTroops} troops");
            }
        }

        private void AddCounterTroops(MobileParty party, CounterTactic tactic, int count, LegitimacyLevel level)
        {
            var roster = party.MemberRoster;

            int targetTier = level switch
            {
                LegitimacyLevel.Rebel => 3,
                LegitimacyLevel.FamousBandit => 4,
                LegitimacyLevel.Warlord => 5,
                LegitimacyLevel.Recognized => 6,
                _ => 2
            };

            float primaryRatio;
            bool primaryMounted, primaryRanged;
            bool secondaryMounted, secondaryRanged;

            switch (tactic)
            {
                case CounterTactic.AntiCavalry:

                    primaryMounted = false; primaryRanged = false;
                    secondaryMounted = false; secondaryRanged = true;
                    primaryRatio = 0.65f;
                    break;

                case CounterTactic.AntiRanged:

                    primaryMounted = true; primaryRanged = false;
                    secondaryMounted = true; secondaryRanged = true;
                    primaryRatio = 0.60f;
                    break;

                case CounterTactic.AntiInfantry:

                    primaryMounted = true; primaryRanged = true;
                    secondaryMounted = true; secondaryRanged = false;
                    primaryRatio = 0.55f;
                    break;

                default:
                    primaryMounted = false; primaryRanged = false;
                    secondaryMounted = true; secondaryRanged = false;
                    primaryRatio = 0.50f;
                    break;
            }

            int primaryCount = (int)(count * primaryRatio);
            int secondaryCount = count - primaryCount;

            var primaryTroop = FindBestTroop(targetTier, primaryMounted, primaryRanged);
            if (primaryTroop != null && primaryCount > 0)
            {
                _ = roster.AddToCounts(primaryTroop, primaryCount);
            }

            var secondaryTroop = FindBestTroop(targetTier, secondaryMounted, secondaryRanged);
            if (secondaryTroop != null && secondaryCount > 0)
            {
                _ = roster.AddToCounts(secondaryTroop, secondaryCount);
            }
        }

        private CharacterObject? FindBestTroop(int targetTier, bool mounted, bool ranged)
        {
            var key = (targetTier, mounted, ranged);
            if (_troopCache.TryGetValue(key, out var cached))
                return cached;

            CharacterObject? best = null;
            int bestTierDiff = int.MaxValue;

            foreach (var character in CharacterObject.All)
            {
                if (character == null || character.IsHero) continue;

                if (character.Occupation != Occupation.Soldier &&
                    character.Occupation != Occupation.Bandit &&
                    character.Occupation != Occupation.Mercenary)
                    continue;

                if (character.IsMounted != mounted || character.IsRanged != ranged) continue;

                int tierDiff = Math.Abs(character.Tier - targetTier);
                if (tierDiff < bestTierDiff ||
                    (tierDiff == bestTierDiff && best != null && character.Tier > best.Tier))
                {
                    bestTierDiff = tierDiff;
                    best = character;
                }
            }

            _troopCache[key] = best;
            return best;
        }

        public enum CounterTactic
        {
            Balanced,
            AntiCavalry,
            AntiRanged,
            AntiInfantry
        }

        public struct ArmyComposition
        {
            public int TotalCount;
            public int InfantryCount;
            public int CavalryCount;
            public int RangedCount;
            public int HorseArcherCount;
            public float AverageTier;

            public static ArmyComposition Default => new ArmyComposition();
        }
    }
}
