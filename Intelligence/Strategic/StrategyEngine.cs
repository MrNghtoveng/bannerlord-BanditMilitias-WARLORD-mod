using System;
using System.Linq;
using System.Collections.Generic;
using BanditMilitias.Infrastructure;
using BanditMilitias.Debug;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Components;
using BanditMilitias.Systems.Grid;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BanditMilitias.Intelligence.Strategic
{
    public class StrategyEngine
    {
        public static void UpdateWarlordStrategy(MobileParty party)
        {
            var comp = party.PartyComponent as MilitiaPartyComponent;
            if (comp == null) return;

            var warlord = WarlordSystem.Instance.GetWarlordForParty(party);
            if (warlord == null) return;

            var tier = WarlordCareerSystem.Instance.GetTier(warlord.StringId);


            CommandType cmdType = DetermineHeuristicCommand(tier, party);


            float searchRadius = tier switch
            {
                CareerTier.Outlaw => 20f,

                CareerTier.Rebel => 30f,

                CareerTier.FamousBandit => 50f,

                CareerTier.Warlord => 100f,

                _ => 150f

            };


            Settlement? target = CampaignGridSystem.FindMostVulnerableTarget(party, searchRadius);

            if (target != null && CommandRequiresTarget(cmdType))
            {
                comp.CurrentOrder = new StrategicCommand
                {
                    Type = cmdType,
                    TargetLocation = CompatibilityLayer.GetSettlementPosition(target),
                    Reason = $"Heuristic-{cmdType}"
                };
                DebugLogger.Info("StrategyEngine",
                    $"[{tier}] {party.Name} -> {cmdType} @ {target.Name} (radius={searchRadius})");
            }
            else if (cmdType == CommandType.CommandLayLow || cmdType == CommandType.AvoidCrowd)
            {


                comp.CurrentOrder = new StrategicCommand
                {
                    Type = cmdType,
                    Reason = $"HeuristicFallback"
                };
            }
            else
            {


                comp.CurrentOrder = null;
            }
        }

        private static bool CommandRequiresTarget(CommandType cmd) => cmd switch
        {
            CommandType.Raid or CommandType.Ambush or CommandType.Engage
            or CommandType.Hunt or CommandType.CommandExtort or CommandType.Defend => true,
            _ => false
        };

        private static CommandType DetermineHeuristicCommand(CareerTier tier, MobileParty party)
        {
            float strength = CompatibilityLayer.GetTotalStrength(party);
            if (strength < 40f) return CommandType.CommandLayLow;

            return tier switch
            {
                CareerTier.Outlaw => CommandType.Hunt,
                CareerTier.Rebel => CommandType.Raid,
                CareerTier.FamousBandit => CommandType.Ambush,
                CareerTier.Warlord => CommandType.CommandExtort,
                CareerTier.Recognized => CommandType.Engage,
                CareerTier.Conqueror => CommandType.Engage,
                _ => CommandType.Patrol
            };
        }


        public static void EvaluateAllRegionalStrategies()
        {
            var activeMilitias = ModuleManager.Instance.ActiveMilitias;
            if (activeMilitias.Count == 0) return;


            var byHideout = new Dictionary<Settlement, (List<MobileParty> parties, int troops)>();
            foreach (var party in activeMilitias)
            {
                if (party == null || !party.IsActive) continue;
                var comp = party.PartyComponent as MilitiaPartyComponent;
                var home = comp?.HomeSettlement;
                if (home == null || !home.IsHideout) continue;

                if (!byHideout.TryGetValue(home, out var entry))
                    entry = (new List<MobileParty>(), 0);
                entry.parties.Add(party);
                entry.troops += party.MemberRoster?.TotalManCount ?? 0;
                byHideout[home] = entry;
            }


            foreach (var kv in byHideout)
            {
                var hideout = kv.Key;
                var (parties, totalTroops) = kv.Value;
                if (totalTroops >= 35) continue;

                foreach (var party in parties)
                {
                    var comp = party.PartyComponent as MilitiaPartyComponent;
                    if (comp == null || party.CurrentSettlement == hideout) continue;

                    comp.CurrentOrder = new StrategicCommand
                    {
                        Type = CommandType.Defend,
                        TargetLocation = CompatibilityLayer.GetSettlementPosition(hideout),
                        Reason = "DesperationDoctrine:RetreatToSafety"
                    };
                    comp.SleepFor(TaleWorlds.Core.MBRandom.RandomFloatRanged(12f, 18f));
                }

                if (Settings.Instance?.TestingMode == true)
                    DebugLogger.TestLog($"[STRATEGY] {hideout.Name} bölgesinde ÇARESİZLİK DOKTRİNİ aktif. {parties.Count} parti kuluçkaya çekildi.");
            }
        }


        public static void EvaluateRegionalStrategy(Settlement hideout)
        {
            if (hideout == null || !hideout.IsHideout) return;


            var parties = ModuleManager.Instance.ActiveMilitias
                .Where(p => (p.PartyComponent as MilitiaPartyComponent)?.HomeSettlement == hideout && p.IsActive)
                .ToList();
            if (parties.Count == 0) return;
            int totalTroops = parties.Sum(m => m.MemberRoster.TotalManCount);
            if (totalTroops >= 35) return;
            foreach (var party in parties)
            {
                var comp = party.PartyComponent as MilitiaPartyComponent;
                if (comp == null || party.CurrentSettlement == hideout) continue;
                comp.CurrentOrder = new StrategicCommand
                {
                    Type = CommandType.Defend,
                    TargetLocation = CompatibilityLayer.GetSettlementPosition(hideout),
                    Reason = "DesperationDoctrine:RetreatToSafety"
                };
                comp.SleepFor(TaleWorlds.Core.MBRandom.RandomFloatRanged(12f, 18f));
            }
        }
    }
}
