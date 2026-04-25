using BanditMilitias.Behaviors;
using BanditMilitias.Components;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.AI;
using BanditMilitias.Systems.Bounty;
using BanditMilitias.Systems.Crisis;
using BanditMilitias.Systems.Diplomacy;
using BanditMilitias.Systems.Economy;
using BanditMilitias.Systems.Workshop;
using BanditMilitias.Systems.Fear;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Systems.Raiding;
using BanditMilitias.Systems.Tracking;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Infrastructure
{

    public class BanditMilitiasSaveDefiner : SaveableTypeDefiner
    {
        public const int SAVE_VERSION = 4;

        public BanditMilitiasSaveDefiner() : base(850_058) { }

        protected override void DefineClassTypes()
        {

            AddClassDefinition(typeof(MilitiaPartyComponent), 1);
            AddClassDefinition(typeof(HideoutReputation), 2);
            AddClassDefinition(typeof(Warlord), 3);
            AddClassDefinition(typeof(SettlementFearState), 5);
            AddClassDefinition(typeof(WarlordLegitimacyRecord), 6);
            AddClassDefinition(typeof(WarlordBountyRecord), 7);
            AddClassDefinition(typeof(BlackMarketRecord), 8);
            AddClassDefinition(typeof(PropagandaRecord), 9);
            AddClassDefinition(typeof(StrategicCommand), 10);
            AddClassDefinition(typeof(VillageRaidTracker), 11);
            AddClassDefinition(typeof(WarlordPoliticalProfile), 12);
            AddClassDefinition(typeof(WarlordPoliticalRelation), 13);
            AddClassDefinition(typeof(AdaptiveDoctrineProfile), 14);

            // Career and Legacy additions
            AddClassDefinition(typeof(BanditMilitias.Systems.Progression.CareerRecord), 25);
            AddClassDefinition(typeof(BanditMilitias.Systems.Legacy.WarlordLegacyRecord), 26);
            AddClassDefinition(typeof(CrisisEvent), 27);
            AddClassDefinition(typeof(BanditMilitias.Systems.Progression.AscensionRecord), 28);

            AddStructDefinition(typeof(CombatOutcome), 15);
            AddClassDefinition(typeof(MilitiaPerformance), 16);
            AddStructDefinition(typeof(RouteCell), 17);
            AddClassDefinition(typeof(Intelligence.Strategic.BattleSite), 18);
            AddClassDefinition(typeof(ThreatAssessment), 19);
            AddClassDefinition(typeof(PlayerProfile), 20);
            AddClassDefinition(typeof(PlayerBehaviorModel), 21);
            AddClassDefinition(typeof(RunningAverage), 30);
            AddClassDefinition(typeof(CommandFeedback), 40);
            AddClassDefinition(typeof(StrategicAssessment), 50);
            AddClassDefinition(typeof(StrategicState), 60);
            AddClassDefinition(typeof(TradeEvent), 70);
            AddClassDefinition(typeof(WarEvent), 71);

            AddClassDefinition(typeof(BanditMilitias.Intelligence.Swarm.SwarmGroup), 82);
            AddClassDefinition(typeof(BanditMilitias.Intelligence.Swarm.SwarmOrder), 83);

            // ── v5.1 Yeni Sistemler ────────────────────────────────────
            AddClassDefinition(typeof(WarlordWorkshop), 90);

            // ── v5.5 Hafıza Sistemi (Plan 6e83e5ce) ─────────────────────
            // Memory Data systems removed for V5 WorldMemory
        }

        protected override void DefineContainerDefinitions()
        {

            ConstructContainerDefinition(typeof(List<MilitiaPartyComponent>));
            ConstructContainerDefinition(typeof(List<Warlord>));
            ConstructContainerDefinition(typeof(List<MobileParty>));
            ConstructContainerDefinition(typeof(List<CombatOutcome>));
            ConstructContainerDefinition(typeof(List<ThreatAssessment>));
            ConstructContainerDefinition(typeof(List<TradeEvent>));
            ConstructContainerDefinition(typeof(List<WarEvent>));
            ConstructContainerDefinition(typeof(List<CrisisEvent>));
            ConstructContainerDefinition(typeof(List<BanditMilitias.Intelligence.Swarm.SwarmGroup>));
            ConstructContainerDefinition(typeof(List<Intelligence.Strategic.BattleSite>));
            ConstructContainerDefinition(typeof(List<Settlement>));

            ConstructContainerDefinition(typeof(long[]));
            ConstructContainerDefinition(typeof(RouteCell[]));

            ConstructContainerDefinition(typeof(Dictionary<string, HideoutReputation>));
            ConstructContainerDefinition(typeof(Dictionary<string, CampaignTime>));
            ConstructContainerDefinition(typeof(Dictionary<string, MilitiaPerformance>));
            ConstructContainerDefinition(typeof(Dictionary<string, Warlord>));
            ConstructContainerDefinition(typeof(Dictionary<string, float>));
            ConstructContainerDefinition(typeof(Dictionary<string, CommandFeedback>));
            ConstructContainerDefinition(typeof(Dictionary<string, StrategicState>));
            ConstructContainerDefinition(typeof(Dictionary<string, SettlementFearState>));
            ConstructContainerDefinition(typeof(Dictionary<string, WarlordLegitimacyRecord>));
            ConstructContainerDefinition(typeof(Dictionary<string, WarlordBountyRecord>));
            ConstructContainerDefinition(typeof(Dictionary<string, BlackMarketRecord>));
            ConstructContainerDefinition(typeof(Dictionary<string, PropagandaRecord>));
            ConstructContainerDefinition(typeof(Dictionary<string, WarlordPoliticalProfile>));
            ConstructContainerDefinition(typeof(Dictionary<string, WarlordPoliticalRelation>));
            ConstructContainerDefinition(typeof(Dictionary<string, AdaptiveDoctrineProfile>));
            ConstructContainerDefinition(typeof(Dictionary<string, BanditMilitias.Systems.Progression.CareerRecord>));
            ConstructContainerDefinition(typeof(Dictionary<string, BanditMilitias.Systems.Progression.AscensionRecord>));
            ConstructContainerDefinition(typeof(Dictionary<string, BanditMilitias.Systems.Legacy.WarlordLegacyRecord>));
            ConstructContainerDefinition(typeof(Dictionary<string, List<string>>));
            ConstructContainerDefinition(typeof(Dictionary<string, int>));
            ConstructContainerDefinition(typeof(Dictionary<string, double>));

            ConstructContainerDefinition(typeof(Dictionary<CommandType, float>));
            ConstructContainerDefinition(typeof(Dictionary<CommandType, RunningAverage>));
            ConstructContainerDefinition(typeof(Dictionary<long, float>));
            ConstructContainerDefinition(typeof(Dictionary<BountySource, int>));

            ConstructContainerDefinition(typeof(List<string>));

            // ── v5.1 Yeni Sistemler ────────────────────────────────────
            ConstructContainerDefinition(typeof(List<WarlordWorkshop>));
            ConstructContainerDefinition(typeof(Dictionary<string, List<WarlordWorkshop>>));

            // ── v5.5 Hafıza Sistemi ────────────────────────────────────
        }

        protected override void DefineEnumTypes()
        {

            AddEnumDefinition(typeof(BackstoryType), 100);
            AddEnumDefinition(typeof(MotivationType), 101);
            AddEnumDefinition(typeof(PersonalityType), 102);

            AddEnumDefinition(typeof(CommandType), 110);
            AddEnumDefinition(typeof(StrategicPosture), 111);
            AddEnumDefinition(typeof(PlayStyle), 112);
            AddEnumDefinition(typeof(CommandCompletionStatus), 113);
            AddEnumDefinition(typeof(BanditMilitias.Systems.Progression.CareerTier), 114);

            AddEnumDefinition(typeof(WarEventType), 120);
            AddEnumDefinition(typeof(LegitimacyLevel), 121);
            AddEnumDefinition(typeof(BountySource), 122);
            AddEnumDefinition(typeof(PlayerCombatDoctrine), 130);
            AddEnumDefinition(typeof(CounterDoctrine), 131);
            AddEnumDefinition(typeof(CrisisType), 132);
            AddEnumDefinition(typeof(CrisisPhase), 133);

            AddEnumDefinition(typeof(BanditMilitias.Intelligence.Swarm.SwarmTactic), 180);
            AddEnumDefinition(typeof(BanditMilitias.Intelligence.Swarm.FormationType), 181);

            // ── v5.1 Yeni Sistemler ────────────────────────────────────
            AddEnumDefinition(typeof(BanditMilitias.Systems.Seasonal.MilitiaSeason), 190);
            AddEnumDefinition(typeof(BanditMilitias.Systems.Workshop.WorkshopType), 191);
        }
    }
}
