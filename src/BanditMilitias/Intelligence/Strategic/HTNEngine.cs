using BanditMilitias.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Systems.Progression;
using TaleWorlds.CampaignSystem.Party;

namespace BanditMilitias.Intelligence.Strategic
{
    public class HTNEngine
    {
        /// <summary>
        /// Hibrit AI icra motoru. Her tick'te BanditAiPatch tarafından çağrılır.
        /// Dönüş: true = biz devraldık (vanilya DURDUR), false = vanilya devam etsin.
        /// </summary>
        public static bool ExecutePlan(MobileParty party, CareerTier tier)
        {
            if (party == null || !party.IsActive) return false;

            var comp = party.PartyComponent as MilitiaPartyComponent;
            if (comp == null) return false;

            // ══════════════════════════════════════════════════════
            // TIER 0-1: OUTLAW / REBEL — Gözlem + Basit Kararlar
            // Vanilya hareket eder, biz sadece acil durumlarda araya gireriz
            // ══════════════════════════════════════════════════════
            if (tier <= CareerTier.Rebel)
            {
                // Acil durum: Çok zayıfsak → Eve dön veya Saklan
                if (party.MemberRoster.TotalManCount < 5)
                {
                    // Basit "eve dön" komutu
                    if (comp.HomeSettlement != null)
                    {
                        CompatibilityLayer.SetMoveGoToSettlement(party, comp.HomeSettlement);
                        return true; // Biz devraldık
                    }
                }
                
                // Aktif bir stratejik emir varsa (BanditBrain'den gelmiş olabilir) uygula
                if (comp.CurrentOrder != null && comp.CurrentOrder.Type != CommandType.Patrol)
                {
                    ExecuteCommand(party, comp.CurrentOrder);
                    return true;
                }
                
                return false; // Geri kalan her şey vanillanın
            }

            // ══════════════════════════════════════════════════════
            // TIER 2: FAMOUS BANDIT — Taktiksel Kararlar
            // Pusu, haraç, avlama → biz. Devriye, genel hareket → vanilya
            // ══════════════════════════════════════════════════════
            if (tier == CareerTier.FamousBandit)
            {
                var order = comp.CurrentOrder;
                
                // Komut yoksa veya basit devriye → vanilya yapsın
                if (order == null || order.Type == CommandType.Patrol)
                    return false;
                
                // Özel taktiksel komutlar → biz devralıyoruz
                ExecuteCommand(party, order);
                return true;
            }

            // ══════════════════════════════════════════════════════
            // TIER 3+: WARLORD+ — Tam Stratejik Kontrol
            // Tüm kararları biz veriyoruz, ama "işsiz" kalırsa vanilya devriye
            // ══════════════════════════════════════════════════════
            var warlordOrder = comp.CurrentOrder;
            
            // Komut yoksa veya devriye ise → vanilya yapsın (performans için)
            if (warlordOrder == null || warlordOrder.Type == CommandType.Patrol)
                return false;
            
            ExecuteCommand(party, warlordOrder);
            return true;
        }

        /// <summary>
        /// Stratejik komutu Bannerlord hareket komutuna çevirir.
        /// </summary>
        private static void ExecuteCommand(MobileParty party, StrategicCommand order)
        {
            switch (order.Type)
            {
                case CommandType.Raid:
                case CommandType.CommandRaidVillage:
                    if (order.TargetLocation != default && order.TargetLocation.IsValid)
                        CompatibilityLayer.SetMoveGoToPoint(party, order.TargetLocation);
                    break;

                case CommandType.Engage:
                case CommandType.Hunt:
                    if (order.TargetParty != null && order.TargetParty.IsActive)
                        CompatibilityLayer.SetMoveEngageParty(party, order.TargetParty);
                    break;

                case CommandType.Ambush:
                    if (order.TargetLocation != default && order.TargetLocation.IsValid)
                        CompatibilityLayer.SetMoveGoToPoint(party, order.TargetLocation);
                    break;

                case CommandType.Defend:
                case CommandType.Retreat:
                    var comp = party.PartyComponent as MilitiaPartyComponent;
                    if (comp != null && comp.HomeSettlement != null)
                        CompatibilityLayer.SetMoveGoToSettlement(party, comp.HomeSettlement);
                    break;

                case CommandType.CommandExtort:
                case CommandType.CommandBuildRepute:
                case CommandType.Harass:
                    if (order.TargetLocation != default && order.TargetLocation.IsValid)
                        CompatibilityLayer.SetMoveGoToPoint(party, order.TargetLocation);
                    break;

                default:
                    // Bilinmeyen veya Patrol → Vanilla devralsın (handled=false olmalıydı zaten)
                    break;
            }
        }
    }
}
