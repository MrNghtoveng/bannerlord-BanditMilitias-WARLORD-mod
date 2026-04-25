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
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //  WARLORD LEGACY SYSTEM
    //  Bir warlord yok olunca tarihi silinmez.
    //
    //  â€¢ GÃ¶lgeKorku  : BÃ¶lgede "geÃ§miÅŸ warlord'un korkusu" kalÄ±ntÄ±sÄ±.
    //                  Yeni spawn'lar %BaseAggression + legacy_echo kadar
    //                  baÅŸlangÄ±Ã§ saldÄ±rganlÄ±ÄŸÄ± kazanÄ±r.
    //  â€¢ MirasÃ§Ä±TaktiÄŸi: Bir sonraki warlord eski warlord'un kazanan
    //                  command type'larÄ±nÄ± kÄ±smen devralÄ±r.
    //  â€¢ SÃ¶ylenti DalgasÄ±: Warlord dÃ¼ÅŸÃ¼nce Ã§evre kÃ¶ylerde kÄ±sa sÃ¼reli
    //                  fear_decay baskÄ±lanÄ±r â€” "efsanesi devam ediyor".
    //  â€¢ KahraMontajÄ±  : Yeterince uzun yaÅŸayan warlord'lar haritada
    //                  kalÄ±cÄ± bir "efsane noktasÄ±" bÄ±rakÄ±r. Bu nokta ileriki
    //                  spawn'lara bonus verir.
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

        // Kazanan taktikler (CommandType â†’ baÅŸarÄ± oranÄ±)
        [SaveableProperty(9)] public Dictionary<string, float> WinningTactics { get; set; } = new();

        // SÃ¶ylenti kalan Ã¶mrÃ¼ (gÃ¼n)
        [SaveableProperty(10)] public float RumourLifeDays { get; set; }
        [SaveableProperty(11)] public CampaignTime FallTime { get; set; }

        // "Efsane eÅŸiÄŸi" geÃ§ildi mi?
        [SaveableProperty(12)] public bool IsLegendary { get; set; }

        // Hesaplanan gÃ¶lge korku ÅŸiddeti [0-1]
        public float EchoStrength =>
            MathF.Min(1f, (DaysActive / 120f) * (PeakFear + 0.1f) * (Kills / 20f + 0.5f));
    }

    public class WarlordLegacySystem : MilitiaModuleBase
    {
        public override string ModuleName => "WarlordLegacy";
        public override bool IsEnabled => Settings.Instance?.EnableWarlordLegacy ?? true;
        public override int Priority => 55;

        private static readonly Lazy<WarlordLegacySystem> _inst =
            new(() => new WarlordLegacySystem());
        public static WarlordLegacySystem Instance => _inst.Value;

        // WarlordId â†’ legacy kaydÄ± (Ã¶lÃ¼ warlord'lar iÃ§in)
        private Dictionary<string, WarlordLegacyRecord> _records = new();

        // HideoutId â†’ o hideout'ta hangi legacy'ler aktif
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
            EventBus.Instance.Subscribe<WarlordFallenEvent>(OnWarlordFallen);
            EventBus.Instance.Subscribe<MilitiaSpawnedEvent>(OnMilitiaSpawned);
        }

        public override void Cleanup()
        {
            EventBus.Instance.Unsubscribe<WarlordFallenEvent>(OnWarlordFallen);
            EventBus.Instance.Unsubscribe<MilitiaSpawnedEvent>(OnMilitiaSpawned);
            _records.Clear();
            _hideoutLegacies.Clear();
        }

        // â”€â”€ Warlord dÃ¼ÅŸtÃ¼ÄŸÃ¼nde legacy kaydÄ± oluÅŸtur â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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

            // Kazanan taktikleri aktar
            if (evt.WinningTactics != null)
                foreach (var kv in evt.WinningTactics)
                    rec.WinningTactics[kv.Key] = kv.Value;

            _records[w.StringId] = rec;

            // Hideout â†’ legacy haritasÄ±nÄ± gÃ¼ncelle
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

        // â”€â”€ Yeni militia spawn â†’ legacy bonus uygula â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private void OnMilitiaSpawned(MilitiaSpawnedEvent evt)
        {
            if (evt?.Party == null || evt.HomeHideout == null) return;
            if (!IsEnabled) return;

            float totalEcho = GetHideoutEcho(evt.HomeHideout);
            if (totalEcho <= 0f) return;

            // SaldÄ±rganlÄ±k bonusu â€” "ata warlord'un ruhu" etkisi
            float bonus = AGGRESSION_BONUS_FROM_ECHO * totalEcho;
            if (evt.Party.Ai != null)
                evt.Party.Aggressiveness = MathF.Min(1f, evt.Party.Aggressiveness + bonus);

            // Kazanan taktik mirasÄ± â€” component'e yaz
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

        // â”€â”€ GÃ¼nlÃ¼k: sÃ¶ylenti Ã¶mrÃ¼ azalt, echo decay â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        public override void OnDailyTick()
        {
            if (!IsEnabled) return;

            var expired = new List<string>();
            foreach (var rec in _records.Values)
            {
                rec.RumourLifeDays -= 1f;

                // Echo doÄŸal sÃ¶nÃ¼mleme â€” efsanelerde Ã§ok daha yavaÅŸ
                float decayMod = rec.IsLegendary ? 0.2f : 1f;
                float echo = rec.EchoStrength;
                // EchoStrength hesaplanan property; DaysActive ve PeakFear Ã¼zerinden
                // ancak biz PeakFear'Ä± daily azaltarak etkili yavaÅŸ decay yaparÄ±z
                rec.PeakFear = MathF.Max(0f, rec.PeakFear - LEGEND_ECHO_DECAY_DAILY * decayMod);

                // Efsanevi echo aktif olduğunda event fırlat (günde bir kez)
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

        // â”€â”€ YardÄ±mcÄ±lar â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>Bir hideout'taki toplam echo kuvvetini dÃ¶ndÃ¼rÃ¼r.</summary>
        public float GetHideoutEcho(Settlement hideout)
        {
            if (hideout?.StringId == null) return 0f;
            if (!_hideoutLegacies.TryGetValue(hideout.StringId, out var ids)) return 0f;
            float total = 0f;
            foreach (var id in ids)
                if (_records.TryGetValue(id, out var r)) total += r.EchoStrength;
            return MathF.Min(1f, total);
        }

        /// <summary>SÃ¶ylenti etkisi aktifken fear decay baskÄ±lama katsayÄ±sÄ± dÃ¶ner.</summary>
        public float GetFearDecaySuppression(Settlement settlement)
        {
            if (!IsEnabled || settlement?.StringId == null) return 1f;
            if (!_hideoutLegacies.TryGetValue(settlement.StringId, out var ids)) return 1f;

            bool hasActiveRumour = ids.Any(id =>
                _records.TryGetValue(id, out var r) && r.RumourLifeDays > 0f);

            return hasActiveRumour ? FEAR_ECHO_SUPPRESSION : 1f;
        }

        /// <summary>Hideout'taki en gÃ¼Ã§lÃ¼ legacy'den miras alÄ±nan taktik seti.</summary>
        public Dictionary<string, float> GetInheritedTactics(Settlement hideout)
        {
            if (hideout?.StringId == null) return new();
            if (!_hideoutLegacies.TryGetValue(hideout.StringId, out var ids)) return new();

            // En yÃ¼ksek echo'lu legacy'yi bul
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

    // ── WarlordLegacyRules (inline) ──────────────────────────────
    /// <summary>
    /// WarlordLegacySystem için pure logic: echo kuvveti, söylenti ömrü.
    /// </summary>
    public static class WarlordLegacyRules
    {
        public const int LEGENDARY_DAYS_THRESHOLD = 90;
        public const int LEGENDARY_KILL_THRESHOLD = 15;
        public const float RUMOUR_LIFE_BASE_DAYS = 30f;
        public const float RUMOUR_LIFE_PER_DAY_ACTIVE = 0.3f;
        public const float LEGEND_ECHO_DECAY_DAILY = 0.004f;
        public const float LEGEND_ECHO_DECAY_LEGENDARY = 0.0008f; // efsane = 5x daha yavaş
        public const float AGGRESSION_BONUS_MAX = 0.25f;
        public const float FEAR_ECHO_SUPPRESSION = 0.5f;

        /// <summary>Warlord efsane eşiğini geçti mi?</summary>
        public static bool IsLegendary(int daysActive, int kills)
            => daysActive >= LEGENDARY_DAYS_THRESHOLD && kills >= LEGENDARY_KILL_THRESHOLD;

        /// <summary>
        /// Söylenti ömrünü hesapla (gün).
        /// Daha uzun yaşayan warlord'lar daha uzun söylenti bırakır.
        /// </summary>
        public static float CalcRumourLife(int daysActive)
            => RUMOUR_LIFE_BASE_DAYS + (daysActive * RUMOUR_LIFE_PER_DAY_ACTIVE);

        /// <summary>
        /// Echo kuvvetini hesapla [0-1].
        /// daysActive=120, peakFear=0.9, kills=20 → ~1.0
        /// </summary>
        public static float CalcEchoStrength(int daysActive, float peakFear, int kills)
        {
            float d = Math.Max(0, daysActive) / 120f;
            float f = peakFear + 0.1f;
            float k = Math.Max(0, kills) / 20f + 0.5f;
            return Math.Min(1f, d * f * k);
        }

        /// <summary>Günlük echo azalması (efsane yavaş solar).</summary>
        public static float DailyEchoDecay(bool isLegendary)
            => isLegendary ? LEGEND_ECHO_DECAY_LEGENDARY : LEGEND_ECHO_DECAY_DAILY;

        /// <summary>Agresiflik bonusu: echo × max_bonus.</summary>
        public static float AggressionBonus(float echoStrength)
            => Math.Min(1f, echoStrength * AGGRESSION_BONUS_MAX);

        /// <summary>
        /// Fear decay suppression katsayısı.
        /// Söylenti aktifken fear daha yavaş azalır.
        /// </summary>
        public static float FearDecaySuppression(bool hasActiveRumour)
            => hasActiveRumour ? FEAR_ECHO_SUPPRESSION : 1f;

        /// <summary>Legacy silinmeli mi?</summary>
        public static bool ShouldExpire(float rumourLifeDays, float echoStrength)
            => rumourLifeDays <= 0f && echoStrength < 0.05f;
    }
}