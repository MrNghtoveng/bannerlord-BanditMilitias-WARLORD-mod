using BanditMilitias.Components;
using BanditMilitias.Intelligence.Strategic;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace BanditMilitias.Behaviors
{
        public class MilitiaDiplomacyCampaignBehavior : CampaignBehaviorBase
        {
            public override void RegisterEvents()
            {
                UnregisterEvents();
                CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
                CampaignEvents.MapEventStarted.AddNonSerializedListener(this, OnMapEventStarted);
            }

        private void OnMapEventStarted(MapEvent mapEvent, PartyBase attackerParty, PartyBase defenderParty)
        {
            if (Infrastructure.CompatibilityLayer.IsGameplayActivationDelayed()) return;

            if (Hero.MainHero?.IsPrisoner == true && Hero.MainHero.PartyBelongedToAsPrisoner != null)
            {
                var captor = Hero.MainHero.PartyBelongedToAsPrisoner;
                if (captor.IsMobile && captor.MobileParty.PartyComponent is MilitiaPartyComponent)
                {
                    if (mapEvent.InvolvedParties.Contains(captor))
                    {
                        try
                        {
                            EndCaptivityAction.ApplyByEscape(Hero.MainHero, null);
                            InformationManager.DisplayMessage(new InformationMessage(
                                new TextObject("{=BM_Escape_Battle}Kamp karışınca kargaşadan faydalanıp kaçmayı başardın!").ToString(),
                                Colors.Green));
                        }
                        catch (Exception ex)
                        {
                            BanditMilitias.Debug.DebugLogger.Error("MilitiaDiplomacy", $"Error releasing player during battle: {ex.Message}");
                        }
                    }
                }
            }

            // YENİ: Oyuncu bir Savaş Lordu ile (Tier 3+) savaşa girdiğinde taktiksel uyarı mesajı
            if (mapEvent != null && mapEvent.IsPlayerMapEvent)
            {
                var enemySide = mapEvent.PlayerSide == TaleWorlds.Core.BattleSideEnum.Attacker ? TaleWorlds.Core.BattleSideEnum.Defender : TaleWorlds.Core.BattleSideEnum.Attacker;

                PartyBase? enemyWarlordParty = null;
                Warlord? matchedWarlord = null;
                foreach (var partyTuple in mapEvent.GetMapEventSide(enemySide).Parties)
                {
                    if (partyTuple.Party?.LeaderHero != null)
                    {
                        // O(1) dictionary lookup — GetAllWarlords() LINQ taraması yerine
                        var wLord = WarlordSystem.Instance?.GetWarlord(partyTuple.Party.LeaderHero.StringId);
                        if (wLord != null)
                        {
                            enemyWarlordParty = partyTuple.Party;
                            matchedWarlord = wLord;
                            break;
                        }
                    }
                }

                if (enemyWarlordParty?.LeaderHero != null && matchedWarlord != null)
                {
                    var level = BanditMilitias.Systems.Progression.WarlordLegitimacySystem.Instance.GetLevel(matchedWarlord.StringId);
                    if (level >= BanditMilitias.Systems.Progression.LegitimacyLevel.Warlord)
                    {
                        string rankInfo = level switch
                        {
                            BanditMilitias.Systems.Progression.LegitimacyLevel.FamousBandit => "ÜNLÜ EŞKIYA",
                            BanditMilitias.Systems.Progression.LegitimacyLevel.Warlord => "SAVAŞ LORDU",
                            BanditMilitias.Systems.Progression.LegitimacyLevel.Recognized => "HÜKÜMDAR",
                            _ => "LORD"
                        };

                        InformationManager.DisplayMessage(new InformationMessage(
                            $"[Taktiksel İhtişam] {rankInfo} {matchedWarlord.Name}, orduna karşı özel olarak dizilmiş anti-birliklerini sahaya sürüyor!",
                            Colors.Red));
                    }
                }
            }
        }

        private void UnregisterEvents()
        {
            Infrastructure.MbEventExtensions.RemoveListenerSafe(CampaignEvents.OnSessionLaunchedEvent, this, (Action<CampaignGameStarter>)OnSessionLaunched);
            Infrastructure.MbEventExtensions.RemoveListenerSafe(CampaignEvents.MapEventStarted, this, (Action<MapEvent, PartyBase, PartyBase>)OnMapEventStarted);
        }

        public override void SyncData(IDataStore dataStore)
        {

        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            try
            {
                AddDialogs(starter);
            }
            catch (Exception ex)
            {
                BanditMilitias.Debug.DebugLogger.Error("MilitiaDiplomacy", $"OnSessionLaunched failed: {ex}");
                try
                {
                    BanditMilitias.Infrastructure.FileLogger.LogError($"MilitiaDiplomacy.OnSessionLaunched failed: {ex}");
                }
                catch
                {
                }
            }
        }

        private void AddDialogs(CampaignGameStarter starter)
        {

            _ = starter.AddDialogLine("militia_start_hostile", "start", "militia_intro",
                "{=BM_Diplo_Start}Stop right there! This is Warlord territory. Pay the toll or bleed.",
                () => IsMilitiaParty() && MobileParty.ConversationParty.MapFaction.IsAtWarWith(Hero.MainHero.MapFaction),
                null);

            _ = starter.AddDialogLine("militia_start_neutral", "start", "militia_intro",
                "{=BM_Diplo_Start_Neutral}What do you want, traveler? We are on patrol.",
                () => IsMilitiaParty() && !MobileParty.ConversationParty.MapFaction.IsAtWarWith(Hero.MainHero.MapFaction),
                null);

            _ = starter.AddPlayerLine("militia_bribe_offer", "militia_intro", "militia_bribe_result",
                "{=BM_Diplo_Bribe}Here is {GOLD_COST} denars. Let us pass.",
                () =>
                {
                    int cost = CalculateBribeCost();
                    MBTextManager.SetTextVariable("GOLD_COST", cost);
                    return Hero.MainHero.Gold >= cost;
                },
                null);

            _ = starter.AddDialogLine("militia_bribe_accept", "militia_bribe_result", "close_window",
                "{=BM_Diplo_Bribe_Accept}Wise choice. You may go.",
                null,
                () =>
                {
                    int cost = CalculateBribeCost();
                    GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, null, cost, true);

                    PlayerEncounter.LeaveEncounter = true;

                });

            _ = starter.AddPlayerLine("militia_recruit_offer", "militia_intro", "militia_recruit_result",
                "{=BM_Diplo_Recruit}You look skilled. Join my army and you'll be rich.",
                () =>
                {
                    int roguery = Hero.MainHero.GetSkillValue(DefaultSkills.Roguery);
                    int leadership = Hero.MainHero.GetSkillValue(DefaultSkills.Leadership);
                    // Bannerlord 1.3.15+: Bridged via CompatibilityLayer (LimitedPartySize prioritized)
                    int limit = Infrastructure.CompatibilityLayer.GetPartyMemberSizeLimit(MobileParty.MainParty.Party);
                    int freeSpace = Math.Max(0, limit - MobileParty.MainParty.MemberRoster.TotalManCount);
                    return roguery >= 40 && leadership >= 35 && freeSpace >= 5;
                },
                null);

            _ = starter.AddDialogLine("militia_recruit_success", "militia_recruit_result", "close_window",
                "{=BM_Diplo_Recruit_Success}Better pay than the Warlord offered. We are with you!",
                () => MBRandom.RandomFloat < CalculateRecruitChance(),
                () =>
                {
                    RecruitParty();
                    PlayerEncounter.LeaveEncounter = true;
                });

            _ = starter.AddDialogLine("militia_recruit_fail", "militia_recruit_result", "close_window",
                "{=BM_Diplo_Recruit_Fail}We serve the Warlord! Die!",
                null,
                () => PlayerEncounter.LeaveEncounter = false);

            _ = starter.AddPlayerLine("militia_threaten", "militia_intro", "close_window",
                "{=BM_Diplo_Fight}Out of my way, scum!",
                null,
                () =>
                {
                    PlayerEncounter.LeaveEncounter = false;
                });

            _ = starter.AddPlayerLine("militia_duel_offer", "militia_intro", "militia_duel_result",
                "{=BM_Diplo_Duel}I challenge your leader to a duel! (1v1)",
                () =>
                {

                    var party = MobileParty.ConversationParty;
                    return party != null &&
                           party.LeaderHero != null &&
                           party.LeaderHero.IsAlive &&
                           Hero.MainHero.HitPoints > 30 &&
                           party.MapFaction != null &&
                           party.MapFaction.IsAtWarWith(Hero.MainHero.MapFaction);
                },
                null);

            _ = starter.AddDialogLine("militia_duel_accept", "militia_duel_result", "close_window",
               "{=BM_Diplo_Duel_Accept}Hah! You have guts. Let's dance!",
               null,
               () =>
               {

                   BanditMilitias.Systems.Diplomacy.DuelSystem.StartDuel(MobileParty.ConversationParty.LeaderHero);

               });

            _ = starter.AddPlayerLine("militia_intel", "militia_intro", "militia_intro",
                "{=BM_Diplo_Intel}Show me your strength!",
                () => IsMilitiaParty(),
                () =>
                {

                    var layer = new BanditMilitias.GUI.GauntletUI.MilitiaIntelLayer();
                    layer.Open(MobileParty.ConversationParty);
                });

            _ = starter.AddPlayerLine("militia_leave", "militia_intro", "close_window",
                "{=BM_Diplo_Leave}Never mind. I'll be going.",
                () => IsMilitiaParty() && !MobileParty.ConversationParty.MapFaction.IsAtWarWith(Hero.MainHero.MapFaction),
                () => PlayerEncounter.LeaveEncounter = true);

            AddVillageDialogs(starter);
        }

        private void AddVillageDialogs(CampaignGameStarter starter)
        {

            _ = starter.AddPlayerLine("player_extortion_demand", "hero_main_options", "notable_extortion_response",
               "{=BM_Extort}I am here to collect protection money. Pay up.",
               () =>
               {
                   return Settlement.CurrentSettlement != null &&
                          Settlement.CurrentSettlement.IsVillage &&
                          Hero.OneToOneConversationHero != null &&
                          Hero.OneToOneConversationHero.IsNotable &&
                          BanditMilitias.Systems.Diplomacy.ExtortionSystem.Instance.CanExtort(Settlement.CurrentSettlement);
               },
               null);

            _ = starter.AddDialogLine("notable_extortion_success", "notable_extortion_response", "close_window",
                "{=BM_Extort_Yes}We don't want trouble... take this and leave us be.",
                () =>
                {
                    return BanditMilitias.Systems.Diplomacy.ExtortionSystem.Instance.WillYield(Hero.MainHero, Settlement.CurrentSettlement);
                },
                () =>
                {
                    var sys = BanditMilitias.Systems.Diplomacy.ExtortionSystem.Instance;
                    int amount = sys.ExecuteExtortion(Hero.MainHero, Settlement.CurrentSettlement);

                    TextObject msg = new TextObject("{=BM_Extort_Paid}Villagers paid {GOLD} gold tribute.");
                    _ = msg.SetTextVariable("GOLD", amount);
                    InformationManager.DisplayMessage(new InformationMessage(msg.ToString(), Colors.Green));
                });

            _ = starter.AddDialogLine("notable_extortion_fail", "notable_extortion_response", "close_window",
                "{=BM_Extort_No}We have nothing for you, vulture! Get out!",
                () =>
                {
                    return !BanditMilitias.Systems.Diplomacy.ExtortionSystem.Instance.WillYield(Hero.MainHero, Settlement.CurrentSettlement);
                },
                () =>
                {
                    BanditMilitias.Systems.Diplomacy.ExtortionSystem.Instance.ExecuteRefusal(Hero.MainHero, Settlement.CurrentSettlement);
                    InformationManager.DisplayMessage(new InformationMessage(new TextObject("{=BM_Extort_Refused}Villagers refused to pay.").ToString(), Colors.Red));
                });
        }

        private bool IsMilitiaParty()
        {
            if (!Infrastructure.CompatibilityLayer.IsGameplayActivationSwitchClosed())
                return false;

            return MobileParty.ConversationParty != null &&
                   (MobileParty.ConversationParty.PartyComponent is MilitiaPartyComponent ||
                    MobileParty.ConversationParty.StringId.Contains("Bandit_Militia"));
        }

        private int CalculateBribeCost()
        {
            int count = MobileParty.ConversationParty?.MemberRoster.TotalManCount ?? 1;
            int costPerMan = Settings.Instance?.BribeCostPerMan ?? 50;

            // Roguery yeteneğine göre indirim (maks %35)
            int roguery = Hero.MainHero.GetSkillValue(DefaultSkills.Roguery);
            float rogueDiscount = 1f - Math.Min(0.35f, roguery / 1000f);

            // Prestij çarpanı: Warlord'un LegitimacyLevel'ı rüşvet maliyetini artırır
            float prestigeMultiplier = 1.0f;
            var conversationParty = MobileParty.ConversationParty;
            var warlord = conversationParty == null
                ? null
                : WarlordSystem.Instance?.GetWarlordForParty(conversationParty);
            if (warlord != null)
            {
                var level = BanditMilitias.Systems.Progression.WarlordLegitimacySystem.Instance
                                .GetLevel(warlord.StringId);
                prestigeMultiplier = level switch
                {
                    BanditMilitias.Systems.Progression.LegitimacyLevel.Outlaw => 1.0f,
                    BanditMilitias.Systems.Progression.LegitimacyLevel.Rebel => 1.3f,
                    BanditMilitias.Systems.Progression.LegitimacyLevel.FamousBandit => 1.6f,
                    BanditMilitias.Systems.Progression.LegitimacyLevel.Warlord => 2.0f,
                    BanditMilitias.Systems.Progression.LegitimacyLevel.Recognized => 2.8f,
                    _ => 1.0f
                };
            }

            int rawCost = (int)(count * costPerMan * rogueDiscount * prestigeMultiplier);
            return Math.Max(200, rawCost);
        }

        private float CalculateRecruitChance()
        {
            int roguery = Hero.MainHero.GetSkillValue(DefaultSkills.Roguery);
            int leadership = Hero.MainHero.GetSkillValue(DefaultSkills.Leadership);
            int militiaSize = MobileParty.ConversationParty?.MemberRoster.TotalManCount ?? 0;

            float chance = 0.05f + (roguery / 1000f) + (leadership / 1500f);

            if (militiaSize > 20)
            {
                chance -= Math.Min(0.25f, (militiaSize - 20) * 0.006f);
            }

            if (MobileParty.ConversationParty?.MapFaction?.IsAtWarWith(Hero.MainHero.MapFaction) == true)
            {
                chance += 0.04f;
            }

            return Math.Max(0.05f, Math.Min(chance, 0.45f));
        }

        private void RecruitParty()
        {
            var party = MobileParty.ConversationParty;
            if (party == null) return;

            // Bannerlord 1.3.15+: Bridged via CompatibilityLayer (LimitedPartySize prioritized)
            int limit = Infrastructure.CompatibilityLayer.GetPartyMemberSizeLimit(MobileParty.MainParty.Party);
            int freeSpace = Math.Max(0, limit - MobileParty.MainParty.MemberRoster.TotalManCount);
            if (freeSpace <= 0)
            {
                InformationManager.DisplayMessage(new InformationMessage("No room in your party to recruit militia.", Colors.Red));
                return;
            }

            int desired = Math.Max(3, (int)(party.MemberRoster.TotalManCount * 0.25f));
            int transferLimit = Math.Min(Math.Min(desired, 20), freeSpace);
            int transferred = 0;

            var troops = party.MemberRoster.GetTroopRoster()
                .Where(t => t.Character != null && !t.Character.IsHero)
                .OrderBy(t => t.Character.Level)
                .ToList();

            foreach (var troop in troops)
            {
                if (transferred >= transferLimit) break;
                int move = Math.Min(troop.Number, transferLimit - transferred);
                if (move <= 0) continue;

                _ = MobileParty.MainParty.MemberRoster.AddToCounts(troop.Character, move);
                _ = party.MemberRoster.AddToCounts(troop.Character, -move);
                transferred += move;
            }

            if (transferred <= 0)
            {
                InformationManager.DisplayMessage(new InformationMessage("Recruit attempt failed.", Colors.Red));
                return;
            }

            if (party.MemberRoster.TotalManCount <= 2)
            {
                DestroyPartyAction.Apply(null, party);
            }
            else
            {
                party.RecentEventsMorale -= 10f;
            }

            InformationManager.DisplayMessage(new InformationMessage($"{transferred} militia joined your party.", Colors.Green));
        }
    }
}
