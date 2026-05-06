using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Core.Neural;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Systems.Legacy
{

    [Serializable]
    public class WarlordLegacyRecord
    {
        [SaveableProperty(1)] public string WarlordId { get; set; } = "";
        [SaveableProperty(2)] public string WarlordName { get; set; } = "";
        [SaveableProperty(3)] public string? HideoutId { get; set; }
        [SaveableProperty(4)] public int DaysActive { get; set; }
        [SaveableProperty(5)] public int Kills { get; set; }
        [SaveableProperty(6)] public float PeakFear { get; set; }
        [SaveableProperty(7)] public PersonalityType Personality { get; set; }
        [SaveableProperty(8)] public MotivationType Motivation { get; set; }


        [SaveableProperty(9)] public Dictionary<string, float> WinningTactics { get; set; } = new();


        [SaveableProperty(10)] public float RumourLifeDays { get; set; }
        [SaveableProperty(11)] public CampaignTime FallTime { get; set; }


        [SaveableProperty(12)] public bool IsLegendary { get; set; }


        public float EchoStrength =>
            MathF.Min(1f, (DaysActive / 120f) * (PeakFear + 0.1f) * (Kills / 20f + 0.5f));
    }

    [BanditMilitias.Core.Components.AutoRegister(Priority = 370, IsCritical = false)]
    public class WarlordLegacySystem : MilitiaModuleBase
    {
        public override string ModuleName => "WarlordLegacy";
        public override bool IsEnabled => Settings.Instance?.EnableWarlordLegacy ?? true;
        public override int Priority => 55;

        private static readonly Lazy<WarlordLegacySystem> _inst =
            new(() => new WarlordLegacySystem());
        public static WarlordLegacySystem Instance => _inst.Value;


        private Dictionary<string, WarlordLegacyRecord> _records = new();


        private Dictionary<string, List<string>> _hideoutLegacies = new();

        private const int LEGENDARY_DAYS_THRESHOLD = WarlordLegacyRules.LEGENDARY_DAYS_THRESHOLD;
        private const int LEGENDARY_KILL_THRESHOLD = WarlordLegacyRules.LEGENDARY_KILL_THRESHOLD;
        private const float RUMOUR_LIFE_BASE_DAYS = WarlordLegacyRules.RUMOUR_LIFE_BASE_DAYS;
        private const float RUMOUR_LIFE_PER_DAY_ACTIVE = WarlordLegacyRules.RUMOUR_LIFE_PER_DAY_ACTIVE;
        private const float LEGEND_ECHO_DECAY_DAILY = WarlordLegacyRules.LEGEND_ECHO_DECAY_DAILY;
        private const float FEAR_ECHO_SUPPRESSION = WarlordLegacyRules.FEAR_ECHO_SUPPRESSION;
        private const float AGGRESSION_BONUS_FROM_ECHO = WarlordLegacyRules.AGGRESSION_BONUS_MAX;

        private WarlordLegacySystem() { }

        public override void Initialize()
        {
            BanditMilitias.Core.Events.EventBus.Instance.Subscribe<WarlordFallenEvent>(OnWarlordFallen);
            BanditMilitias.Core.Events.EventBus.Instance.Subscribe<MilitiaSpawnedEvent>(OnMilitiaSpawned);
        }

        public override void Cleanup()
        {
            BanditMilitias.Core.Events.EventBus.Instance.Unsubscribe<WarlordFallenEvent>(OnWarlordFallen);
            BanditMilitias.Core.Events.EventBus.Instance.Unsubscribe<MilitiaSpawnedEvent>(OnMilitiaSpawned);
            _records.Clear();
            _hideoutLegacies.Clear();
        }


        private void OnWarlordFallen(WarlordFallenEvent evt)
        {
            if (evt?.Warlord == null) return;
            var w = evt.Warlord;

            var rec = new WarlordLegacyRecord
            {
                WarlordId = w.StringId,
                WarlordName = w.Name,
                HideoutId = w.AssignedHideout?.StringId,
                DaysActive = w.DaysActive,
                Kills = w.Kills,
                PeakFear = evt.PeakFear,
                Personality = w.Personality,
                Motivation = w.Motivation,
                FallTime = CampaignTime.Now,
                RumourLifeDays = RUMOUR_LIFE_BASE_DAYS +
                                 (w.DaysActive * RUMOUR_LIFE_PER_DAY_ACTIVE),
                IsLegendary = w.DaysActive >= LEGENDARY_DAYS_THRESHOLD &&
                              w.Kills >= LEGENDARY_KILL_THRESHOLD
            };


            if (evt.WinningTactics != null)
                foreach (var kv in evt.WinningTactics)
                    rec.WinningTactics[kv.Key] = kv.Value;

            _records[w.StringId] = rec;


            if (rec.HideoutId != null)
            {
                if (!_hideoutLegacies.TryGetValue(rec.HideoutId, out var list))
                {
                    list = new List<string>();
                    _hideoutLegacies[rec.HideoutId] = list;
                }
                list.Add(w.StringId);
            }

            if (rec.IsLegendary)
                AnnounceEfsane(rec);

            DebugLogger.Info("WarlordLegacy",
                $"Legacy kaydÄ±: {rec.WarlordName} | Echo={rec.EchoStrength:F2} | " +
                $"Efsane={rec.IsLegendary} | SÃ¶ylenti={rec.RumourLifeDays:F0} gÃ¼n");
        }


        private void OnMilitiaSpawned(MilitiaSpawnedEvent evt)
        {
            if (evt?.Party == null || evt.HomeHideout == null) return;
            if (!IsEnabled) return;

            float totalEcho = GetHideoutEcho(evt.HomeHideout);
            if (totalEcho <= 0f) return;


            float bonus = AGGRESSION_BONUS_FROM_ECHO * totalEcho;
            if (evt.Party.Ai != null)
                evt.Party.Aggressiveness = MathF.Min(1f, evt.Party.Aggressiveness + bonus);


            if (evt.Party.PartyComponent is MilitiaPartyComponent comp)
            {
                var inheritedTactics = GetInheritedTactics(evt.HomeHideout);
                if (inheritedTactics.Count > 0)
                    comp.InheritedTactics = inheritedTactics;
            }

            if (Settings.Instance?.TestingMode == true)
                DebugLogger.Info("WarlordLegacy",
                    $"{evt.Party.Name} legacy echo +{bonus:F2} aggressiveness | " +
                    $"Hideout={evt.HomeHideout.Name}");
        }


        public override void OnDailyTick()
        {
            if (!IsEnabled) return;

            var expired = new List<string>();
            foreach (var rec in _records.Values)
            {
                rec.RumourLifeDays -= 1f;


                float decayMod = rec.IsLegendary ? 0.2f : 1f;
                float echo = rec.EchoStrength;


                rec.PeakFear = MathF.Max(0f, rec.PeakFear - LEGEND_ECHO_DECAY_DAILY * decayMod);


                if (rec.IsLegendary && echo > 0.7f && rec.HideoutId != null)
                {
                    try
                    {
                        var hideout = TaleWorlds.CampaignSystem.Settlements.Settlement.Find(rec.HideoutId);
                        if (hideout != null)
                        {
                            var echoEvt = new BanditMilitias.Core.Events.LegacyEchoActivatedEvent
                            {
                                Record = rec,
                                Hideout = hideout,
                                Echo = echo
                            };
                            BanditMilitias.Core.Neural.NeuralEventRouter.Instance.Publish(echoEvt);
                        }
                    }
                    catch { }
                }

                if (rec.RumourLifeDays <= 0f && echo < 0.05f)
                    expired.Add(rec.WarlordId);
            }

            foreach (var id in expired)
            {
                if (_records.TryGetValue(id, out var rec) && rec.HideoutId != null)
                {
                    if (_hideoutLegacies.TryGetValue(rec.HideoutId, out var list))
                        _ = list.Remove(id);
                }
                _ = _records.Remove(id);
                DebugLogger.Info("WarlordLegacy", $"Legacy silindi: {id}");
            }
        }


        public float GetHideoutEcho(Settlement hideout)
        {
            if (hideout?.StringId == null) return 0f;
            if (!_hideoutLegacies.TryGetValue(hideout.StringId, out var ids)) return 0f;
            float total = 0f;
            foreach (var id in ids)
                if (_records.TryGetValue(id, out var r)) total += r.EchoStrength;
            return MathF.Min(1f, total);
        }

        public float GetFearDecaySuppression(Settlement settlement)
        {
            if (!IsEnabled || settlement?.StringId == null) return 1f;
            if (!_hideoutLegacies.TryGetValue(settlement.StringId, out var ids)) return 1f;

            bool hasActiveRumour = ids.Any(id =>
                _records.TryGetValue(id, out var r) && r.RumourLifeDays > 0f);

            return hasActiveRumour ? FEAR_ECHO_SUPPRESSION : 1f;
        }

        public Dictionary<string, float> GetInheritedTactics(Settlement hideout)
        {
            if (hideout?.StringId == null) return new();
            if (!_hideoutLegacies.TryGetValue(hideout.StringId, out var ids)) return new();


            WarlordLegacyRecord? best = null;
            float bestEcho = 0f;
            foreach (var id in ids)
            {
                if (_records.TryGetValue(id, out var r) && r.EchoStrength > bestEcho)
                {
                    bestEcho = r.EchoStrength;
                    best = r;
                }
            }
            return best?.WinningTactics ?? new();
        }

        public IReadOnlyCollection<WarlordLegacyRecord> GetAllRecords() =>
            _records.Values;

        private static void AnnounceEfsane(WarlordLegacyRecord rec)
        {
            InformationManager.DisplayMessage(new InformationMessage(
                $"[EFSANE] {rec.WarlordName} dÃ¼ÅŸtÃ¼ â€” ama sÃ¶ylentisi bÃ¶lgede yaÅŸÄ±yor.",
                Colors.Magenta));
        }

        public override void SyncData(IDataStore ds)
        {
            _ = ds.SyncData("_legacyRecords_v1", ref _records);
            _ = ds.SyncData("_hideoutLegacies_v1", ref _hideoutLegacies);

            if (ds.IsLoading)
            {
                _records ??= new();
                _hideoutLegacies ??= new();
            }
        }

        public override string GetDiagnostics() =>
            $"WarlordLegacy: {_records.Count} kayÄ±t | " +
            $"{_records.Values.Count(r => r.IsLegendary)} efsane aktif";
    }


    public static class WarlordLegacyRules
    {
        public const int LEGENDARY_DAYS_THRESHOLD = 90;
        public const int LEGENDARY_KILL_THRESHOLD = 15;
        public const float RUMOUR_LIFE_BASE_DAYS = 30f;
        public const float RUMOUR_LIFE_PER_DAY_ACTIVE = 0.3f;
        public const float LEGEND_ECHO_DECAY_DAILY = 0.004f;
        public const float LEGEND_ECHO_DECAY_LEGENDARY = 0.0008f;

        public const float AGGRESSION_BONUS_MAX = 0.25f;
        public const float FEAR_ECHO_SUPPRESSION = 0.5f;


        public static bool IsLegendary(int daysActive, int kills)
            => daysActive >= LEGENDARY_DAYS_THRESHOLD && kills >= LEGENDARY_KILL_THRESHOLD;


        public static float CalcRumourLife(int daysActive)
            => RUMOUR_LIFE_BASE_DAYS + (daysActive * RUMOUR_LIFE_PER_DAY_ACTIVE);


        public static float CalcEchoStrength(int daysActive, float peakFear, int kills)
        {
            float d = Math.Max(0, daysActive) / 120f;
            float f = peakFear + 0.1f;
            float k = Math.Max(0, kills) / 20f + 0.5f;
            return Math.Min(1f, d * f * k);
        }


        public static float DailyEchoDecay(bool isLegendary)
            => isLegendary ? LEGEND_ECHO_DECAY_LEGENDARY : LEGEND_ECHO_DECAY_DAILY;


        public static float AggressionBonus(float echoStrength)
            => Math.Min(1f, echoStrength * AGGRESSION_BONUS_MAX);


        public static float FearDecaySuppression(bool hasActiveRumour)
            => hasActiveRumour ? FEAR_ECHO_SUPPRESSION : 1f;


        public static bool ShouldExpire(float rumourLifeDays, float echoStrength)
            => rumourLifeDays <= 0f && echoStrength < 0.05f;
    }
}


