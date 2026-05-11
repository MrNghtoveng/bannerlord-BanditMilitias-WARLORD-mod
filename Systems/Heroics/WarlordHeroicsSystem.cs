using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Systems.WarlordLegitimacy;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Systems.Heroics
{
    public class HeroicsFeat
    {
        [SaveableProperty(1)] public string WarlordId { get; set; } = "";
        [SaveableProperty(2)] public string Description { get; set; } = "";
        [SaveableProperty(3)] public CampaignTime Date { get; set; } = CampaignTime.Zero;
        [SaveableProperty(4)] public int RenownGain { get; set; } = 0;
    }

    [BanditMilitias.Core.Components.ModuleDependency(
        typeof(BanditMilitias.Systems.Progression.WarlordCareerSystem),
        typeof(BanditMilitias.Systems.WarlordLegitimacy.WarlordLegitimacySystem))]
    [BanditMilitias.Core.Components.AutoRegister(Priority = 85, IsCritical = false)]
    public class WarlordHeroicsSystem : MilitiaModuleBase
    {
        public override string ModuleName => "WarlordHeroicsSystem";
        public override bool IsEnabled => Settings.Instance?.EnableHeroicFeats ?? true;
        public override int Priority => 85;

        private static readonly Lazy<WarlordHeroicsSystem> _instance =
            new Lazy<WarlordHeroicsSystem>(() => new WarlordHeroicsSystem());
        public static WarlordHeroicsSystem Instance => _instance.Value;

        private Dictionary<string, List<HeroicsFeat>> _feats = new();

        private WarlordHeroicsSystem() { }

        public override void Initialize()
        {
            BanditMilitias.Core.Events.EventBus.Instance.Subscribe<MilitiaBattleResultEvent>(OnBattleResult);
        }

        public override void Cleanup()
        {
            BanditMilitias.Core.Events.EventBus.Instance.Unsubscribe<MilitiaBattleResultEvent>(OnBattleResult);
            _feats.Clear();
        }

        public override void SyncData(IDataStore dataStore)
        {
            _ = dataStore.SyncData("HeroicsFeats_v1", ref _feats);
            if (dataStore.IsLoading && _feats == null)
            {
                _feats = new Dictionary<string, List<HeroicsFeat>>();
            }
        }

        private void OnBattleResult(MilitiaBattleResultEvent evt)
        {
            if (!IsEnabled || evt.Warlord == null || !evt.IsVictory) return;


            var enemyLords = evt.EnemyParties?.Where(p => p.LeaderHero != null && p.LeaderHero.IsLord).ToList();

            if (enemyLords != null && enemyLords.Count > 0)
            {


                float diffRatio = evt.EnemyTotalStrength / Math.Max(1f, evt.MilitiaTotalStrength);

                int renownBase = 10;
                if (diffRatio > 1.5f) renownBase = 20;

                if (diffRatio > 2.0f) renownBase = 30;


                RecordFeat(evt.Warlord, $"Defeated Lord(s) {string.Join(", ", enemyLords.Select(l => l.LeaderHero.Name))}", renownBase);


                if (WarlordCareerSystem.Instance.GetTier(evt.Warlord.StringId) >= CareerTier.Recognized)
                {
                    foreach (var lordParty in enemyLords)
                    {
                        if (lordParty.LeaderHero.IsAlive && !lordParty.LeaderHero.IsPrisoner && MBRandom.RandomFloat < 0.3f)
                        {


                            TakePrisonerAction.Apply(evt.Warlord.CommandedMilitias.FirstOrDefault()?.Party, lordParty.LeaderHero);
                            InformationManager.DisplayMessage(new InformationMessage(
                                $"[Heroics] {evt.Warlord.Name} captured {lordParty.LeaderHero.Name} in battle!", Colors.Red));
                            RecordFeat(evt.Warlord, $"Captured {lordParty.LeaderHero.Name}", 15);
                        }
                    }
                }
            }
        }

        private void RecordFeat(Warlord w, string desc, int renown)
        {
            if (!_feats.TryGetValue(w.StringId, out var list))
            {
                list = new List<HeroicsFeat>();
                _feats[w.StringId] = list;
            }

            list.Add(new HeroicsFeat { WarlordId = w.StringId, Description = desc, Date = CampaignTime.Now, RenownGain = renown });


            if (w.CommandedMilitias.Count > 0 && w.CommandedMilitias[0].PartyComponent is Components.MilitiaPartyComponent comp)
            {
                comp.Renown += renown;
            }

            if (ModuleAccess.TryGetEnabled<WarlordLegitimacySystem>(out var legit))
            {
                legit.ApplyPoints(w, renown * 2f, "Heroic Feat");
            }

            DebugLogger.Info("Heroics", $"[FEAT] {w.Name}: {desc} (+{renown} renown)");
        }

        public override string GetDiagnostics()
        {
            int totalFeats = _feats.Values.Sum(l => l.Count);
            int heroes = _feats.Count;
            return $"HeroicsSystem: {totalFeats} feats recorded across {heroes} warlords.";
        }
    }
}


