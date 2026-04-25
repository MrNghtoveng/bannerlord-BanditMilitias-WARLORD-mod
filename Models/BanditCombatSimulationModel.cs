using BanditMilitias.Components;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Models
{
    public class BanditCombatSimulationModel : DefaultCombatSimulationModel
    {
        // ── GetSimulationDamage ─────────────────────────────────────────
        // BUG-DEAD-3 DÜZELTMESİ:
        // Eski kod: T1 savunucu kontrolü erken return yapıyordu → saldırı multiplier'ı
        //           yok sayılıyordu. Ambush'taki bir milis T1 savunucuya karşı
        //           bonus yerine ceza alıyordu.
        // Yeni kod: Her iki çarpan hesaplanır, sonra birleştirilir.
        public override int GetSimulationDamage(MobileParty attackerParty, MobileParty defenderParty)
        {
            int baseDamage = base.GetSimulationDamage(attackerParty, defenderParty);

            // ── Saldırı çarpanı ────────────────────────────────────────
            float attackMult = 1.0f;

            if (attackerParty?.PartyComponent is MilitiaPartyComponent attackerComp)
            {
                // Pusu bonusu
                if (attackerComp.CurrentOrder?.Type == Intelligence.Strategic.CommandType.Ambush)
                    attackMult += 0.75f;

                // Arazi bonusu: Ormanda haydut avantajı
                if (Campaign.Current?.MapSceneWrapper != null)
                {
                    var pos = Infrastructure.CompatibilityLayer.GetPartyPosition(attackerParty);
                    if (pos.IsValid &&
                        Campaign.Current.MapSceneWrapper.GetFaceTerrainType(pos) == TerrainType.Forest)
                        attackMult += 0.30f;
                }

                // Sayısal üstünlük bonusu
                int attackerCount = attackerParty.MemberRoster?.TotalManCount ?? 0;
                int defenderCount = defenderParty?.MemberRoster?.TotalManCount ?? 1;
                if (attackerCount > defenderCount * 1.5f)
                    attackMult += 0.20f;

                // Gece baskını bonusu
                if (Campaign.Current?.IsNight == true)
                    attackMult += 0.25f;
            }

            // ── Savunma çarpanı (T0-T2 hayatta kalma) ─────────────────
            // DÜZELTİLDİ: Artık erken return yok. Savunma indirimi, saldırı
            // bonusuyla birleştirilir; her iki etki de nihai hasara yansır.
            float defenseMult = 1.0f;

            if (defenderParty?.PartyComponent is MilitiaPartyComponent &&
                defenderParty.MemberRoster != null)
            {
                float t1Ratio = GetLowTierRatio(defenderParty);

                if (t1Ratio > 0.5f)
                {
                    // T0-T2 yoğunluklu milis: Hayatta kalma şansı için hasar indirimi.
                    // İndirim oranı T1 yoğunluğuyla orantılı (max %35).
                    defenseMult = 1.0f - (t1Ratio * 0.35f);
                }
            }

            // İki çarpan birleştirilir: saldırı bonusu ve savunma indirimi aynı anda etki eder.
            float finalMult = attackMult * defenseMult;

            return (int)(baseDamage * finalMult);
        }

        // ── GetSimulationCasualties ─────────────────────────────────────
        // GetSimulationDamage tek başına yetersiz: engine hasarı hesaplar,
        // kayıpları ayrı bir çağrıyla belirler. T0-T2 birimlerin düşük stat'ları
        // DefaultCombatSimulationModel'in kayıp hesabında onları ezer.
        // Bu override, milis savunucuların minimum hayatta kalma şansını garantiler.
        //
        // NOT: Bu metodun tam imzası Bannerlord versiyonuna göre değişebilir.
        // Derleme hatası alırsanız DefaultCombatSimulationModel'deki imzayı
        // ILSpy ile doğrulayın ve aşağıdaki parametreleri güncelleyin.
        public override int GetSimulationCasualties(
            MapEventParty attackerParty,
            MapEventParty defenderParty,
            int attackerSideCount,
            int defenderSideCount,
            float strengths0,
            float strengths1,
            float advantage)
        {
            int baseCasualties = base.GetSimulationCasualties(
                attackerParty, defenderParty,
                attackerSideCount, defenderSideCount,
                strengths0, strengths1, advantage);

            // Savunucu milis ve T0-T2 yoğunluklu mu?
            var defenderMobile = defenderParty?.Party?.MobileParty;
            if (defenderMobile?.PartyComponent is MilitiaPartyComponent &&
                defenderMobile.MemberRoster != null)
            {
                float t1Ratio = GetLowTierRatio(defenderMobile);

                if (t1Ratio > 0.5f)
                {
                    // Kayıp oranını T1 yoğunluğuyla orantılı olarak düşür.
                    // t1Ratio=0.5 → %20 indirim, t1Ratio=1.0 → %40 indirim.
                    float reduction = t1Ratio * 0.40f;
                    return (int)(baseCasualties * (1f - reduction));
                }
            }

            return baseCasualties;
        }

        // ── Yardımcılar ────────────────────────────────────────────────
        /// <summary>
        /// Partideki T0-T2 birimlerin toplam asker içindeki oranını döner (0-1).
        /// </summary>
        private static float GetLowTierRatio(MobileParty party)
        {
            int total = party.MemberRoster.TotalManCount;
            if (total <= 0) return 0f;

            int lowTierCount = 0;
            foreach (var troop in party.MemberRoster.GetTroopRoster())
            {
                if (troop.Character != null && troop.Character.Tier <= 2)
                    lowTierCount += troop.Number;
            }

            return (float)lowTierCount / total;
        }
    }
}
