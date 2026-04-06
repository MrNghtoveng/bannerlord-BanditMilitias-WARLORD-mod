using BanditMilitias.Systems.Grid;
using BanditMilitias.Infrastructure;
using BanditMilitias.Debug;
using BanditMilitias.Intelligence.ML;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Components;
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

            // QiRL karar alma — TÜM tier'larda aktif (Evrimsel öğrenme için)
            var state = AILearningSystem.Instance.DetermineState(party);
            var action = AILearningSystem.Instance.GetBestAction(state);

            // Rütbeye göre menzil → evrimsel büyüme
            float searchRadius = tier switch
            {
                CareerTier.Eskiya => 20f,     // Çok yakın — sadece etraftaki fırsatlar
                CareerTier.Rebel => 30f,      // Biraz daha geniş
                CareerTier.FamousBandit => 50f, // Bölgesel menzil
                CareerTier.Warlord => 100f,   // Geniş operasyonel alan
                _ => 150f                      // Harita geneli (Taninmis/Fatih)
            };

            // Hedef bul
            Settlement? target = CampaignGridSystem.FindMostVulnerableTarget(party, searchRadius);

            if (target != null && ActionRequiresTarget(action))
            {
                comp.CurrentOrder = CreateOrder(action, target);
                DebugLogger.Info("StrategyEngine",
                    $"[{tier}] {party.Name} -> {action} @ {target.Name} (radius={searchRadius})");
            }
            else if (action == AIAction.LayLow || action == AIAction.Merge)
            {
                // Hedefsiz komutlar -> Eve dönüş / Saklanma
                comp.CurrentOrder = new StrategicCommand
                {
                    Type = action == AIAction.LayLow ? CommandType.Retreat : CommandType.Defend,
                    Reason = $"QiRL: {action}"
                };
            }
            else
            {
                // Hedef bulunamadı veya aksiyon hedef gerektirmiyor -> Komutu temizle, vanilya devriye yapsın
                comp.CurrentOrder = null;
            }
        }

        private static bool ActionRequiresTarget(AIAction action) => action switch
        {
            AIAction.Raiding or AIAction.Ambush or AIAction.Engage
            or AIAction.Hunt or AIAction.Extort or AIAction.Defend => true,
            _ => false
        };

        private static StrategicCommand CreateOrder(AIAction action, Settlement target) => new()
        {
            Type = action switch
            {
                AIAction.Raiding => CommandType.Raid,
                AIAction.Ambush => CommandType.Ambush,
                AIAction.Engage => CommandType.Engage,
                AIAction.Hunt => CommandType.Hunt,
                AIAction.Extort => CommandType.CommandExtort,
                AIAction.Defend => CommandType.Defend,
                _ => CommandType.Patrol
            },
            TargetLocation = CompatibilityLayer.GetSettlementPosition(target),
            Reason = $"QiRL-{action}"
        };
    }
}
