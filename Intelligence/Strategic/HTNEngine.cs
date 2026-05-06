using BanditMilitias.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Intelligence.AI;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace BanditMilitias.Intelligence.Strategic
{
    public class HTNEngine
    {


        public static bool ExecutePlan(MobileParty party, CareerTier tier)
        {
            if (party == null || !party.IsActive) return false;

            var comp = party.PartyComponent as MilitiaPartyComponent;
            if (comp == null) return false;

            var partyPos = CompatibilityLayer.GetPartyPosition(party);


            if (comp.IsWatcher)
            {
                if (comp.CurrentOrder == null ||
                    (comp.CurrentOrder.Type != CommandType.Engage &&
                     comp.CurrentOrder.Type != CommandType.Hunt))
                {
                    if (comp.HomeSettlement != null && partyPos.IsValid)
                    {
                        var settlementPos = CompatibilityLayer.GetSettlementPosition(comp.HomeSettlement);
                        if (settlementPos.IsValid &&
                            partyPos.DistanceSquared(settlementPos) > 400f)
                        {


                            AssignIntent(party, comp, CommandType.Retreat, "Watcher returning to base", 0.8f);
                            return true;
                        }
                    }
                    return false;

                }
            }


            if (tier <= CareerTier.Rebel)
            {


                if (party.MemberRoster.TotalManCount < 5)
                {
                    if (comp.HomeSettlement != null)
                    {
                        AssignIntent(party, comp, CommandType.Retreat, "Too weak, need recruits", 0.9f);
                        return true;
                    }
                }


                if (comp.CurrentOrder != null && comp.CurrentOrder.Type != CommandType.Patrol)
                {
                    return true;
                }

                return false;
            }


            if (tier == CareerTier.FamousBandit)
            {
                var order = comp.CurrentOrder;
                if (order == null || order.Type == CommandType.Patrol)
                    return false;

                return true;
            }


            var warlordOrder = comp.CurrentOrder;

            if (warlordOrder == null || warlordOrder.Type == CommandType.Patrol)
                return false;

            return true;
        }

        private static void AssignIntent(MobileParty party, MilitiaPartyComponent comp, CommandType type, string reason, float priority)
        {
            if (comp.CurrentOrder?.Type == type) return;

            CustomMilitiaAI.AssignCommand(party, new StrategicCommand
            {
                Type = type,
                Priority = priority,
                Reason = reason,
                TargetLocation = comp.HomeSettlement != null ? CompatibilityLayer.GetSettlementPosition(comp.HomeSettlement) : Vec2.Invalid
            });
        }
    }
}
