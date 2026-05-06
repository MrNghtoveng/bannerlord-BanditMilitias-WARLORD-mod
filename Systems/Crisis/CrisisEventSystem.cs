using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Core.Neural;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Systems.Crisis
{

    public enum CrisisType
    {
        Betrayal,
        Mutiny,
        PowerVacuum,
        SiegePanic,
        BountyCrisis
    }

    public enum CrisisPhase
    {
        Brewing,

        Active,

        Resolving,

        Resolved

    }

    [Serializable]
    public class CrisisEvent
    {
        [SaveableProperty(1)] public string CrisisId { get; set; } = Guid.NewGuid().ToString("N")[..8];
        [SaveableProperty(2)] public CrisisType Type { get; set; }
        [SaveableProperty(3)] public CrisisPhase Phase { get; set; } = CrisisPhase.Brewing;
        [SaveableProperty(4)] public string WarlordId { get; set; } = "";
        [SaveableProperty(5)] public CampaignTime StartTime { get; set; }
        [SaveableProperty(6)] public int DaysActive { get; set; }
        [SaveableProperty(7)] public float Intensity { get; set; } = 0.5f;


        [SaveableProperty(8)] public string? TraitorPartyId { get; set; }


        [SaveableProperty(9)] public List<string> Rivals { get; set; } = new();


        [SaveableProperty(10)] public string? Resolution { get; set; }
    }

    [BanditMilitias.Core.Components.AutoRegister(Priority = 450, IsCritical = false)]
    public class CrisisEventSystem : MilitiaModuleBase
    {
        public override string ModuleName => "CrisisEvents";
        public override bool IsEnabled => Settings.Instance?.EnableCrisisEvents ?? true;
        public override int Priority => 48;

        private static readonly Lazy<CrisisEventSystem> _inst =
            new(() => new CrisisEventSystem());
        public static CrisisEventSystem Instance => _inst.Value;

        private List<CrisisEvent> _activeCrises = new();


        private const int BETRAYAL_MIN_MILITIAS = CrisisRules.BETRAYAL_MIN_MILITIAS;
        private const float MUTINY_DAYS_NO_WIN = CrisisRules.MUTINY_NO_WIN_DAYS;
        private const float BOUNTY_CRISIS_THRESHOLD = CrisisRules.BOUNTY_CRISIS_THRESHOLD;
        private const float SIEGE_PANIC_ARMY_SIZE = CrisisRules.SIEGE_PANIC_ARMY_SIZE;
        private const int MAX_CONCURRENT_CRISES = CrisisRules.MAX_CONCURRENT_CRISES;

        private CrisisEventSystem() { }

        public override void Initialize()
        {
            BanditMilitias.Core.Events.EventBus.Instance.Subscribe<HideoutClearedEvent>(OnHideoutCleared);
        }


        private void FireCrisisStartedEvent(CrisisEvent crisis)
        {
            try
            {
                var evt = BanditMilitias.Core.Events.EventBus.Instance.Get<BanditMilitias.Core.Events.CrisisStartedEvent>();
                if (evt != null)
                {
                    evt.WarlordId = crisis.WarlordId;
                    evt.CrisisType = crisis.Type.ToString();
                    evt.Intensity = crisis.Intensity;
                    NeuralEventRouter.Instance.Publish(evt);
                    BanditMilitias.Core.Events.EventBus.Instance.Return(evt);
                }
            }
            catch { }
        }

        public override void Cleanup()
        {
            BanditMilitias.Core.Events.EventBus.Instance.Unsubscribe<HideoutClearedEvent>(OnHideoutCleared);
            _activeCrises.Clear();
        }


        public override void OnDailyTick()
        {
            if (!IsEnabled) return;

            EvaluateNewCrises();
            ProcessActiveCrises();
            PurgeResolved();
        }


        private void EvaluateNewCrises()
        {
            if (_activeCrises.Count >= MAX_CONCURRENT_CRISES) return;

            var warlords = WarlordSystem.Instance.GetAllWarlords();
            foreach (var w in warlords)
            {


                if (_activeCrises.Any(c =>
                    c.WarlordId == w.StringId && c.Phase != CrisisPhase.Resolved))
                    continue;

                TryTriggerBetrayal(w);
                TryTriggerMutiny(w);
                TryTriggerBountyCrisis(w);
                TryTriggerSiegePanic(w);
            }
        }

        private void TryTriggerBetrayal(Warlord w)
        {
            int activeCrises = _activeCrises.Count(c => c.WarlordId == w.StringId && c.Phase != CrisisPhase.Resolved);
            if (!CrisisRules.CanBetrayal(w.CommandedMilitias.Count, w.Gold, activeCrises)) return;

            float chance = CrisisRules.CalcBetrayalChance(w.DaysActive);
            if (MBRandom.RandomFloat > chance) return;


            var traitor = w.CommandedMilitias
                .Where(m => m?.IsActive == true)
                .OrderBy(m => m.MemberRoster.TotalManCount)
                .FirstOrDefault();

            if (traitor == null) return;

            var crisis = new CrisisEvent
            {
                Type = CrisisType.Betrayal,
                WarlordId = w.StringId,
                Phase = CrisisPhase.Active,
                StartTime = CampaignTime.Now,
                Intensity = 0.6f + MBRandom.RandomFloat * 0.4f,
                TraitorPartyId = traitor.StringId
            };
            _activeCrises.Add(crisis);
            FireCrisisStartedEvent(crisis);
            ApplyBetrayalEffect(w, traitor, crisis);

            DebugLogger.Info("CrisisEvents",
                $"[BETRAYAL] {traitor.Name} -> separated from {w.Name}!");

            InformationManager.DisplayMessage(new InformationMessage(
                $"[Crisis] {traitor.Name} {w.Name} left — uncontrolled power!", Colors.Red));
        }

        private void TryTriggerMutiny(Warlord w)
        {
            if (w.CommandedMilitias.Count < 2) return;


            bool recentVictory = w.CommandedMilitias
                .Any(m => m?.PartyComponent is MilitiaPartyComponent c &&
                          (CampaignTime.Now - c.LastBattleTime).ToDays < MUTINY_DAYS_NO_WIN);

            if (recentVictory) return;

            float chance = 0.02f;
            if (MBRandom.RandomFloat > chance) return;

            var crisis = new CrisisEvent
            {
                Type = CrisisType.Mutiny,
                WarlordId = w.StringId,
                Phase = CrisisPhase.Active,
                StartTime = CampaignTime.Now,
                Intensity = 0.4f + MBRandom.RandomFloat * 0.3f
            };
            _activeCrises.Add(crisis);
            FireCrisisStartedEvent(crisis);
            ApplyMutinyEffect(w, crisis);

            DebugLogger.Info("CrisisEvents", $"[MUTINY] Mutiny in {w.Name}'s army!");
        }

        private void TryTriggerBountyCrisis(Warlord w)
        {
            float bounty = Systems.Bounty.BountySystem.Instance?.IsEnabled == true
                ? Systems.Bounty.BountySystem.Instance.GetBounty(w.StringId)
                : 0f;

            int activeCrises = _activeCrises.Count(c => c.WarlordId == w.StringId && c.Phase != CrisisPhase.Resolved);
            if (!CrisisRules.CanBountyCrisis(bounty, activeCrises)) return;

            float chance = CrisisRules.CalcBountyCrisisChance(bounty);
            if (MBRandom.RandomFloat > chance) return;

            var crisis = new CrisisEvent
            {
                Type = CrisisType.BountyCrisis,
                WarlordId = w.StringId,
                Phase = CrisisPhase.Active,
                StartTime = CampaignTime.Now,
                Intensity = MathF.Min(1f, bounty / 30_000f)
            };
            _activeCrises.Add(crisis);
            FireCrisisStartedEvent(crisis);


            w.IssueOrderToMilitias(new StrategicCommand
            {
                Type = CommandType.CommandLayLow,
                Priority = 0.95f,
                Reason = "BountyCrisis: bounty too high"
            });

            DebugLogger.Info("CrisisEvents",
                $"[BOUNTY CRISIS] {w.Name} — Bounty={bounty:F0}");
        }

        private void TryTriggerSiegePanic(Warlord w)
        {
            if (w.AssignedHideout == null) return;

            var hideoutPos = CompatibilityLayer.GetSettlementPosition(w.AssignedHideout);
            if (!hideoutPos.IsValid) return;


            var nearbyParties = new System.Collections.Generic.List<TaleWorlds.CampaignSystem.Party.MobileParty>();
            BanditMilitias.Systems.Grid.SpatialGridSystem.Instance.QueryNearby(hideoutPos, 25f, nearbyParties);
            bool bigArmyNear = nearbyParties.Any(p => p?.IsActive == true &&
                          p.IsLordParty &&
                          p.MemberRoster.TotalManCount >= SIEGE_PANIC_ARMY_SIZE);

            if (!bigArmyNear) return;
            if (MBRandom.RandomFloat > 0.15f) return;

            var crisis = new CrisisEvent
            {
                Type = CrisisType.SiegePanic,
                WarlordId = w.StringId,
                Phase = CrisisPhase.Active,
                StartTime = CampaignTime.Now,
                Intensity = 0.8f
            };
            _activeCrises.Add(crisis);
            FireCrisisStartedEvent(crisis);


            w.IssueOrderToMilitias(new StrategicCommand
            {
                Type = CommandType.Retreat,
                Priority = 1f,
                Reason = "SiegePanic: large lord army nearby"
            });

            InformationManager.DisplayMessage(new InformationMessage(
                $"[Panic] {w.Name}'s forces are dispersing — large army coming!",
                Colors.Red));
        }


        private void ProcessActiveCrises()
        {
            foreach (var crisis in _activeCrises.Where(c => c.Phase == CrisisPhase.Active))
            {
                crisis.DaysActive++;

                switch (crisis.Type)
                {
                    case CrisisType.Betrayal:
                        ProcessBetrayal(crisis); break;
                    case CrisisType.Mutiny:
                        ProcessMutiny(crisis); break;
                    case CrisisType.BountyCrisis:
                        ProcessBountyCrisis(crisis); break;
                    case CrisisType.SiegePanic:
                        ProcessSiegePanic(crisis); break;
                }
            }
        }

        private void ProcessBetrayal(CrisisEvent crisis)
        {
            if (!CrisisRules.IsCrisisExpired(nameof(CrisisType.Betrayal), crisis.DaysActive)) return;

            var w = WarlordSystem.Instance.GetWarlord(crisis.WarlordId);
            bool returned = w != null && w.Gold > 1000f && MBRandom.RandomFloat < 0.4f;

            crisis.Phase = CrisisPhase.Resolved;
            crisis.Resolution = returned ? "Traitor returned" : "Traitor dispersed";

            DebugLogger.Info("CrisisEvents",
                $"[BETRAYAL RESOLVED] {w?.Name} | {crisis.Resolution}");
        }

        private void ProcessMutiny(CrisisEvent crisis)
        {
            if (!CrisisRules.IsCrisisExpired(nameof(CrisisType.Mutiny), crisis.DaysActive)) return;

            var w = WarlordSystem.Instance.GetWarlord(crisis.WarlordId);
            if (w != null)
            {


                foreach (var m in w.CommandedMilitias.Where(m => m?.IsActive == true))
                    m.Aggressiveness = MathF.Max(0.3f, m.Aggressiveness - 0.2f);
            }
            crisis.Phase = CrisisPhase.Resolved;
            crisis.Resolution = "Mutiny suppressed";
        }

        private void ProcessBountyCrisis(CrisisEvent crisis)
        {
            if (!CrisisRules.IsCrisisExpired(nameof(CrisisType.BountyCrisis), crisis.DaysActive)) return;
            crisis.Phase = CrisisPhase.Resolved;
            crisis.Resolution = "Lay-low completed";
        }

        private void ProcessSiegePanic(CrisisEvent crisis)
        {
            if (!CrisisRules.IsCrisisExpired(nameof(CrisisType.SiegePanic), crisis.DaysActive)) return;
            crisis.Phase = CrisisPhase.Resolved;
            crisis.Resolution = "Army dispersed";
        }


        private static void ApplyBetrayalEffect(Warlord w, MobileParty traitor, CrisisEvent crisis)
        {


            w.ReleaseMilitia(traitor);


            traitor.Aggressiveness = MathF.Min(1f, traitor.Aggressiveness + 0.4f);
        }

        private static void ApplyMutinyEffect(Warlord w, CrisisEvent crisis)
        {
            foreach (var m in w.CommandedMilitias.Where(m => m?.IsActive == true))
            {


                m.Aggressiveness *= (1f - crisis.Intensity * 0.4f);
            }
        }

        private void OnHideoutCleared(HideoutClearedEvent evt)
        {
            if (evt == null || evt.Hideout == null) return;


            var w = WarlordSystem.Instance.GetWarlordForHideout(evt.Hideout);
            if (w == null) return;

            foreach (var c in _activeCrises.Where(c =>
                c.WarlordId == w.StringId && c.Phase != CrisisPhase.Resolved))
            {
                c.Phase = CrisisPhase.Resolved;
                c.Resolution = "Warlord fallen";
            }
        }

        private void PurgeResolved()
        {
            _ = _activeCrises.RemoveAll(c => c.Phase == CrisisPhase.Resolved);
        }

        public IReadOnlyList<CrisisEvent> GetActiveCrises() => _activeCrises;

        public override void SyncData(IDataStore ds)
        {
            _ = ds.SyncData("_crisisEvents_v1", ref _activeCrises);
            if (ds.IsLoading) _activeCrises ??= new();
        }

        public override string GetDiagnostics() =>
            $"CrisisEvents: {_activeCrises.Count} active | " +
            $"Betrayal={_activeCrises.Count(c => c.Type == CrisisType.Betrayal)} " +
            $"Mutiny={_activeCrises.Count(c => c.Type == CrisisType.Mutiny)}";
    }


    public static class CrisisRules
    {


        public const int BETRAYAL_MIN_MILITIAS = 3;
        public const float BETRAYAL_BASE_CHANCE = 0.03f;
        public const float BETRAYAL_DAYS_SCALE = 60f;

        public const float MUTINY_NO_WIN_DAYS = 21f;
        public const float MUTINY_BASE_CHANCE = 0.02f;

        public const float BOUNTY_CRISIS_THRESHOLD = 15_000f;
        public const float BOUNTY_MAX_SCALE = 50_000f;
        public const float BOUNTY_CHANCE_MULTIPLIER = 0.05f;

        public const float SIEGE_PANIC_ARMY_SIZE = 300f;
        public const float SIEGE_PANIC_CHANCE = 0.15f;

        public const int MAX_CONCURRENT_CRISES = 6;


        public static float CalcBetrayalChance(int daysActive)
        {
            float t = Math.Max(0, daysActive) / BETRAYAL_DAYS_SCALE;
            return BETRAYAL_BASE_CHANCE * (1f + t);
        }


        public static bool CanBetrayal(int militiaCount, float gold, int activeWarlordCrises)
            => militiaCount >= BETRAYAL_MIN_MILITIAS
            && gold <= 0f
            && activeWarlordCrises < MAX_CONCURRENT_CRISES;


        public static bool CanMutiny(int militiaCount, float daysSinceLastBattle, int activeCrises)
            => militiaCount >= 2
            && daysSinceLastBattle >= MUTINY_NO_WIN_DAYS
            && activeCrises < MAX_CONCURRENT_CRISES;


        public static float CalcBountyCrisisChance(float bounty)
        {
            if (bounty < BOUNTY_CRISIS_THRESHOLD) return 0f;
            float excess = bounty - BOUNTY_CRISIS_THRESHOLD;
            return Math.Min(1f, (excess / BOUNTY_MAX_SCALE) * BOUNTY_CHANCE_MULTIPLIER);
        }


        public static bool CanBountyCrisis(float bounty, int activeCrises)
            => bounty >= BOUNTY_CRISIS_THRESHOLD
            && activeCrises < MAX_CONCURRENT_CRISES;


        public static bool IsArmySizeDangerous(int armySize)
            => armySize >= SIEGE_PANIC_ARMY_SIZE;


        public static bool IsCrisisExpired(string crisisType, int daysActive)
        {
            int maxDays = crisisType switch
            {
                "Betrayal" => 7,
                "Mutiny" => 5,
                "BountyCrisis" => 10,
                "SiegePanic" => 3,
                _ => 7
            };
            return daysActive >= maxDays;
        }
    }
}


