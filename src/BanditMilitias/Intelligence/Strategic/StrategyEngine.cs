using System;
using System.Linq;
using System.Collections.Generic;
using BanditMilitias.Infrastructure;
using BanditMilitias.Debug;
using BanditMilitias.Intelligence.ML;
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

        /// <summary>
        /// HİBRİT AI: Bölgesel Çaresizlik Doktrini (Incubation).
        /// Eğer sığınağa bağlı toplam haydut gücü kritik eşiğin altındaysa,
        /// tüm birimlere geri çekilip kuluçkaya yatma (güç toplama) emri verilir.
        /// </summary>
        public static void EvaluateRegionalStrategy(Settlement hideout)
        {
            if (hideout == null || !hideout.IsHideout) return;

            var linkedMilitias = ModuleManager.Instance.ActiveMilitias
                .Where(p => (p.PartyComponent as MilitiaPartyComponent)?.HomeSettlement == hideout && p.IsActive)
                .ToList();

            if (linkedMilitias.Count == 0) return;

            int totalTroops = linkedMilitias.Sum(m => m.MemberRoster.TotalManCount);

            // Çaresizlik Eşiği: Toplam 35 askerden azsa bu bölge 'infaz' bölgesidir.
            if (totalTroops < 35)
            {
                foreach (var party in linkedMilitias)
                {
                    var comp = party.PartyComponent as MilitiaPartyComponent;
                    if (comp == null) continue;

                    // Eğer zaten sığınakta değilse veya başka bir kritik emir yoksa geri çağır
                    if (party.CurrentSettlement != hideout)
                    {
                        comp.CurrentOrder = new StrategicCommand
                        {
                            Type = CommandType.Defend,
                            TargetLocation = CompatibilityLayer.GetSettlementPosition(hideout),
                            Reason = "DesperationDoctrine:RetreatToSafety"
                        };
                        
                        // Yapay zekayı 12-18 saat uyutarak pusuya yatır (Pusuda toparlansınlar)
                        comp.SleepFor(TaleWorlds.Core.MBRandom.RandomFloatRanged(12f, 18f));
                    }
                }

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.TestLog($"[STRATEGY] {hideout.Name} bölgesinde ÇARESİZLİK DOKTRİNİ aktif. {linkedMilitias.Count} parti kuluçkaya çekildi.");
                }
            }
        }
    }
}
