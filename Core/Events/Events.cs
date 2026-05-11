using System;
using System.Collections.Generic;
using BanditMilitias.Intelligence.Neural;
using BanditMilitias.Intelligence.Strategic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using BanditMilitias.Systems.WarlordLegitimacy;
using BanditMilitias.Systems.Progression;
using TaleWorlds.CampaignSystem.CharacterDevelopment;

namespace BanditMilitias.Core.Events
{
    public abstract class MilitiaEventBase : IGameEvent
    {
        public virtual EventPriority Priority => EventPriority.Normal;
        public virtual bool ShouldLog => true;
        public virtual string GetDescription() => GetType().Name;
    }

    public class MilitiaSpawnedEvent : MilitiaEventBase, IPoolableEvent
    {
        public MobileParty? Party { get; set; }
        public Settlement? HomeHideout { get; set; }
        public void Reset() { Party = null; HomeHideout = null; }
    }

    public class MilitiaKilledEvent : MilitiaEventBase, IPoolableEvent
    {
        public MobileParty? Victim { get; set; }
        public MobileParty? Party { get => Victim; set => Victim = value; }
        public MobileParty? Killer { get; set; }
        public Hero? KillerHero { get; set; }
        public Settlement? HomeHideout { get; set; }
        public bool WasPlayerKill { get; set; }
        public bool IsPlayerResponsible { get; set; }
        public void Reset()
        {
            Victim = null; Killer = null; KillerHero = null;
            HomeHideout = null; WasPlayerKill = false; IsPlayerResponsible = false;
        }
    }

    public class HideoutClearedEvent : MilitiaEventBase, IPoolableEvent
    {
        public Settlement? Hideout { get; set; }
        public PartyBase? Attacker { get; set; }
        public Hero? Clearer { get; set; }
        public int SurvivingMilitias { get; set; }
        public void Reset() { Hideout = null; Attacker = null; Clearer = null; SurvivingMilitias = 0; }
    }

    public class StrategicCommandEvent : MilitiaEventBase, IPoolableEvent
    {
        public StrategicCommand? Command { get; set; }
        public MobileParty? TargetParty { get; set; }
        public Settlement? TargetRegion { get; set; }
        public string IssuedBy { get; set; } = "";
        public CampaignTime Timestamp { get; set; }
        public void Reset() { Command = null; TargetParty = null; TargetRegion = null; IssuedBy = ""; Timestamp = CampaignTime.Zero; }
    }

    public class CommandCompletionEvent : MilitiaEventBase
    {
        public StrategicCommand? Command { get; set; }
        public CommandCompletionStatus Status { get; set; }
        public MobileParty? Party { get; set; }
        public CampaignTime CompletionTime { get; set; }
    }

    public class WarlordFallenEvent : MilitiaEventBase, IPoolableEvent
    {
        public Warlord? Warlord { get; set; }
        public string Reason { get; set; } = "";
        public float PeakFear { get; set; }
        public Dictionary<string, float>? WinningTactics { get; set; }
        public void Reset() { Warlord = null; Reason = ""; PeakFear = 0f; WinningTactics = null; }
    }

    public class CareerConquerorPromotionEvent : MilitiaEventBase, IPoolableEvent
    {
        public Warlord? Warlord { get; set; }
        public void Reset() { Warlord = null; }
    }

    public class MilitiaBattleResultEvent : MilitiaEventBase, IPoolableEvent
    {
        public MobileParty? WinnerParty { get; set; }
        public MobileParty? LoserParty { get; set; }
        public bool WinnerHadLordParty { get; set; }
        public bool LoserHadLordParty { get; set; }
        public bool WinnerHadWarlordParty { get; set; }
        public bool LoserHadWarlordParty { get; set; }
        public float EnemyStrengthRatio { get; set; }
        // Extended fields used by WarlordHeroicsSystem
        public Warlord? Warlord { get; set; }
        public bool IsVictory { get; set; }
        public List<MobileParty>? EnemyParties { get; set; }
        public float EnemyTotalStrength { get; set; }
        public float MilitiaTotalStrength { get; set; }
        public float WinnerRemainingStrength { get; set; }
        public float LoserRemainingStrength { get; set; }
        public void Reset()
        {
            WinnerParty = null; LoserParty = null; WinnerHadLordParty = false;
            LoserHadLordParty = false; WinnerHadWarlordParty = false; LoserHadWarlordParty = false;
            EnemyStrengthRatio = 0f; Warlord = null; IsVictory = false;
            EnemyParties = null; EnemyTotalStrength = 0f; MilitiaTotalStrength = 0f;
            WinnerRemainingStrength = 0f; LoserRemainingStrength = 0f;
        }
    }

    public class MilitiaMergeEvent : MilitiaEventBase, IPoolableEvent
    {
        public MobileParty? ResultingParty { get; set; }
        public List<MobileParty>? MergedParties { get; set; }
        public void Reset() { ResultingParty = null; MergedParties = null; }
    }

    public class ThreatLevelChangedEvent : MilitiaEventBase, IPoolableEvent, ISemanticEvent
    {
        public float OldThreatLevel { get; set; }
        public float NewThreatLevel { get; set; }
        public float ThreatDelta { get; set; }
        public CampaignTime ChangeTime { get; set; }
        public string Reason { get; set; } = "";
        public void Reset() { OldThreatLevel = 0f; NewThreatLevel = 0f; ThreatDelta    = 0f; ChangeTime     = CampaignTime.Zero; Reason = ""; }

        public float GetSignificanceDelta() => Math.Abs(ThreatDelta);
    }

    public class WarlordBetrayedEvent : MilitiaEventBase, IPoolableEvent
    {
        public Warlord? VictimWarlord { get; set; }
        public Settlement? BetrayingSettlement { get; set; }
        public void Reset() { VictimWarlord = null; BetrayingSettlement = null; }
    }

    public class ZombiePartyDetectedEvent : MilitiaEventBase, IPoolableEvent
    {
        public MobileParty? Party { get; set; }
        public Settlement? HomeSettlement { get; set; }
        public void Reset() { Party = null; HomeSettlement = null; }
    }

    public class MilitiaRaidCompletedEvent : MilitiaEventBase, IPoolableEvent
    {
        public MobileParty? RaiderParty { get; set; }
        public Settlement? TargetVillage { get; set; }
        public BanditMilitias.Systems.Raiding.RaidOutcome Outcome { get; set; }
        public int GoldLooted { get; set; }
        public BanditMilitias.Systems.Raiding.RaidIntensity Intensity { get; set; }
        public bool WasSuccessful { get; set; }
        public void Reset() { RaiderParty = null; TargetVillage = null; GoldLooted = 0; WasSuccessful = false; }
    }

    public class WarlordLevelChangedEvent : MilitiaEventBase, IPoolableEvent
    {
        public Warlord? Warlord { get; set; }
        public LegitimacyLevel OldLevel { get; set; }
        public LegitimacyLevel NewLevel { get; set; }
        public void Reset() { Warlord = null; }
    }

    public class WarlordAllianceFormedEvent : MilitiaEventBase, IPoolableEvent, ISemanticEvent
    {
        public Warlord? PrimaryWarlord { get; set; }
        public Warlord? SecondaryWarlord { get; set; }
        public float RelationScore { get; set; }
        public CampaignTime FormedAt { get; set; }
        public void Reset() { PrimaryWarlord = null; SecondaryWarlord = null; RelationScore = 0f; FormedAt = CampaignTime.Zero; }
        // İlişki skorunun büyüklüğü önem göstergesi
        public float GetSignificanceDelta() => Math.Abs(RelationScore);
    }

    public class AllianceOfferEvent : MilitiaEventBase, IPoolableEvent
    {
        public Warlord? Warlord { get; set; }
        public string KingdomId { get; set; } = "";
        public int OfferCount { get; set; }
        public void Reset() { Warlord = null; KingdomId = ""; OfferCount = 0; }
    }

    public class WarlordRivalryEscalatedEvent : MilitiaEventBase, IPoolableEvent, ISemanticEvent
    {
        public Warlord? PrimaryWarlord { get; set; }
        public Warlord? SecondaryWarlord { get; set; }
        public float Tension { get; set; }
        public float RelationScore { get; set; }
        public CampaignTime EscalatedAt { get; set; }
        public void Reset() { PrimaryWarlord = null; SecondaryWarlord = null; Tension = 0f; RelationScore = 0f; EscalatedAt = CampaignTime.Zero; }
        // Gerilim değişiminin büyüklüğünü significance olarak kullan
        public float GetSignificanceDelta() => Math.Abs(Tension);
    }

    public class WarlordBackstabEvent : MilitiaEventBase, IPoolableEvent
    {
        public Warlord? Betrayer { get; set; }
        public Warlord? Betrayed { get; set; }
        public float RelationScoreAfter { get; set; }
        public CampaignTime HappenedAt { get; set; }
        public void Reset() { Betrayer = null; Betrayed = null; RelationScoreAfter = 0f; HappenedAt = CampaignTime.Zero; }
    }

    public class WarlordBountyThresholdReachedEvent : MilitiaEventBase, IPoolableEvent
    {
        public Warlord? Warlord { get; set; }
        public int BountyAmount { get; set; }
        public int Threshold { get; set; }
        public void Reset() { Warlord = null; BountyAmount = 0; Threshold = 0; }
    }

    public class AdaptiveDoctrineShiftedEvent : MilitiaEventBase, IPoolableEvent, ISemanticEvent
    {
        public Warlord? Warlord { get; set; }
        public string OldDoctrine { get; set; } = "";
        public string NewDoctrine { get; set; } = "";
        public float Confidence { get; set; }
        public CampaignTime ChangedAt { get; set; }

        public void Reset()
        {
            Warlord = null;
            OldDoctrine = "";
            NewDoctrine = "";
            Confidence = 0f;
            ChangedAt = CampaignTime.Zero;
        }

        // Güven skoru significance olarak kullan — düşük confidence = önemsiz geçiş
        public float GetSignificanceDelta() => Math.Abs(Confidence);
    }

    public class CrisisStartedEvent : MilitiaEventBase, IPoolableEvent
    {
        public string WarlordId { get; set; } = "";
        public string CrisisType { get; set; } = "";
        public float Intensity { get; set; }
#pragma warning disable CS8625
        public void Reset() { WarlordId = ""; CrisisType = default; Intensity = 0f; }
#pragma warning restore CS8625
    }

    public class TributeCollectedEvent : MilitiaEventBase, IPoolableEvent
    {
        public string? WarlordId { get; set; }
        public Settlement? Village { get; set; }
        public int Amount { get; set; }
        public void Reset() { WarlordId = null; Village = null; Amount = 0; }
    }

    public class VillageResistanceEvent : MilitiaEventBase, IPoolableEvent
    {
        public Settlement? Village { get; set; }
        public string WarlordId { get; set; } = "";
        public void Reset() { Village = null; WarlordId = ""; }
    }

    public class LegacyEchoActivatedEvent : MilitiaEventBase
    {
        public BanditMilitias.Systems.Legacy.WarlordLegacyRecord? Record { get; set; }
        public float Echo { get; set; }
        public Settlement? Hideout { get; set; }
    }

    public class MilitiaRaidEvent : MilitiaEventBase, IPoolableEvent
    {
        public MobileParty? Party { get; set; }
        public MobileParty? Raider { get => Party; set => Party = value; }
        public Settlement? Target { get; set; }
        public bool Success { get; set; }
        public int LootGained { get; set; }
        public void Reset() { Party = null; Target = null; Success = false; LootGained = 0; }
    }

    public class SiegePreparationReadyEvent : MilitiaEventBase, IPoolableEvent
    {
        public Warlord? Warlord { get; set; }
        public int WeaponLevel { get; set; }
        public void Reset() { Warlord = null; WeaponLevel = 0; }
    }

    public class PlayerEnteredTerritoryEvent : MilitiaEventBase
    {
        public Settlement? Territory { get; set; }
        public Settlement? NearbyHideout { get => Territory; set => Territory = value; }
        public float Distance { get; set; }
        public List<MobileParty>? NearbyMilitias { get; set; }
    }

    public class MilitiaDisbandedEvent : MilitiaEventBase, IPoolableEvent
    {
        public MobileParty? Party { get; set; }
        public string Reason { get; set; } = "";
        public void Reset() { Party = null; Reason = ""; }
    }

    public class CareerTierChangedEvent : MilitiaEventBase, IPoolableEvent
    {
        public Warlord? Warlord { get; set; }
        public CareerTier OldTier { get; set; }
        public CareerTier NewTier { get; set; }
        public CareerTier PreviousTier { get => OldTier; set => OldTier = value; }
        public void Reset() { Warlord = null; OldTier = CareerTier.Outlaw; NewTier = CareerTier.Outlaw; }
    }

    public class StrategicAssessmentEvent : MilitiaEventBase, IPoolableEvent
    {
        public Warlord? Warlord { get; set; }
        public string AssessmentType { get; set; } = "";
        public void Reset() { Warlord = null; AssessmentType = ""; }
    }

    public class AIDecisionEvent : MilitiaEventBase, IPoolableEvent
    {
        public string DecisionType { get; set; } = "";
        public string Reason { get; set; } = "";
        public MobileParty? Party { get; set; }
        public void Reset() { DecisionType = ""; Reason = ""; Party = null; }
    }
}
