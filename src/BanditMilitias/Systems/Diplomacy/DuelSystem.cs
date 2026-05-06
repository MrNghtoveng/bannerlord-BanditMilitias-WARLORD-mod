using BanditMilitias.Components;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Progression;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Diplomacy
{
    /// <summary>
    /// Oyuncu bir milisyayÄ± yendiÄŸinde warlord varsa dÃ¼ello teklif edilir.
    /// GerÃ§ek Ã¶dÃ¼l: Ã¼n, altÄ±n, warlord dÃ¼ÅŸÃ¼ÅŸÃ¼ veya fidye.
    /// </summary>
    public class DuelSystem : Core.Components.MilitiaModuleBase
    {
        public override string ModuleName => "DuelSystem";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 30;

        public static readonly DuelSystem Instance = new();
        private DuelSystem() { }

        private bool _initialized;

        public override void Initialize()
        {
            if (_initialized) return;
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, new Action<MapEvent>(OnMapEventEnded));
            _initialized = true;
        }

        public override void Cleanup()
        {
            CampaignEvents.MapEventEnded.ClearListeners(this);
            if (!_initialized) return;
            CampaignEvents.MapEventEnded.RemoveNonSerializedListener(this, new Action<MapEvent>(OnMapEventEnded));
            _initialized = false;
        }

        private void OnMapEventEnded(MapEvent ev)
        {
            if (ev == null || !ev.IsPlayerMapEvent) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;
            if (ev.WinningSide != ev.PlayerSide) return;

            // Yenilen taraftaki milisya var mÄ±?
            var loserSide = ev.PlayerSide == BattleSideEnum.Attacker
                ? ev.DefenderSide : ev.AttackerSide;

            foreach (var party in loserSide.Parties)
            {
                if (party.Party?.MobileParty?.PartyComponent is not MilitiaPartyComponent) continue;

                // EK-B FIX: GetByParty â†’ GetWarlordForParty (Intelligence.Strategic.WarlordSystem)
                var w = WarlordSystem.Instance.GetWarlordForParty(party.Party.MobileParty);
                if (w == null || !w.IsAlive) continue;

                int tier = (int)WarlordCareerSystem.Instance.GetTier(w.StringId);
                if (tier < 2) continue; // sadece Tier 2+ warlord iÃ§in dÃ¼ello

                TryOfferDuel(w, party.Party.MobileParty, tier);
                break; // tek dÃ¼ello yeterli
            }
        }

        // EK-B FIX: Warlord â†’ Warlord; tier parametre olarak geÃ§iliyor (Ã¶nbelleklendi)
        private static void TryOfferDuel(Warlord w, MobileParty militia, int tier)
        {
            if (Hero.MainHero == null) return;

            float playerScore = CalcScore(Hero.MainHero);
            // BattlesWon â†’ Kills (Intelligence.Strategic.Warlord'da Kills var, BattlesWon yok)
            float warlordScore = tier * 40f + w.Kills * 5f + MBRandom.RandomFloat * 30f;

            bool playerWins = playerScore * (0.8f + MBRandom.RandomFloat * 0.4f)
                            >= warlordScore * (0.8f + MBRandom.RandomFloat * 0.4f);

            // FullTitle â†’ FullName  (Intelligence.Strategic.Warlord'da FullName var)
            string bodyKey = playerWins
                ? $"DÃ¼ello kazanÄ±ldÄ±! {w.FullName} devrildi."
                : $"DÃ¼ello kaybedildi. {w.FullName} kaÃ§mayÄ± baÅŸardÄ±.";

            InformationManager.ShowInquiry(new InquiryData(
                playerWins ? "DÃ¼ello Zaferi!" : "DÃ¼ello Yenilgisi",
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
                // Ãœn + altÄ±n Ã¶dÃ¼lÃ¼
                float renown = (tier + 1) * 5f;
                int gold = (int)(w.Gold * 0.3f) + tier * 200;
                try
                {
                    Hero.MainHero.Clan.AddRenown(renown, false);
                    Hero.MainHero.ChangeHeroGold(gold);
                }
                catch { }

                // Warlord'u dÃ¼ÅŸÃ¼r
                if (w.LinkedHero?.IsAlive == true)
                {
                    try { KillCharacterAction.ApplyByMurder(w.LinkedHero, Hero.MainHero, true); }
                    catch { }
                }
                WarlordSystem.Instance.RemoveWarlord(w);

                InformationManager.DisplayMessage(new InformationMessage(
                    $"[DÃ¼ello] {w.FullName} yenildi! +{renown:F0} Ã¼n, +{gold} altÄ±n",
                    Colors.Green));

                FileLogger.Log($"[Duel] Oyuncu kazandÄ± vs {w.Name}, Tier={tier}");
            }
            else
            {
                // Warlord kaÃ§ar, kÃ¼Ã§Ã¼k ceza
                w.Gold += 500f; // gÃ¼cÃ¼nÃ¼ gÃ¶sterdi
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[DÃ¼ello] {w.FullName} kaÃ§tÄ±!", Colors.Yellow));
                FileLogger.Log($"[Duel] Oyuncu kaybetti vs {w.Name}");
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

        public static void StartDuel(Hero leader)
        {
            if (leader == null) return;
            // O(1) dictionary lookup — GetAllWarlords().FirstOrDefault LINQ taraması yerine
            var w = WarlordSystem.Instance.GetWarlordForHero(leader);
            if (w == null) return;
            int tier = (int)WarlordCareerSystem.Instance.GetTier(w.StringId);
            TryOfferDuel(w, null!, tier);
        }
    }
}
