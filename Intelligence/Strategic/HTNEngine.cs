using BanditMilitias.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Systems.Progression;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

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

            // ── BUG-CRASH-1 FIX: party.Position2D v1.3.15'te kaldırıldı ──
            // Eski kod: party.Position2D.DistanceSquared(...)
            // Yeni kod: CompatibilityLayer.GetPartyPosition(party) üzerinden
            var partyPos = CompatibilityLayer.GetPartyPosition(party);

            // ══════════════════════════════════════════════════════
            // KATMANLI ZEKA: Gözlemci (Watcher) Protokolü
            // Gözlemciler savaşmak yerine geniş alan taraması yapar.
            // ══════════════════════════════════════════════════════
            if (comp.IsWatcher)
            {
                // Acil savaş emri yoksa, Gözlemci özgürce dolaşır veya sığınağa yakın durur
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
                            CompatibilityLayer.SetMoveGoToSettlement(party, comp.HomeSettlement);
                            return true;
                        }
                    }
                    return false; // Vanilla devriye mantığına bırak
                }
            }

            // ══════════════════════════════════════════════════════
            // HAYATTA KALMA İÇGÜDÜSÜ (Survival Instinct)
            // Güç oranı kritik eşiği geçtiyse → Geri çekil.
            // T0-T2 partilerin ölüm sarmalına girmesini engeller.
            // ══════════════════════════════════════════════════════
            if (ShouldRetreat(party, partyPos))
            {
                if (comp.HomeSettlement != null)
                {
                    CompatibilityLayer.SetMoveGoToSettlement(party, comp.HomeSettlement);
                    DebugLogger.Info("HTNEngine",
                        $"[SurvivalInstinct] {party.Name} geri çekiliyor (çok zayıf).");
                    return true;
                }
            }

            // ══════════════════════════════════════════════════════
            // TIER 0-1: OUTLAW / REBEL — Gözlem + Basit Kararlar
            // Vanilya hareket eder, biz sadece acil durumlarda araya gireriz
            // ══════════════════════════════════════════════════════
            if (tier <= CareerTier.Rebel)
            {
                // Acil durum: Çok az asker → Eve dön
                if (party.MemberRoster.TotalManCount < 5)
                {
                    if (comp.HomeSettlement != null)
                    {
                        CompatibilityLayer.SetMoveGoToSettlement(party, comp.HomeSettlement);
                        return true;
                    }
                }

                // Aktif stratejik emir varsa uygula
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

                if (order == null || order.Type == CommandType.Patrol)
                    return false;

                ExecuteCommand(party, order);
                return true;
            }

            // ══════════════════════════════════════════════════════
            // TIER 3+: WARLORD+ — Tam Stratejik Kontrol
            // Komut yoksa veya devriye ise → vanilya (performans)
            // ══════════════════════════════════════════════════════
            var warlordOrder = comp.CurrentOrder;

            if (warlordOrder == null || warlordOrder.Type == CommandType.Patrol)
                return false;

            ExecuteCommand(party, warlordOrder);
            return true;
        }

        // ── Hayatta Kalma İçgüdüsü ─────────────────────────────────────
        /// <summary>
        /// Yakındaki düşman tehdidi partinin gücünü FLEE_RATIO kat aşıyorsa true döner.
        /// SpatialGrid kullanılır, ancak yoksa veya hata verirse false ile devam edilir.
        /// </summary>
        private const float FLEE_STRENGTH_RATIO = 2.5f;

        private static bool ShouldRetreat(MobileParty party, Vec2 partyPos)
        {
            try
            {
                float myStrength = party.Party?.TotalStrength ?? 0f;
                if (myStrength <= 0f) return false;

                // Yalnızca T0-T2 ağırlıklı partiler için aktif — performans tasarrufu
                if (myStrength > 200f) return false;

                var nearbyResult = new System.Collections.Generic.List<TaleWorlds.CampaignSystem.Party.MobileParty>(16);
                Systems.Grid.SpatialGridSystem.Instance.QueryNearby(partyPos, 20f, nearbyResult);

                float threatStrength = 0f;
                foreach (var other in nearbyResult)
                {
                    if (other == null || other == party || !other.IsActive) continue;
                    if (other.MapFaction == null || party.MapFaction == null) continue;
                    if (!other.MapFaction.IsAtWarWith(party.MapFaction)) continue;

                    threatStrength += other.Party?.TotalStrength ?? 0f;
                }

                return threatStrength > myStrength * FLEE_STRENGTH_RATIO;
            }
            catch
            {
                return false; // Güvenli fallback: saldırıya devam
            }
        }

        // ── Komut yürütücü ──────────────────────────────────────────────
        /// <summary>
        /// Stratejik komutu Bannerlord hareket komutuna çevirir.
        /// </summary>
        private static void ExecuteCommand(MobileParty party, StrategicCommand order)
        {
            var comp = party.PartyComponent as MilitiaPartyComponent;

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
                    if (comp?.HomeSettlement != null)
                        CompatibilityLayer.SetMoveGoToSettlement(party, comp.HomeSettlement);
                    break;

                case CommandType.CommandExtort:
                case CommandType.CommandBuildRepute:
                case CommandType.Harass:
                    if (order.TargetLocation != default && order.TargetLocation.IsValid)
                        CompatibilityLayer.SetMoveGoToPoint(party, order.TargetLocation);
                    break;

                default:
                    // Bilinmeyen veya Patrol → handled=false ile vanillanın alması gerekir
                    break;
            }
        }
    }
}
