using BanditMilitias.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Progression;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Diplomacy
{
    [BanditMilitias.Core.Components.ModuleDependency(
        typeof(BanditMilitias.Intelligence.Strategic.WarlordSystem),
        typeof(BanditMilitias.Systems.Progression.WarlordCareerSystem))]
    [BanditMilitias.Core.Components.AutoRegister(Priority = 30, IsCritical = false, IsSingleton = false)]
    public class DuelSystem : Core.Components.MilitiaModuleBase
    {
        public override string ModuleName => "DuelSystem";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 30;

        private static DuelSystem? _instance;
        public static DuelSystem? Instance => _instance;

        private bool _initialized;

        public override void Initialize()
        {
            if (_initialized) return;
            _instance = this;
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, new Action<MapEvent>(OnMapEventEnded));
            _initialized = true;
        }

        public override void Cleanup()
        {
            try { CampaignEvents.MapEventEnded?.ClearListeners(this); } catch { }
            if (!_initialized) return;
            try { CampaignEvents.MapEventEnded?.RemoveNonSerializedListener(this, new Action<MapEvent>(OnMapEventEnded)); } catch { }
            _initialized = false;
            _instance = null;
        }

        private void OnMapEventEnded(MapEvent ev)
        {
            if (ev == null || !ev.IsPlayerMapEvent) return;
            if (ModActivationManager.IsGameplayActivationDelayed()) return;
            if (ev.WinningSide != ev.PlayerSide) return;

            var loserSide = ev.PlayerSide == BattleSideEnum.Attacker
                ? ev.DefenderSide : ev.AttackerSide;

            foreach (var party in loserSide.Parties)
            {
                if (party.Party?.MobileParty?.PartyComponent is not MilitiaPartyComponent) continue;


                var w = WarlordSystem.Instance.GetWarlordForParty(party.Party.MobileParty);
                if (w == null || !w.IsAlive) continue;

                int tier = (int)WarlordCareerSystem.Instance.GetTier(w.StringId);
                if (tier < 2) continue;

                TryOfferDuel(w, party.Party.MobileParty, tier);
                break;

            }
        }

        private static void TryOfferDuel(Warlord w, MobileParty militia, int tier)
        {
            if (Hero.MainHero == null) return;

            float playerScore = CalcScore(Hero.MainHero);


            float warlordScore = tier * 40f + w.Kills * 5f + MBRandom.RandomFloat * 30f;

            bool playerWins = playerScore * (0.8f + MBRandom.RandomFloat * 0.4f)
                            >= warlordScore * (0.8f + MBRandom.RandomFloat * 0.4f);


            string bodyKey = playerWins
                ? $"Düello kazanıldı! {w.FullName} devrildi."
                : $"Düello kaybedildi. {w.FullName} kaçmayı başardı.";

            InformationManager.ShowInquiry(new InquiryData(
                playerWins ? "Düello Zaferi!" : "Düello Yenilgisi",
                bodyKey,
                true, false,
                "Tamam", null,
                () => ApplyDuelOutcome(w, militia, playerWins, tier),
                null));
        }

        private static void ApplyDuelOutcome(Warlord w, MobileParty militia, bool playerWins, int tier)
        {
            if (playerWins)
            {


                float renown = (tier + 1) * 5f;
                int gold = (int)(w.Gold * 0.3f) + tier * 200;
                try
                {
                    Hero.MainHero.Clan.AddRenown(renown, false);
                    Hero.MainHero.ChangeHeroGold(gold);
                }
                catch (Exception ex)
                {
                    Infrastructure.FileLogger.LogWarning($"Duel reward application failed: {ex.Message}");
                }


                if (w.LinkedHero?.IsAlive == true)
                {
                    try { KillCharacterAction.ApplyByMurder(w.LinkedHero, Hero.MainHero, true); }
                    catch (Exception ex)
                    {
                        Infrastructure.FileLogger.LogWarning($"Duel executioner action failed: {ex.Message}");
                    }
                }
                WarlordSystem.Instance.RemoveWarlord(w);

                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Düello] {w.FullName} yenildi! +{renown:F0} ün, +{gold} altın",
                    Colors.Green));

                FileLogger.Log($"[Duel] Oyuncu kazandı vs {w.Name}, Tier={tier}");
            }
            else
            {


                w.Gold += 500f;
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[DÜELLO] {w.FullName} düelloyu kazandı ve seni esir aldı!", Colors.Red));
                FileLogger.Log($"[Duel] Oyuncu kaybetti vs {w.Name}");

                if (militia != null && militia.Party != null)
                {
                    try
                    {
                        if (TaleWorlds.CampaignSystem.Encounters.PlayerEncounter.Current != null)
                        {
                            TaleWorlds.CampaignSystem.Encounters.PlayerEncounter.LeaveEncounter = true;
                        }
                        TakePrisonerAction.Apply(militia.Party, Hero.MainHero);
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Error("DuelSystem", $"Failed to take player prisoner: {ex.Message}");
                    }
                }
            }
        }

        private static float CalcScore(Hero h)
        {
            if (h == null) return 0f;
            return h.HitPoints
                 + h.GetSkillValue(DefaultSkills.OneHanded)
                 + h.GetSkillValue(DefaultSkills.TwoHanded) * 0.7f
                 + h.Level * 5f;
        }

        public static void StartDuel(MobileParty militiaParty)
        {
            if (militiaParty == null || militiaParty.LeaderHero == null) return;


            var w = WarlordSystem.Instance.GetWarlordForHero(militiaParty.LeaderHero);
            if (w == null) return;
            int tier = (int)WarlordCareerSystem.Instance.GetTier(w.StringId);
            TryOfferDuel(w, militiaParty, tier);
        }
    }
}


