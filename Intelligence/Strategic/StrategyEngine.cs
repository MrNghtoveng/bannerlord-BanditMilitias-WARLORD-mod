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

            // Kural tabanlı karar alma
            CommandType cmdType = DetermineHeuristicCommand(tier, party);

            // Rütbeye göre menzil
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
                // Hedefsiz komutlar -> Eve dönüş / Saklanma
                comp.CurrentOrder = new StrategicCommand
                {
                    Type = cmdType,
                    Reason = $"HeuristicFallback"
                };
            }
            else
            {
                // Hedef bulunamadı veya aksiyon hedef gerektirmiyor -> Komutu temizle, vanilya devriye yapsın
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
                CareerTier.Eskiya => CommandType.Hunt,
                CareerTier.Rebel => CommandType.Raid,
                CareerTier.FamousBandit => CommandType.Ambush,
                CareerTier.Warlord => CommandType.CommandExtort,
                CareerTier.Taninmis => CommandType.Engage,
                CareerTier.Fatih => CommandType.Engage,
                _ => CommandType.Patrol
            };
        }

        /// <summary>
        /// HİBRİT AI: Bölgesel Çaresizlik Doktrini (Incubation).
        /// Eğer sığınağa bağlı toplam haydut gücü kritik eşiğin altındaysa,
        /// tüm birimlere geri çekilip kuluçkaya yatma (güç toplama) emri verilir.
        /// </summary>
        /// <summary>
        /// Tüm sığınaklara ait Çaresizlik Doktrini değerlendirmesini TEK bir döngüde yapar.
        /// MilitiaBehavior.OnHourlyTick içindeki "foreach hideout → EvaluateRegionalStrategy"
        /// kalıbının yerini alır: N hideout × ActiveMilitias.Where() yerine
        /// milis listesi bir kez bölümlenerek hideout başına gruplandırılır.
        /// </summary>
        public static void EvaluateAllRegionalStrategies()
        {
            var activeMilitias = ModuleManager.Instance.ActiveMilitias;
            if (activeMilitias.Count == 0) return;

            // Milis listesini hideout'a göre bir kerede gruplandır — O(M) tek geçiş
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

            // Her hideout için doktrin kararı — artık Where/ToList yok
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

        /// <summary>Tek sığınak için değerlendirme — geriye uyumluluk için korundu.</summary>
        public static void EvaluateRegionalStrategy(Settlement hideout)
        {
            if (hideout == null || !hideout.IsHideout) return;
            // Tekil çağrı hâlâ destekleniyor; toplu çağrı için EvaluateAllRegionalStrategies tercih edilmeli.
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
