using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Bounty;
using BanditMilitias.Systems.Economy;
using BanditMilitias.Systems.Fear;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Systems.Progression
{
    // ═══════════════════════════════════════════════════════════════════════════
    // AscensionEvaluator — Çok Boyutlu Warlord Terfi Sistemi
    //
    // 5 bağımsız sütun üzerinden çalışır. Terfi için hepsinde minimum
    // eşik karşılanmalıdır (AND mantığı). Farklı oynanış stilleri
    // (savaşçı / tüccar / diplomat / hayatta kalan) farklı hızlarda
    // farklı sütunları doldurur.
    //
    // Sütun 1 — CombatScore   : Savaş kazanımı, lord/warlord yenme
    // Sütun 2 — EconomyScore  : Ekonomik sürdürülebilirlik, altın birikimi
    // Sütun 3 — TerritoryScore: Kontrol altındaki yerleşim + korku derinliği
    // Sütun 4 — PrestigeScore : İtibar, bounty, ittifak, milestone olaylar
    // Sütun 5 — SurvivalScore : Hayatta kalma günleri + bounty altında yaşam
    //
    // Entegrasyon:
    //   WarlordLegitimacySystem.OnDailyTick() → RecalculateDaily(warlord)
    //   WarlordLegitimacySystem.CheckLevelTransition() → CanPromote(warlord, level)
    //   WarlordLegitimacySystem.CheckLevelTransition() → OnPromotionGranted(warlord, level)
    // ═══════════════════════════════════════════════════════════════════════════

    [Serializable]
    public class AscensionRecord
    {
        [SaveableProperty(1)]  public string WarlordId            { get; set; } = string.Empty;

        // ── 5 Sütun ──
        [SaveableProperty(2)]  public float CombatScore           { get; set; }
        [SaveableProperty(3)]  public float EconomyScore          { get; set; }
        [SaveableProperty(4)]  public float TerritoryScore        { get; set; }
        [SaveableProperty(5)]  public float PrestigeScore         { get; set; }
        [SaveableProperty(6)]  public float SurvivalScore         { get; set; }

        // ── Milestone bayrakları (tek seferlik) ──
        [SaveableProperty(10)] public bool AwardedFirstLordKill   { get; set; }
        [SaveableProperty(11)] public bool AwardedFirstWarlordKill { get; set; }
        [SaveableProperty(12)] public bool AwardedFirstTownRaid   { get; set; }

        // ── Savaş serisi ──
        [SaveableProperty(20)] public int ConsecutiveWins         { get; set; }
        [SaveableProperty(21)] public int ConsecutiveLosses       { get; set; }

        // ── Kırılganlık penceresi ──
        [SaveableProperty(30)] public int   DaysSinceLastPromotion { get; set; } = 999;
        [SaveableProperty(31)] public LegitimacyLevel LastPromotedLevel { get; set; } = LegitimacyLevel.Outlaw;

        // ── Gold milestone bayrakları ──
        [SaveableProperty(40)] public bool Gold5kAwarded          { get; set; }
        [SaveableProperty(41)] public bool Gold15kAwarded         { get; set; }
        [SaveableProperty(42)] public bool Gold40kAwarded         { get; set; }

        public float Total => CombatScore + EconomyScore + TerritoryScore + PrestigeScore + SurvivalScore;
    }

    public class AscensionEvaluator : MilitiaModuleBase
    {
        public override string ModuleName => "AscensionEvaluator";
        public override bool IsEnabled    => BanditMilitias.Settings.Instance?.EnableLegitimacySystem ?? true;
        public override int  Priority     => 66; // WarlordLegitimacySystem (65) + 1

        private static readonly Lazy<AscensionEvaluator> _instance = new(() => new AscensionEvaluator());
        public static AscensionEvaluator Instance => _instance.Value;

        private Dictionary<string, AscensionRecord> _records = new();
        private bool _isInitialized;

        // ── Eşik Tablosu ─────────────────────────────────────────────────────────
        // Her sütun için minimum puan. TERFİ için HEPSİ aynı anda sağlanmalı.
        //
        //                                 Combat  Eco    Terr   Pres   Surv
        private static readonly Dictionary<LegitimacyLevel, (float C, float E, float T, float P, float S)>
        Thresholds = new()
        {
            { LegitimacyLevel.Rebel,        ( 40f,  10f,   8f,   0f,  20f) },
            { LegitimacyLevel.FamousBandit, (120f,  40f,  30f,  15f,  50f) },
            { LegitimacyLevel.Warlord,      (300f, 100f,  80f,  50f,  90f) },
            { LegitimacyLevel.Recognized,   (700f, 220f, 180f, 130f, 160f) },
        };

        // ── Puan Sabitleri ───────────────────────────────────────────────────────

        // Sütun 1 — Combat
        private const float WIN_BASE         =  10f;
        private const float WIN_LORD         =  30f;
        private const float WIN_WARLORD      =  50f;
        private const float WIN_UNDER_2X     =   1.5f;  // 2:1 orana karşı çarpan
        private const float WIN_UNDER_3X     =   2.0f;  // 3:1 orana karşı çarpan
        private const float WIN_STREAK_BONUS =   5f;    // 3.+ kazanış serisi başına
        private const float LOSS_NORMAL      =  -5f;
        private const float LOSS_HEAVY       = -15f;    // lord/warlord'a yenilme
        private const float COMBAT_FLOOR     = -100f;   // negatif taban

        // Sütun 2 — Economy
        private const float ECO_PROFIT_DAY   =   0.5f;  // kârlı gün başına
        private const float ECO_GOLD_5K      =  15f;    // 5k altın milestone
        private const float ECO_GOLD_15K     =  30f;
        private const float ECO_GOLD_40K     =  60f;
        private const float ECO_BM_DAILY     =   0.1f;  // kara borsa ağı başına/gün

        // Sütun 3 — Territory
        private const float TERR_SETT_DAY    =   0.4f;  // kontrollü yerleşim başına/gün
        private const float TERR_FEAR_BONUS  =   0.3f;  // ortalama korku > 0.5 ise ek

        // Sütun 4 — Prestige
        private const float PRES_BOUNTY_DAY  =   0.3f;  // aktif bounty varken/gün
        private const float PRES_FIRST_LORD  =  25f;
        private const float PRES_FIRST_WAR   =  60f;
        private const float PRES_FIRST_TOWN  =  20f;
        private const float PRES_ALLIANCE    =  15f;

        // Sütun 5 — Survival
        private const float SURV_BASE_DAY    =   1.0f;
        private const float SURV_BOUNTY_EXT  =   1.0f;  // bounty varken ek

        // Kırılganlık
        private const int   FRAGILE_DAYS     = 10;
        private const float FRAGILE_PENALTY  = -80f;

        private AscensionEvaluator() { }

        // ── Lifecycle ────────────────────────────────────────────────────────────

        public override void Initialize()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            EventBus.Instance.Subscribe<MilitiaBattleResultEvent>(OnBattleResult);
            EventBus.Instance.Subscribe<WarlordAllianceFormedEvent>(OnAllianceFormed);
            EventBus.Instance.Subscribe<WarlordFallenEvent>(OnWarlordFallen);
            EventBus.Instance.Subscribe<MilitiaRaidCompletedEvent>(OnRaidCompleted);

            DebugLogger.Info("AscensionEvaluator", "Initialized — 5-column promotion gate active.");
        }

        public override void Cleanup()
        {
            EventBus.Instance.Unsubscribe<MilitiaBattleResultEvent>(OnBattleResult);
            EventBus.Instance.Unsubscribe<WarlordAllianceFormedEvent>(OnAllianceFormed);
            EventBus.Instance.Unsubscribe<WarlordFallenEvent>(OnWarlordFallen);
            EventBus.Instance.Unsubscribe<MilitiaRaidCompletedEvent>(OnRaidCompleted);

            _records.Clear();
            _isInitialized = false;
        }

        public override void OnTick(float dt) { }
        public override void OnHourlyTick()   { }
        public override void OnDailyTick()    { } // WarlordLegitimacySystem üzerinden çağrılır

        // ── Ana API ──────────────────────────────────────────────────────────────

        /// <summary>
        /// CheckLevelTransition içinden terfi kararından ÖNCE çağrılır.
        /// Tüm 5 sütun eşiğini karşılıyorsa true döner.
        /// Sistem devre dışıysa her zaman true döner (geriye uyumluluk).
        /// </summary>
        public bool CanPromote(Warlord warlord, LegitimacyLevel targetLevel)
        {
            if (!IsEnabled) return true;
            var rec = GetOrCreate(warlord.StringId);
            bool ok = MeetsAll(rec, targetLevel);

            if (!ok && BanditMilitias.Settings.Instance?.TestingMode == true)
            {
                var progress = GetProgress(rec, targetLevel);
                DebugLogger.Info("AscensionEvaluator",
                    $"[BLOCKED] {warlord.Name} → {targetLevel} | " +
                    $"C:{rec.CombatScore:F0}/{GetThresh(targetLevel).C} " +
                    $"E:{rec.EconomyScore:F0}/{GetThresh(targetLevel).E} " +
                    $"T:{rec.TerritoryScore:F0}/{GetThresh(targetLevel).T} " +
                    $"P:{rec.PrestigeScore:F0}/{GetThresh(targetLevel).P} " +
                    $"S:{rec.SurvivalScore:F0}/{GetThresh(targetLevel).S}");
                DebugLogger.Info("AscensionEvaluator",
                    $"  Progress: C:{progress.C:P0} E:{progress.E:P0} T:{progress.T:P0} P:{progress.P:P0} S:{progress.S:P0}");
            }
            return ok;
        }

        /// <summary>
        /// Terfi onaylandıktan SONRA çağrılır — kırılganlık sayacını sıfırlar.
        /// </summary>
        public void OnPromotionGranted(Warlord warlord, LegitimacyLevel newLevel)
        {
            var rec = GetOrCreate(warlord.StringId);
            rec.LastPromotedLevel       = newLevel;
            rec.DaysSinceLastPromotion  = 0;

            DebugLogger.Info("AscensionEvaluator",
                $"[GRANTED] {warlord.Name} → {newLevel} | " +
                $"C:{rec.CombatScore:F0} E:{rec.EconomyScore:F0} " +
                $"T:{rec.TerritoryScore:F0} P:{rec.PrestigeScore:F0} S:{rec.SurvivalScore:F0} " +
                $"Total:{rec.Total:F0}");
        }

        /// <summary>
        /// WarlordLegitimacySystem.OnDailyTick içinden her warlord için çağrılır.
        /// Economy / Territory / Prestige / Survival sütunlarını günceller.
        /// </summary>
        public void RecalculateDaily(Warlord warlord)
        {
            if (!IsEnabled || !_isInitialized) return;
            var rec = GetOrCreate(warlord.StringId);

            RecalcEconomy(warlord, rec);
            RecalcTerritory(warlord, rec);
            RecalcPrestigeDaily(warlord, rec);
            RecalcSurvival(warlord, rec);

            rec.DaysSinceLastPromotion++;
        }

        /// <summary>
        /// HİBRİT AI: Terfi için gereken asker sayısını dinamik olarak hesaplar.
        /// Tecrübeli liderler (Survival/Combat) daha az askerle meşruiyet kazanabilir.
        /// </summary>
        public int GetDynamicTroopRequirement(Warlord warlord, int originalThreshold)
        {
            if (!IsEnabled) return originalThreshold;
            
            var rec = GetOrCreate(warlord.StringId);
            
            // Formül: Dinamik_Eşik = Sabit_Eşik - (SurvivalScore * 0.5) - (CombatScore * 0.2)
            float reduction = (rec.SurvivalScore * 0.5f) + (rec.CombatScore * 0.2f);
            
            int dynamicThreshold = (int)(originalThreshold - reduction);
            
            // Kural: Eşik asla 25'in altına düşmez (Kritik kitle koruması).
            return Math.Max(25, dynamicThreshold);
        }

        // ── Kayıt Erişimi ─────────────────────────────────────────────────────────

        public AscensionRecord GetOrCreate(string warlordId)
        {
            if (!_records.TryGetValue(warlordId, out var rec))
            {
                rec = new AscensionRecord { WarlordId = warlordId };
                _records[warlordId] = rec;
            }
            return rec;
        }

        public AscensionRecord? GetOrNull(string warlordId)
            => _records.TryGetValue(warlordId, out var r) ? r : null;

        // ── Event Handler'ları ────────────────────────────────────────────────────

        private void OnBattleResult(MilitiaBattleResultEvent evt)
        {
            if (evt == null) return;

            if (evt.WinnerParty != null)
            {
                var winner = WarlordSystem.Instance.GetWarlordForParty(evt.WinnerParty);
                if (winner != null) ApplyVictory(winner, evt);
            }

            if (evt.LoserParty != null)
            {
                var loser = WarlordSystem.Instance.GetWarlordForParty(evt.LoserParty);
                if (loser != null) ApplyDefeat(loser, evt);
            }
        }

        private void ApplyVictory(Warlord warlord, MilitiaBattleResultEvent evt)
        {
            var rec = GetOrCreate(warlord.StringId);
            float pts = WIN_BASE;

            if      (evt.LoserHadWarlordParty) pts = WIN_WARLORD;
            else if (evt.LoserHadLordParty)    pts = WIN_LORD;

            // Underdog çarpanı
            if      (evt.EnemyStrengthRatio >= 3.0f) pts *= WIN_UNDER_3X;
            else if (evt.EnemyStrengthRatio >= 2.0f) pts *= WIN_UNDER_2X;

            // Kazanış serisi
            rec.ConsecutiveWins++;
            rec.ConsecutiveLosses = 0;
            if (rec.ConsecutiveWins >= 3)
                pts += WIN_STREAK_BONUS * (rec.ConsecutiveWins - 2);

            rec.CombatScore += pts;

            // Prestige milestones
            if (evt.LoserHadLordParty && !rec.AwardedFirstLordKill)
            {
                rec.AwardedFirstLordKill = true;
                rec.PrestigeScore       += PRES_FIRST_LORD;
                DebugLogger.Info("AscensionEvaluator",
                    $"🏆 {warlord.Name} — İlk Lord Yenildi! +{PRES_FIRST_LORD} Prestige");
            }

            if (evt.LoserHadWarlordParty && !rec.AwardedFirstWarlordKill)
            {
                rec.AwardedFirstWarlordKill = true;
                rec.PrestigeScore           += PRES_FIRST_WAR;
                DebugLogger.Info("AscensionEvaluator",
                    $"🏆 {warlord.Name} — İlk Warlord Yenildi! +{PRES_FIRST_WAR} Prestige");
            }
        }

        private void ApplyDefeat(Warlord warlord, MilitiaBattleResultEvent evt)
        {
            var rec = GetOrCreate(warlord.StringId);

            bool heavyLoss = evt.WinnerHadLordParty || evt.WinnerHadWarlordParty;
            float penalty  = heavyLoss ? LOSS_HEAVY : LOSS_NORMAL;

            rec.ConsecutiveLosses++;
            rec.ConsecutiveWins = 0;

            // Kırılganlık penceresi — terfi sonrası ilk FRAGILE_DAYS günde büyük yenilgi
            if (rec.DaysSinceLastPromotion <= FRAGILE_DAYS && heavyLoss)
            {
                rec.CombatScore += FRAGILE_PENALTY;
                DebugLogger.Info("AscensionEvaluator",
                    $"⚠ {warlord.Name} kırılganlık penceresinde büyük yenilgi! ({FRAGILE_PENALTY})");
            }
            else
            {
                rec.CombatScore += penalty;
            }

            if (rec.CombatScore < COMBAT_FLOOR) rec.CombatScore = COMBAT_FLOOR;
        }

        private void OnAllianceFormed(WarlordAllianceFormedEvent evt)
        {
            if (evt?.PrimaryWarlord != null)
                GetOrCreate(evt.PrimaryWarlord.StringId).PrestigeScore += PRES_ALLIANCE;

            if (evt?.SecondaryWarlord != null)
                GetOrCreate(evt.SecondaryWarlord.StringId).PrestigeScore += PRES_ALLIANCE;
        }

        private void OnWarlordFallen(WarlordFallenEvent evt)
        {
            // Düşen warlord'un kaydını temizle (WarlordFallenEvent'i zaten WarlordSystem fırlatıyor)
            if (evt?.Warlord != null)
                _ = _records.Remove(evt.Warlord.StringId);
        }

        private void OnRaidCompleted(MilitiaRaidCompletedEvent evt)
        {
            if (evt?.RaiderParty == null || !evt.WasSuccessful) return;

            var warlord = WarlordSystem.Instance.GetWarlordForParty(evt.RaiderParty);
            if (warlord == null) return;

            var rec = GetOrCreate(warlord.StringId);

            // İlk baskın milestone (PrestigeScore)
            if (!rec.AwardedFirstTownRaid && evt.TargetVillage != null)
            {
                rec.AwardedFirstTownRaid = true;
                rec.PrestigeScore       += PRES_FIRST_TOWN;
                DebugLogger.Info("AscensionEvaluator",
                    $"🏆 {warlord.Name} — İlk Baskın! +{PRES_FIRST_TOWN} Prestige");
            }

            // Yağma geliri → EconomyScore'a capped katkı
            float bonus = Math.Min(evt.GoldLooted / 500f, 15f);
            rec.EconomyScore += bonus;
        }

        // ── Günlük Hesaplama ─────────────────────────────────────────────────────

        private static void RecalcEconomy(Warlord warlord, AscensionRecord rec)
        {
            var ecoSys = WarlordEconomySystem.Instance;
            if (ecoSys == null) return;

            // Günlük ekonomik çıktı pozitifse istikrar puanı
            var tier = WarlordCareerSystem.Instance.GetTier(warlord.StringId);
            float dailyOut = ecoSys.CalculateDailyOutcome(warlord, tier);
            if (dailyOut > 0f)
                rec.EconomyScore += ECO_PROFIT_DAY;

            // Gold milestone (tek seferlik)
            float gold = warlord.Gold;
            if (gold >= 5000f  && !rec.Gold5kAwarded)  { rec.Gold5kAwarded  = true; rec.EconomyScore += ECO_GOLD_5K;  }
            if (gold >= 15000f && !rec.Gold15kAwarded) { rec.Gold15kAwarded = true; rec.EconomyScore += ECO_GOLD_15K; }
            if (gold >= 40000f && !rec.Gold40kAwarded) { rec.Gold40kAwarded = true; rec.EconomyScore += ECO_GOLD_40K; }

            // Kara borsa ağları (günlük küçük katkı)
            var bm = BlackMarketSystem.Instance;
            if (bm?.IsEnabled == true)
            {
                int nets = bm.GetNetworkCount(warlord.StringId);
                rec.EconomyScore += nets * ECO_BM_DAILY;
            }
        }

        private static void RecalcTerritory(Warlord warlord, AscensionRecord rec)
        {
            var fearSys = FearSystem.Instance;
            if (fearSys?.IsEnabled != true) return;

            int controlled = fearSys.GetControlledSettlementCount(warlord.StringId);
            if (controlled <= 0) return;

            float daily = controlled * TERR_SETT_DAY;

            // Yüksek korku derinliği bonusu
            float avgFear = fearSys.GetAverageFearForWarlord(warlord.StringId);
            if (avgFear > 0.5f) daily += controlled * TERR_FEAR_BONUS;

            rec.TerritoryScore += daily;
        }

        private static void RecalcPrestigeDaily(Warlord warlord, AscensionRecord rec)
        {
            var bountySys = BountySystem.Instance;
            if (bountySys?.IsEnabled != true) return;

            if (bountySys.GetBounty(warlord.StringId) > 0)
                rec.PrestigeScore += PRES_BOUNTY_DAY;
        }

        private static void RecalcSurvival(Warlord warlord, AscensionRecord rec)
        {
            var bountySys = BountySystem.Instance;
            bool hasBounty = bountySys?.IsEnabled == true
                          && bountySys.GetBounty(warlord.StringId) > 0;

            rec.SurvivalScore += SURV_BASE_DAY + (hasBounty ? SURV_BOUNTY_EXT : 0f);
        }

        // ── Eşik Yardımcıları ─────────────────────────────────────────────────────

        private static bool MeetsAll(AscensionRecord rec, LegitimacyLevel level)
        {
            if (!Thresholds.TryGetValue(level, out var t)) return true;
            return rec.CombatScore    >= t.C
                && rec.EconomyScore   >= t.E
                && rec.TerritoryScore >= t.T
                && rec.PrestigeScore  >= t.P
                && rec.SurvivalScore  >= t.S;
        }

        private static (float C, float E, float T, float P, float S) GetThresh(LegitimacyLevel level)
            => Thresholds.TryGetValue(level, out var t) ? t : default;

        private static (float C, float E, float T, float P, float S) GetProgress(AscensionRecord rec, LegitimacyLevel level)
        {
            if (!Thresholds.TryGetValue(level, out var t)) return (1, 1, 1, 1, 1);
            return (
                t.C > 0 ? Math.Min(1f, rec.CombatScore    / t.C) : 1f,
                t.E > 0 ? Math.Min(1f, rec.EconomyScore   / t.E) : 1f,
                t.T > 0 ? Math.Min(1f, rec.TerritoryScore / t.T) : 1f,
                t.P > 0 ? Math.Min(1f, rec.PrestigeScore  / t.P) : 1f,
                t.S > 0 ? Math.Min(1f, rec.SurvivalScore  / t.S) : 1f
            );
        }

        // ── SyncData ─────────────────────────────────────────────────────────────

        public override void SyncData(IDataStore ds)
        {
            _ = ds.SyncData("_ascensionRecords_v1", ref _records);
            if (ds.IsLoading) _records ??= new();
        }

        public override string GetDiagnostics()
        {
            return $"AscensionEvaluator: {_records.Count} kayıt | IsEnabled={IsEnabled}";
        }
    }
}
