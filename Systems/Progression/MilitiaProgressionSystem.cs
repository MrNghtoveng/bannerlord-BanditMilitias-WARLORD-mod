using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Progression
{
    [BanditMilitias.Core.Components.ModuleDependency(
        typeof(BanditMilitias.Intelligence.Strategic.WarlordSystem))]
    [BanditMilitias.Core.Components.AutoRegister(Priority = 80, IsCritical = true)]
    public class MilitiaProgressionSystem : MilitiaModuleBase
    {
        private static readonly Lazy<MilitiaProgressionSystem> _inst =
            new(() => new MilitiaProgressionSystem());
        public static MilitiaProgressionSystem Instance => _inst.Value;

        public override string ModuleName => "MilitiaProgressionSystem";
        public override bool IsEnabled => true;
        public override int Priority => 80;


        private Dictionary<string, int> _hordeXpPool = new();


        public override void OnDailyTick()
        {
            if (!IsEnabled) return;

            var snapshot = ModuleManager.Instance.ActiveMilitias;


            for (int p = 0; p < snapshot.Count; p++)
            {
                var party = snapshot[p];
                if (party == null || !party.IsActive || party.MemberRoster == null) continue;

                var comp = party.GetMilitiaComponent();
                if (comp == null) continue;

                var warlord = comp.AssignedWarlord
                              ?? WarlordSystem.Instance.GetWarlordForParty(party);
                if (warlord == null) continue;

                int xp = CalculatePassiveTrainingXp(comp, warlord);
                bool anyAdded = ApplyXpToRoster(party, xp);


                if (anyAdded)
                    TryUpgradeRoster(party, warlord);
            }


            DistributeHordePool();
        }

        public void OnBattleVictory(MobileParty winner, float enemyStrength, float winnerStrength)
        {
            if (winner == null || !winner.IsActive || winner.MemberRoster == null) return;
            var comp = winner.GetMilitiaComponent();
            if (comp == null) return;

            var warlord = comp.AssignedWarlord
                          ?? WarlordSystem.Instance.GetWarlordForParty(winner);
            if (warlord == null) return;


            int xpReward = (int)(enemyStrength * 50f);


            if (winnerStrength > 0f && winnerStrength < enemyStrength * 0.8f)
                xpReward = (int)(xpReward * 1.5f);

            xpReward = Math.Max(xpReward, 200);


            bool anyAdded = ApplyXpToRoster(winner, xpReward);
            if (anyAdded)
                TryUpgradeRoster(winner, warlord);


            AddToHordePool(warlord, xpReward);

            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.TestLog(
                    $"[Progression] {winner.Name}: Battle XP={xpReward} (enemy strength={enemyStrength:F0})",
                    Colors.Green);
            }
        }


        public void AddToHordePool(Warlord warlord, int baseAmount)
        {
            if (warlord == null || baseAmount <= 0) return;

            if (!_hordeXpPool.ContainsKey(warlord.StringId))
                _hordeXpPool[warlord.StringId] = 0;

            _hordeXpPool[warlord.StringId] += (int)(baseAmount * 0.15f);
        }

        private void DistributeHordePool()
        {
            foreach (var kv in _hordeXpPool)
            {
                if (kv.Value <= 0) continue;

                var warlord = WarlordSystem.Instance.GetWarlordById(kv.Key);
                if (warlord == null) continue;

                var militias = warlord.CommandedMilitias;
                if (militias == null || militias.Count == 0) continue;

                int xpPerParty = kv.Value / militias.Count;
                if (xpPerParty <= 0) continue;

                for (int i = 0; i < militias.Count; i++)
                {
                    var party = militias[i];
                    if (party == null || !party.IsActive) continue;

                    bool anyAdded = ApplyXpToRoster(party, xpPerParty);
                    if (anyAdded)
                        TryUpgradeRoster(party, warlord);
                }
            }


            var keys = new List<string>(_hordeXpPool.Keys);
            foreach (var k in keys)
                _hordeXpPool[k] = 0;
        }


        private static int CalculatePassiveTrainingXp(MilitiaPartyComponent comp, Warlord warlord)
        {
            int xp = 80;


            if (warlord.Gold > 20_000f) xp += 50;
            if (warlord.Gold > 50_000f) xp += 50;


            if (comp.Role == MilitiaPartyComponent.MilitiaRole.VeteranCaptain)
                xp += 40;

            return xp;
        }


        private static bool ApplyXpToRoster(MobileParty party, int amount)
        {
            if (amount <= 0 || party?.MemberRoster == null) return false;

            bool anyAdded = false;
            int count = party.MemberRoster.Count;

            for (int i = 0; i < count; i++)
            {
                var element = party.MemberRoster.GetElementCopyAtIndex(i);
                if (element.Character == null || element.Number <= 0) continue;

                int xpToAdd = element.Character.Tier <= 2
                    ? (int)(amount * 1.5f)

                    : amount;

                party.MemberRoster.AddXpToTroopAtIndex(xpToAdd, i);
                anyAdded = true;
            }

            return anyAdded;
        }


        private static void TryUpgradeRoster(MobileParty party, Warlord warlord)
        {
            if (party?.MemberRoster == null || warlord == null) return;

            bool upgradedAny = false;


            for (int i = party.MemberRoster.Count - 1; i >= 0; i--)
            {
                var element = party.MemberRoster.GetElementCopyAtIndex(i);
                if (element.Character == null || element.Number <= 0) continue;
                if (element.Character.UpgradeTargets == null
                    || element.Character.UpgradeTargets.Length == 0) continue;

                int xpCost = element.Character.GetUpgradeXpCost(party.Party, 0);
                if (xpCost <= 0) continue;

                int currentXp = CompatibilityLayer.GetElementXpAtIndex(party.MemberRoster, i);
                int upgradeReadyCount = currentXp / xpCost;
                if (upgradeReadyCount <= 0) continue;


                int toUpgrade = Math.Min(upgradeReadyCount, Math.Max(1, element.Number / 3));


                int goldCostPerTroop = Math.Max(1, element.Character.Tier * 10);
                int totalGoldCost = toUpgrade * goldCostPerTroop;

                if (warlord.Gold < totalGoldCost)
                {
                    toUpgrade = (int)(warlord.Gold / goldCostPerTroop);
                    totalGoldCost = toUpgrade * goldCostPerTroop;
                }

                if (toUpgrade <= 0) continue;

                var targetTroop = element.Character.UpgradeTargets[0];


                party.MemberRoster.AddToCounts(element.Character, -toUpgrade);
                party.MemberRoster.AddToCounts(targetTroop, toUpgrade);
                warlord.Gold -= totalGoldCost;
                upgradedAny = true;


                int remainingXp = currentXp - (toUpgrade * xpCost);
                TrySetRemainingXp(party, element.Character, remainingXp);

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.TestLog(
                        $"[Upgrade] {party.Name}: {toUpgrade}x {element.Character.Name} → {targetTroop.Name} (-{totalGoldCost} Gold)",
                        Colors.Yellow);
                }
            }

            if (upgradedAny)
                party.RecentEventsMorale += 5f;
        }

        private static void TrySetRemainingXp(MobileParty party, CharacterObject character, int remainingXp)
        {
            if (remainingXp <= 0) return;

            try
            {
                for (int j = 0; j < party.MemberRoster.Count; j++)
                {
                    var el = party.MemberRoster.GetElementCopyAtIndex(j);
                    if (el.Character != character) continue;


                    int current = CompatibilityLayer.GetElementXpAtIndex(party.MemberRoster, j);
                    if (current > remainingXp)
                    {


                        int count = el.Number;
                        party.MemberRoster.AddToCounts(character, -count);
                        party.MemberRoster.AddToCounts(character, count);


                        int newIdx = FindSlotIndex(party, character);
                        if (newIdx >= 0 && remainingXp > 0)
                            party.MemberRoster.AddXpToTroopAtIndex(remainingXp, newIdx);
                    }
                    break;
                }
            }
            catch (Exception ex)
            {


                DebugLogger.Warning("MilitiaProgressionSystem",
                    $"TrySetRemainingXp failed for {character?.Name}: {ex.Message}");
            }
        }

        private static int FindSlotIndex(MobileParty party, CharacterObject character)
        {
            for (int i = 0; i < party.MemberRoster.Count; i++)
            {
                if (party.MemberRoster.GetElementCopyAtIndex(i).Character == character)
                    return i;
            }
            return -1;
        }

        public void UpgradePartyTroopsCompat(MobileParty party, Warlord warlord)
        {
            if (!IsEnabled) return;
            TryUpgradeRoster(party, warlord);
        }


        public override void OnTick(float dt) { }
        public override void OnHourlyTick() { }

        public override string GetDiagnostics()
        {
            int poolTotal = 0;
            foreach (var v in _hordeXpPool.Values) poolTotal += v;
            return $"MilitiaProgression: HordePool={poolTotal} XP | Warlords={_hordeXpPool.Count}";
        }

        public override void SyncData(IDataStore ds)
        {
            _ = ds.SyncData("_hordeXpPool_v1", ref _hordeXpPool);
            if (ds.IsLoading && _hordeXpPool == null)
            {
                _hordeXpPool = new Dictionary<string, int>();
            }
        }

        public override void Cleanup()
        {
            _hordeXpPool.Clear();
        }
    }
}


