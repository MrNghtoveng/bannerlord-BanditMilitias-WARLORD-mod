using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Legacy;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Systems.Raiding;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BanditMilitias.Core.Events
{
    // ── CareerEvents.cs ──

    // â”€â”€ Kariyer Fatih Terfisi â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public class CareerFatihPromotionEvent : IPoolableEvent
    {
        public Warlord? Warlord { get; set; }

        public EventPriority Priority => EventPriority.Critical;
        public bool ShouldLog => true;
        public string GetDescription() => $"Conqueror Promotion: {Warlord?.Name}";

        public void Reset() { Warlord = null; }
    }

    // â”€â”€ Kariyer Tier DeÄŸiÅŸti â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public class CareerTierChangedEvent : IPoolableEvent
    {
        public Warlord? Warlord { get; set; }
        public CareerTier PreviousTier { get; set; }
        public CareerTier NewTier { get; set; }

        public EventPriority Priority => EventPriority.High;
        public bool ShouldLog => true;
        public string GetDescription() =>
            $"TierChange: {Warlord?.Name} {PreviousTier}â†’{NewTier}";

        public void Reset()
        {
            Warlord = null;
            PreviousTier = CareerTier.Eskiya;
            NewTier = CareerTier.Eskiya;
        }
    }

    // â”€â”€ Ä°ttifak Teklifi Edildi â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public class AllianceOfferEvent : IPoolableEvent
    {
        public Warlord? Warlord { get; set; }
        public string? KingdomId { get; set; }
        public int OfferCount { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => true;
        public string GetDescription() => $"AllianceOffer #{OfferCount}: {KingdomId} â†’ {Warlord?.Name}";

        public void Reset()
        {
            Warlord = null;
            KingdomId = null;
            OfferCount = 0;
        }
    }
    // ── IntegrationEvents.cs ──

    public class WarlordBetrayedEvent : IPoolableEvent
    {
        public Warlord? VictimWarlord { get; set; }
        public Settlement? BetrayingSettlement { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => BanditMilitias.Settings.Instance?.TestingMode == true;
        public string GetDescription() => $"Warlord {VictimWarlord?.Name} was betrayed by {BetrayingSettlement?.Name}!";

        public void Reset()
        {
            VictimWarlord = null;
            BetrayingSettlement = null;
        }
    }

    // Canonical: WarlordLevelChangedEvent — uses full Warlord object + LegitimacyLevel
    public class WarlordLevelChangedEvent : IPoolableEvent
    {
        public Warlord? Warlord { get; set; }
        public LegitimacyLevel OldLevel { get; set; }
        public LegitimacyLevel NewLevel { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => true;
        public string GetDescription() => $"Warlord {Warlord?.Name ?? "Unknown"} rank changed from {OldLevel} to {NewLevel}";

        public void Reset()
        {
            Warlord = null;
            OldLevel = LegitimacyLevel.Outlaw;
            NewLevel = LegitimacyLevel.Outlaw;
        }
    }

    public class WarlordBountyThresholdReachedEvent : IPoolableEvent
    {
        public Warlord? Warlord { get; set; }
        public int Threshold { get; set; }
        public int BountyAmount { get; set; }

        public EventPriority Priority => EventPriority.High;
        public bool ShouldLog => true;
        public string GetDescription() => $"Warlord {Warlord?.Name ?? "Unknown"} reached bounty threshold {Threshold}!";

        public void Reset()
        {
            Warlord = null;
            Threshold = 0;
            BountyAmount = 0;
        }
    }

    public class WarlordAllianceFormedEvent : IPoolableEvent
    {
        public Warlord? PrimaryWarlord { get; set; }
        public Warlord? SecondaryWarlord { get; set; }
        public float RelationScore { get; set; }
        public CampaignTime FormedAt { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => true;
        public string GetDescription() => $"Alliance formed: {PrimaryWarlord?.Name} <-> {SecondaryWarlord?.Name}";

        public void Reset()
        {
            PrimaryWarlord = null;
            SecondaryWarlord = null;
            RelationScore = 0f;
            FormedAt = CampaignTime.Zero;
        }
    }

    public class WarlordRivalryEscalatedEvent : IPoolableEvent
    {
        public Warlord? PrimaryWarlord { get; set; }
        public Warlord? SecondaryWarlord { get; set; }
        public float RelationScore { get; set; }
        public CampaignTime EscalatedAt { get; set; }

        public EventPriority Priority => EventPriority.High;
        public bool ShouldLog => true;
        public string GetDescription() => $"Rivalry escalated: {PrimaryWarlord?.Name} vs {SecondaryWarlord?.Name}";

        public void Reset()
        {
            PrimaryWarlord = null;
            SecondaryWarlord = null;
            RelationScore = 0f;
            EscalatedAt = CampaignTime.Zero;
        }
    }

    public class WarlordBackstabEvent : IPoolableEvent
    {
        public Warlord? Betrayer { get; set; }
        public Warlord? Betrayed { get; set; }
        public float RelationScoreAfter { get; set; }
        public CampaignTime HappenedAt { get; set; }

        public EventPriority Priority => EventPriority.Critical;
        public bool ShouldLog => true;
        public string GetDescription() => $"Backstab: {Betrayer?.Name} betrayed {Betrayed?.Name}";

        public void Reset()
        {
            Betrayer = null;
            Betrayed = null;
            RelationScoreAfter = 0f;
            HappenedAt = CampaignTime.Zero;
        }
    }

    public class AdaptiveDoctrineShiftedEvent : IPoolableEvent
    {
        public Warlord? Warlord { get; set; }
        public string OldDoctrine { get; set; } = string.Empty;
        public string NewDoctrine { get; set; } = string.Empty;
        public float Confidence { get; set; }
        public CampaignTime ChangedAt { get; set; }

        public EventPriority Priority => EventPriority.Low;
        public bool ShouldLog => true;
        public string GetDescription() => $"Doctrine shift: {Warlord?.Name} {OldDoctrine} -> {NewDoctrine}";

        public void Reset()
        {
            Warlord = null;
            OldDoctrine = string.Empty;
            NewDoctrine = string.Empty;
            Confidence = 0f;
            ChangedAt = CampaignTime.Zero;
        }
    }
    // ── MilitiaEvents.cs ──


    public class MilitiaSpawnedEvent : IPoolableEvent
    {
        public MobileParty? Party { get; set; }
        public Settlement? HomeHideout { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => false;
        public string GetDescription() => $"Militia Spawned for {HomeHideout?.Name}";

        public MilitiaSpawnedEvent() { }
        public MilitiaSpawnedEvent(MobileParty party, Settlement hideout)
        {
            Party = party;
            HomeHideout = hideout;
        }

        public void Reset()
        {
            Party = null;
            HomeHideout = null;
        }
    }

    public class MilitiaDisbandedEvent : IPoolableEvent
    {
        public MobileParty? Party { get; set; }
        public bool IsNuclearCleanup { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => false;
        public string GetDescription() => $"Militia Disbanded: {Party?.Name}";

        public MilitiaDisbandedEvent() { }
        public MilitiaDisbandedEvent(MobileParty party, bool forced = false)
        {
            Party = party;
            IsNuclearCleanup = forced;
        }

        public void Reset()
        {
            Party = null;
            IsNuclearCleanup = false;
        }
    }

    public class MilitiaMergeEvent : IPoolableEvent
    {
        public MobileParty? ResultingParty { get; set; }
        public List<MobileParty>? MergedParties { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => true;
        public string GetDescription() => $"Merged {MergedParties?.Count} parties into {ResultingParty?.Name}";

        public MilitiaMergeEvent() { }
        public MilitiaMergeEvent(MobileParty resultingParty, List<MobileParty> mergedParties)
        {
            ResultingParty = resultingParty;
            MergedParties = mergedParties;
        }

        public void Reset()
        {
            ResultingParty = null;
            MergedParties = null;
        }
    }

    public class MilitiaKilledEvent : IPoolableEvent
    {
        public MobileParty? Party { get; set; }
        public MobileParty? Victim { get => Party; set => Party = value; }
        public Hero? Killer { get; set; }
        public Settlement? HomeHideout { get; set; }
        public bool IsPlayerResponsible { get; set; }
        public bool WasPlayerKill => IsPlayerResponsible || (Killer != null && Killer == Hero.MainHero);

        public EventPriority Priority => WasPlayerKill ? EventPriority.High : EventPriority.Normal;
        public bool ShouldLog => WasPlayerKill;
        public string GetDescription() => $"Militia Killed: {Party?.Name}";

        public MilitiaKilledEvent() { }
        public MilitiaKilledEvent(MobileParty party, Settlement homeHideout, bool isPlayerResponsible = false)
        {
            Party = party;
            HomeHideout = homeHideout;
            IsPlayerResponsible = isPlayerResponsible;
        }

        public void Reset()
        {
            Party = null;
            Killer = null;
            HomeHideout = null;
            IsPlayerResponsible = false;
        }
    }

    public class HideoutClearedEvent : IPoolableEvent
    {
        public Settlement? Hideout { get; set; }
        public Hero? Clearer { get; set; }
        public int SurvivingMilitias { get; set; }

        public EventPriority Priority => EventPriority.Critical;
        public bool ShouldLog => true;
        public string GetDescription() => $"{Hideout?.Name} cleared by {Clearer?.Name}, {SurvivingMilitias} survivors";

        public void Reset()
        {
            Hideout = null;
            Clearer = null;
            SurvivingMilitias = 0;
        }
    }

    public class MilitiaRaidEvent : IPoolableEvent
    {
        public MobileParty? Raider { get; set; }
        public Settlement? Target { get; set; }
        public bool Success { get; set; }
        public int LootGained { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => false;
        public string GetDescription() => $"{Raider?.Name} raided {Target?.Name}, loot: {LootGained}";

        public void Reset()
        {
            Raider = null;
            Target = null;
            Success = false;
            LootGained = 0;
        }
    }

    public class PlayerEnteredTerritoryEvent : IGameEvent
    {
        public Settlement? NearbyHideout { get; set; }
        public float Distance { get; set; }
        public int NearbyMilitias { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => false;
        public string GetDescription() => $"Player near {NearbyHideout?.Name} ({NearbyMilitias} militias)";
    }

    public class AIDecisionEvent : IGameEvent
    {
        public MobileParty? Party { get; set; }
        public string? DecisionType { get; set; }
        public string? Reason { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => false;
        public string GetDescription() => $"{Party?.Name}: {DecisionType} ({Reason})";
    }
    // ── NewSystemEvents.cs ──

    // â”€â”€ Warlord Fallen (yeni â€” Legacy + Crisis sistemleri dinler) â”€â”€â”€â”€â”€â”€â”€â”€
    public class WarlordFallenEvent : IPoolableEvent
    {
        public Warlord? Warlord { get; set; }
        public float PeakFear { get; set; }
        public Dictionary<string, float>? WinningTactics { get; set; }
        public string? KilledBy { get; set; }  // "player" | "lord" | "rival_warlord"

        public EventPriority Priority => EventPriority.High;
        public bool ShouldLog => true;
        public string GetDescription() => $"Warlord Fallen: {Warlord?.Name} | KilledBy={KilledBy}";

        public void Reset()
        {
            Warlord = null;
            PeakFear = 0f;
            WinningTactics = null;
            KilledBy = null;
        }
    }

    // â”€â”€ KÃ¶y DireniÅŸ OlayÄ± â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public class VillageResistanceEvent : IPoolableEvent
    {
        public Settlement? Village { get; set; }
        public string? WarlordId { get; set; }
        public float FearDelta { get; set; } = -0.05f;

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => false;
        public string GetDescription() => $"{Village?.Name} direniÅŸ | Warlord={WarlordId}";

        public void Reset()
        {
            Village = null;
            WarlordId = null;
            FearDelta = -0.05f;
        }
    }

    // â”€â”€ Kriz BaÅŸladÄ± â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public class CrisisStartedEvent : IPoolableEvent
    {
        public string? WarlordId { get; set; }
        public Systems.Crisis.CrisisType CrisisType { get; set; }
        public float Intensity { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => true;
        public string GetDescription() => $"Kriz: {CrisisType} | Warlord={WarlordId}";

        public void Reset()
        {
            WarlordId = null;
            CrisisType = default;
            Intensity = 0f;
        }
    }

    // â”€â”€ Legacy Echo AktifleÅŸti â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public class LegacyEchoActivatedEvent : IGameEvent
    {
        public WarlordLegacyRecord? Record { get; set; }
        public Settlement? Hideout { get; set; }
        public float Echo { get; set; }

        public EventPriority Priority => EventPriority.Low;
        public bool ShouldLog => false;
        public string GetDescription() =>
            $"Echo: {Record?.WarlordName} @ {Hideout?.Name} ({Echo:F2})";
    }

    // â”€â”€ HaraÃ§ AlÄ±ndÄ± â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public class TributeCollectedEvent : IPoolableEvent
    {
        public string? WarlordId { get; set; }
        public Settlement? Village { get; set; }
        public int Amount { get; set; }

        public EventPriority Priority => EventPriority.Low;
        public bool ShouldLog => false;
        public string GetDescription() =>
            $"HaraÃ§: {Amount} altÄ±n | {Village?.Name} â†’ {WarlordId}";

        public void Reset()
        {
            WarlordId = null;
            Village = null;
            Amount = 0;
        }
    }

    // â”€â”€ Warlord Raid Completed â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public class MilitiaRaidCompletedEvent : IPoolableEvent
    {
        public MobileParty? RaiderParty { get; set; }
        public Settlement? TargetVillage { get; set; }
        public int GoldLooted { get; set; }
        public float ProsperityDamage { get; set; }
        public RaidOutcome Outcome { get; set; }
        public RaidIntensity Intensity { get; set; }

        public bool WasSuccessful =>
            Outcome == Systems.Raiding.RaidOutcome.Success ||
            Outcome == Systems.Raiding.RaidOutcome.PartialSuccess;

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => WasSuccessful;
        public string GetDescription() =>
            $"Raid {(WasSuccessful ? "OK" : "FAIL")}: {RaiderParty?.Name} â†’ {TargetVillage?.Name}";

        public void Reset()
        {
            RaiderParty = null;
            TargetVillage = null;
            GoldLooted = 0;
            ProsperityDamage = 0f;
            Outcome = Systems.Raiding.RaidOutcome.Aborted;
            Intensity = Systems.Raiding.RaidIntensity.Skirmish;
        }
    }
    // ── StrategicCommandEvent.cs ──

    public class StrategicCommandEvent : IPoolableEvent
    {
        public StrategicCommand? Command { get; set; }
        public string? IssuedBy { get; set; }
        public CampaignTime Timestamp { get; set; }
        public Settlement? TargetRegion { get; set; }
        public MobileParty? TargetParty { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => true;
        public string GetDescription() => $"Strategic Command: {Command?.Type} by {IssuedBy}";

        public void Reset()
        {
            Command = null;
            IssuedBy = null;
            TargetRegion = null;
            TargetParty = null;
            Timestamp = CampaignTime.Zero;
        }
    }
    // ── StrategicEvents.cs ──

    public class CommandCompletionEvent : IPoolableEvent
    {
        public MobileParty? Party { get; set; }
        public StrategicCommand? Command { get; set; }
        public CommandCompletionStatus Status { get; set; }
        public CampaignTime CompletionTime { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => false;
        public string GetDescription() => $"Command {Command?.Type} completed with status: {Status}";

        public void Reset()
        {
            Party = null;
            Command = null;
            Status = CommandCompletionStatus.Failure;
            CompletionTime = CampaignTime.Zero;
        }
    }

    public class StrategicAssessmentEvent : IPoolableEvent
    {
        public Warlord? TargetWarlord { get; set; }
        public StrategicAssessment? Assessment { get; set; }

        public EventPriority Priority => EventPriority.Low;
        public bool ShouldLog => false;
        public string GetDescription() => $"Strategic Assessment for {TargetWarlord?.Name}";

        public void Reset()
        {
            TargetWarlord = null;
            Assessment = null;
        }
    }

    public class ThreatLevelChangedEvent : IPoolableEvent
    {
        public float OldThreatLevel { get; set; }
        public float NewThreatLevel { get; set; }
        public float ThreatDelta { get; set; }
        public CampaignTime ChangeTime { get; set; }
        public string? Reason { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => true;
        public string GetDescription() => $"Threat Level Changed: {OldThreatLevel:F2} -> {NewThreatLevel:F2} ({Reason})";

        public void Reset()
        {
            OldThreatLevel = 0f;
            NewThreatLevel = 0f;
            ThreatDelta    = 0f;
            ChangeTime     = CampaignTime.Zero;
            Reason = string.Empty;
        }
    }

    // â”€â”€ Siege Preparation Ready â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public class SiegePreparationReadyEvent : IPoolableEvent
    {
        public Warlord? Warlord { get; set; }
        public int WeaponLevel { get; set; }

        public EventPriority Priority => EventPriority.High;
        public bool ShouldLog => true;
        public string GetDescription() => $"Siege Ready: {Warlord?.Name} (Level {WeaponLevel})";

        public void Reset()
        {
            Warlord = null;
            WeaponLevel = 0;
        }
    }
    // ── Militia Battle Result (AscensionEvaluator için) ──────────────────────────
    // ProcessVictory içinden fırlatılır; galip/mağlup savaş sonuçlarını taşır.
    public class MilitiaBattleResultEvent : IPoolableEvent
    {
        /// <summary>Galip milis partisi.</summary>
        public TaleWorlds.CampaignSystem.Party.MobileParty? WinnerParty { get; set; }

        /// <summary>Yenilen tarafı temsil eden parti.</summary>
        public TaleWorlds.CampaignSystem.Party.MobileParty? LoserParty { get; set; }

        /// <summary>Yenilen tarafta Lord partisi var mıydı?</summary>
        public bool LoserHadLordParty { get; set; }

        /// <summary>Yenilen tarafta Warlord milis partisi var mıydı?</summary>
        public bool LoserHadWarlordParty { get; set; }

        /// <summary>Galip tarafta Lord partisi var mıydı?</summary>
        public bool WinnerHadLordParty { get; set; }

        /// <summary>Galip tarafta Warlord milis partisi var mıydı?</summary>
        public bool WinnerHadWarlordParty { get; set; }

        /// <summary>Düşman güç / bizim güç oranı (underdog çarpanı için).</summary>
        public float EnemyStrengthRatio { get; set; } = 1f;

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => false;
        public string GetDescription() =>
            $"BattleResult: {WinnerParty?.Name} won | LordLoss={LoserHadLordParty} WarlordLoss={LoserHadWarlordParty}";

        public void Reset()
        {
            WinnerParty           = null;
            LoserParty            = null;
            LoserHadLordParty     = false;
            LoserHadWarlordParty  = false;
            WinnerHadLordParty    = false;
            WinnerHadWarlordParty = false;
            EnemyStrengthRatio    = 1f;
        }
    }

}