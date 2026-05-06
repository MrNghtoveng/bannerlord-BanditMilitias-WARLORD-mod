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
        /// <summary>
        /// Hibrit AI icra motoru. Her tick'te BanditAiPatch tarafından çağrılır.
        /// Dönüş: true = biz niyet belirledik (vanilya DURDUR), false = vanilya devam etsin.
        /// FIX-DUAL-BRAIN: HTNEngine artık doğrudan hareket komutu vermez, 'niyet' belirler.
        /// Gerçek hareket kararı MilitiaDecider tarafından Swarm ve Survival süzgecinden geçirilerek verilir.
        /// </summary>
        public static bool ExecutePlan(MobileParty party, CareerTier tier)
        {
            if (party == null || !party.IsActive) return false;

            var comp = party.PartyComponent as MilitiaPartyComponent;
            if (comp == null) return false;

            var partyPos = CompatibilityLayer.GetPartyPosition(party);

            // ══════════════════════════════════════════════════════
            // KATMANLI ZEKA: Gözlemci (Watcher) Protokolü
            // ══════════════════════════════════════════════════════
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
                            // Niyet: Eve dön
                            AssignIntent(party, comp, CommandType.Retreat, "Watcher returning to base", 0.8f);
                            return true;
                        }
                    }
                    return false; // Vanilla devriye mantığına bırak
                }
            }

            // FIX-DUAL-BRAIN: ShouldRetreat (Survival Instinct) buradan kaldırıldı.
            // MilitiaDecider Katman 0 (TrySurvivalRetreat) bu işi doktrine duyarlı şekilde halleder.

            // ══════════════════════════════════════════════════════
            // TIER 0-1: OUTLAW / REBEL — Gözlem + Basit Kararlar
            // ══════════════════════════════════════════════════════
            if (tier <= CareerTier.Rebel)
            {
                // Acil durum: Çok az asker → Eve dön niyeti
                if (party.MemberRoster.TotalManCount < 5)
                {
                    if (comp.HomeSettlement != null)
                    {
                        AssignIntent(party, comp, CommandType.Retreat, "Too weak, need recruits", 0.9f);
                        return true;
                    }
                }

                // Aktif stratejik emir varsa vanillayı durdur
                if (comp.CurrentOrder != null && comp.CurrentOrder.Type != CommandType.Patrol)
                {
                    return true;
                }

                return false; 
            }

            // ══════════════════════════════════════════════════════
            // TIER 2: FAMOUS BANDIT — Taktiksel Kararlar
            // ══════════════════════════════════════════════════════
            if (tier == CareerTier.FamousBandit)
            {
                var order = comp.CurrentOrder;
                if (order == null || order.Type == CommandType.Patrol)
                    return false;

                return true;
            }

            // ══════════════════════════════════════════════════════
            // TIER 3+: WARLORD+ — Tam Stratejik Kontrol
            // ══════════════════════════════════════════════════════
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
