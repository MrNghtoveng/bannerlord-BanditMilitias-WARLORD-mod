using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Economy;
using BanditMilitias.Systems.Enhancement;
using BanditMilitias.Systems.Fear;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Systems.Progression
{
    // ── WarlordLegitimacySystem ─────────────────────────────────────────

    public enum LegitimacyLevel
    {
        Outlaw = 0,
        Rebel = 1,
        FamousBandit = 2,
        Warlord = 3,
        Recognized = 4
    }

    [Serializable]
    public class WarlordLegitimacyRecord
    {
        [SaveableProperty(1)]
        public string WarlordId { get; set; } = string.Empty;
        [SaveableProperty(2)]
        public LegitimacyLevel Level { get; set; } = LegitimacyLevel.Outlaw;
        [SaveableProperty(3)]
        public float LegitimacyPoints { get; set; } = 0f;
        [SaveableProperty(4)]
        public CampaignTime LastLevelChangeTime { get; set; } = CampaignTime.Zero;

        [SaveableProperty(5)]
        public int TotalSettlementsControlled { get; set; } = 0;
        [SaveableProperty(6)]
        public float MaxFearAchieved { get; set; } = 0f;
        [SaveableProperty(7)]
        public int TotalVillagesPillaged { get; set; } = 0;
    }

    [BanditMilitias.Core.Components.AutoRegister]
    public class WarlordLegitimacySystem : BanditMilitias.Core.Components.MilitiaModuleBase
    {

        public override string ModuleName => "LegitimacySystem";
        public override bool IsEnabled => BanditMilitias.Settings.Instance?.EnableLegitimacySystem ?? true;
        public override int Priority => 65;

        private static readonly Lazy<WarlordLegitimacySystem> _instance =
            new Lazy<WarlordLegitimacySystem>(() => new WarlordLegitimacySystem());
        public static WarlordLegitimacySystem Instance => _instance.Value;

        private Dictionary<string, WarlordLegitimacyRecord> _records = new();

        // ── Kariyer eÅŸikleri (WarlordCareerRules'dan beslenir) ──────────
        public static float THRESH_REBEL => WarlordCareerRules.THRESH_REBEL;
        public static float THRESH_FAMOUS => WarlordCareerRules.THRESH_FAMOUS;
        public static float THRESH_WARLORD => WarlordCareerRules.THRESH_WARLORD;
        public static float THRESH_TANINMIS => WarlordCareerRules.THRESH_TANINMIS;

        // YENİ: Çok Faktörlü Terfi Eşikleri (AI Motivasyonu için)
        private const int MIN_GOLD_FAMOUS = 15000;
        private const int MIN_TROOPS_FAMOUS = 45;
        private const int MIN_GOLD_WARLORD = 40000;
        private const int MIN_TROOPS_WARLORD = 110;
        private const int MIN_GOLD_RECOGNIZED = 150000;
        private const int MIN_TROOPS_RECOGNIZED = 220;

        public struct PromotionDrive
        {
            public float WealthDrive;  // Altın ihtiyacı (0-1)
            public float PowerDrive;   // Asker ihtiyacı (0-1)
            public float HonorDrive;   // Nam/Puan ihtiyacı (0-1)
            public LegitimacyLevel NextLevel;
            public bool IsCloseToPromotion;
        }

        private const float POINTS_PER_CONTROLLED_VILLAGE = 2.5f;

        private const float POINTS_EXTORTION_SUCCESS = 15f;
        private const float POINTS_RELEASED_LORD = 50f;
        private const float POINTS_PILLAGE_PENALTY = -25f;
        private const float POINTS_BETRAYAL_PENALTY = -60f;

        private bool _isInitialized = false;

        private WarlordLegitimacySystem() { }

        public override void Initialize()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            EventBus.Instance.Subscribe<WarlordBetrayedEvent>(OnWarlordBetrayed);

            DebugLogger.Info("LegitimacySystem", "WarlordLegitimacySystem initialized.");
        }

        public override void Cleanup()
        {
            EventBus.Instance.Unsubscribe<WarlordBetrayedEvent>(OnWarlordBetrayed);
            _records.Clear();
            _isInitialized = false;
        }

        public override void RegisterCampaignEvents()
        {
            SyncAllMilitiaBannerPrestige();
        }

        public override void OnTick(float dt) { }
        public override void OnHourlyTick() { }

        public override void OnDailyTick()
        {
            if (!IsEnabled || !_isInitialized) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;

            var warlords = WarlordSystem.Instance.GetAllWarlords();

            foreach (var warlord in warlords)
            {
                var record = GetOrCreateRecord(warlord.StringId);
                float dailyChange = 0f;

                int controlledCount = 0;

                if (ModuleAccess.TryGetEnabled<FearSystem>(out var fearSystem))
                {

                    try
                    {
                        controlledCount = fearSystem.GetControlledSettlementCount(warlord.StringId);
                    }
                    catch
                    {

                        controlledCount = warlord.CommandedMilitias.Count / 2;
                    }
                }

                dailyChange += controlledCount * POINTS_PER_CONTROLLED_VILLAGE;

                if (warlord.Gold > 50000) dailyChange += 5f;
                else if (warlord.Gold > 10000) dailyChange += 2f;

                ApplyPoints(warlord, dailyChange, "Daily prestige");

                // AscensionEvaluator: 5 sütunlu günlük güncelleme
                AscensionEvaluator.Instance.RecalculateDaily(warlord);

                CheckLevelTransition(warlord, record);
            }
        }

        public PromotionDrive ComputePromotionDrive(Warlord warlord)
        {
            var record = GetOrCreateRecord(warlord.StringId);
            var drive = new PromotionDrive { NextLevel = record.Level + 1 };
            
            if (record.Level >= LegitimacyLevel.Recognized) return drive;

            float targetPts = drive.NextLevel switch {
                LegitimacyLevel.Rebel => THRESH_REBEL,
                LegitimacyLevel.FamousBandit => THRESH_FAMOUS,
                LegitimacyLevel.Warlord => THRESH_WARLORD,
                LegitimacyLevel.Recognized => THRESH_TANINMIS,
                _ => 10000f
            };
            int targetGold = drive.NextLevel switch {
                LegitimacyLevel.FamousBandit => MIN_GOLD_FAMOUS,
                LegitimacyLevel.Warlord => MIN_GOLD_WARLORD,
                LegitimacyLevel.Recognized => MIN_GOLD_RECOGNIZED,
                _ => 0
            };
            // HİBRİT AI: Dinamik asker gereksinimi hesapla
            int targetTroops = drive.NextLevel switch {
                LegitimacyLevel.FamousBandit => AscensionEvaluator.Instance.GetDynamicTroopRequirement(warlord, MIN_TROOPS_FAMOUS),
                LegitimacyLevel.Warlord => AscensionEvaluator.Instance.GetDynamicTroopRequirement(warlord, MIN_TROOPS_WARLORD),
                LegitimacyLevel.Recognized => AscensionEvaluator.Instance.GetDynamicTroopRequirement(warlord, MIN_TROOPS_RECOGNIZED),
                _ => 0
            };

            int currentTroops = warlord.CommandedMilitias != null ? warlord.CommandedMilitias.Sum(m => m.MemberRoster.TotalManCount) : 0;

            drive.HonorDrive = Math.Max(0, (targetPts - record.LegitimacyPoints) / Math.Max(1, targetPts));
            drive.WealthDrive = targetGold > 0 ? Math.Max(0, (float)(targetGold - warlord.Gold) / targetGold) : 0;
            drive.PowerDrive = targetTroops > 0 ? Math.Max(0, (float)(targetTroops - currentTroops) / targetTroops) : 0;

            drive.IsCloseToPromotion = drive.HonorDrive < 0.2f && drive.WealthDrive < 0.2f && drive.PowerDrive < 0.2f;

            return drive;
        }

        public void OnSuccessfulExtortion(Warlord warlord, Settlement target)
        {
            ApplyPoints(warlord, POINTS_EXTORTION_SUCCESS, $"Extortion from {target.Name}");
        }

        public void OnVillagePillaged(Warlord warlord, Settlement target)
        {
            var record = GetOrCreateRecord(warlord.StringId);
            record.TotalVillagesPillaged++;

            ApplyPoints(warlord, POINTS_PILLAGE_PENALTY, $"Pillaged {target.Name}");
        }

        public void OnLordReleased(Warlord warlord, Hero lord)
        {
            ApplyPoints(warlord, POINTS_RELEASED_LORD, $"Released Lord {lord.Name}");
        }

        public void AddFallbackPoints(Warlord warlord, float points)
        {
            ApplyPoints(warlord, points, "Fallback Warlord promotion");
            var record = GetOrCreateRecord(warlord.StringId);
            CheckLevelTransition(warlord, record);
        }

        private void OnWarlordBetrayed(WarlordBetrayedEvent evt)
        {
            if (evt.VictimWarlord == null) return;
            ApplyPoints(evt.VictimWarlord, POINTS_BETRAYAL_PENALTY, "Betrayed by forces");
        }

        public void ApplyPoints(Warlord warlord, float points, string reason)
        {
            if (points == 0) return;

            var record = GetOrCreateRecord(warlord.StringId);
            record.LegitimacyPoints += points;

            if (record.LegitimacyPoints < 0) record.LegitimacyPoints = 0;

            if (BanditMilitias.Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("Legitimacy",
                    $"[{warlord.Name}] {points:+#;-#} points ({reason}). Total: {record.LegitimacyPoints:F1}");
            }
        }

        private void CheckLevelTransition(Warlord warlord, WarlordLegitimacyRecord record)
        {
            float pts = record.LegitimacyPoints;
            LegitimacyLevel oldLevel = record.Level;
            LegitimacyLevel newLevel;

            int gold = (int)warlord.Gold;
            int troops = warlord.CommandedMilitias != null ? warlord.CommandedMilitias.Sum(m => m.MemberRoster.TotalManCount) : 0;

            // FIX-7: Alternatif Terfi Yolları (Yol A/B/C)
            // Savaşçı (Yüksek Asker), Tüccar (Yüksek Altın), Diplomat (Yüksek Puan/Legitimacy)

            // HİBRİT AI: Dinamik asker gereksinimlerini al
            int dynFamous = AscensionEvaluator.Instance.GetDynamicTroopRequirement(warlord, MIN_TROOPS_FAMOUS);
            int dynWarlord = AscensionEvaluator.Instance.GetDynamicTroopRequirement(warlord, MIN_TROOPS_WARLORD);
            int dynRecognized = AscensionEvaluator.Instance.GetDynamicTroopRequirement(warlord, MIN_TROOPS_RECOGNIZED);

            // RECOGNIZED
            bool isWarrior_Rec = (pts >= THRESH_TANINMIS * 0.8f && troops >= dynRecognized);
            bool isMerchant_Rec = (pts >= THRESH_TANINMIS * 0.6f && gold >= MIN_GOLD_RECOGNIZED);
            bool isDiplomat_Rec = (pts >= THRESH_TANINMIS * 1.5f && (gold >= MIN_GOLD_RECOGNIZED / 2 || troops >= dynRecognized / 2));

            // WARLORD
            bool isWarrior_War = (pts >= THRESH_WARLORD * 0.8f && troops >= dynWarlord);
            bool isMerchant_War = (pts >= THRESH_WARLORD * 0.6f && gold >= MIN_GOLD_WARLORD);
            bool isDiplomat_War = (pts >= THRESH_WARLORD * 1.4f && (gold >= MIN_GOLD_WARLORD / 2 || troops >= dynWarlord / 2));

            // FAMOUS BANDIT
            bool isWarrior_Fam = (pts >= THRESH_FAMOUS * 0.8f && troops >= dynFamous);
            bool isMerchant_Fam = (pts >= THRESH_FAMOUS * 0.6f && gold >= MIN_GOLD_FAMOUS);
            bool isDiplomat_Fam = (pts >= THRESH_FAMOUS * 1.3f && (gold >= MIN_GOLD_FAMOUS / 2 || troops >= dynFamous / 2));

            if (isWarrior_Rec || isMerchant_Rec || isDiplomat_Rec) newLevel = LegitimacyLevel.Recognized;
            else if (isWarrior_War || isMerchant_War || isDiplomat_War) newLevel = LegitimacyLevel.Warlord;
            else if (isWarrior_Fam || isMerchant_Fam || isDiplomat_Fam) newLevel = LegitimacyLevel.FamousBandit;
            else if (pts >= THRESH_REBEL) newLevel = LegitimacyLevel.Rebel;
            else newLevel = LegitimacyLevel.Outlaw;

            // AscensionEvaluator: terfi için 5 sütun da karşılanmalı
            if (newLevel > oldLevel && !AscensionEvaluator.Instance.CanPromote(warlord, newLevel))
                newLevel = oldLevel; // Henüz hazır değil

            if (newLevel != oldLevel)
            {
                // Terfi onaylandı — kırılganlık sayacını sıfırla
                AscensionEvaluator.Instance.OnPromotionGranted(warlord, newLevel);

                record.Level = newLevel;
                record.LastLevelChangeTime = CampaignTime.Now;
                ApplyMilitiaBannerPrestige(warlord, newLevel);

                string title = newLevel.ToString().ToUpper();
                string titleTr = newLevel switch
                {
                    LegitimacyLevel.Rebel => "KAPTAN",
                    LegitimacyLevel.FamousBandit => "ÜNLÜ EŞKİYA",
                    LegitimacyLevel.Warlord => "SAVAŞ LORDU",
                    LegitimacyLevel.Recognized => "HÜKÜMDAR",
                    _ => title
                };

                // YENİ: Büyük Ekranda UI Yükseliş Duyurusu
                InformationManager.DisplayMessage(new InformationMessage(
                    $"Karanlık Siyaset: {warlord.Name} artık {titleTr} statüsüne ulaştı!",
                    Colors.Magenta));

                var evt = EventBus.Instance.Get<WarlordLevelChangedEvent>();
                evt.Warlord = warlord;
                evt.OldLevel = oldLevel;
                evt.NewLevel = newLevel;
                EventBus.Instance.Publish(evt);
                EventBus.Instance.Return(evt);

                TelemetryBridge.LogEvent("LegitimacyChange", new
                {
                    warlord = warlord.Name?.ToString(),
                    oldLevel,
                    newLevel,
                    points = Math.Round(record.LegitimacyPoints, 1)
                });
            }
        }


        private bool IsStrongestWarlord(string warlordId)
        {
            float myPoints = GetPoints(warlordId);
            bool isStrongest = true;

            foreach (var record in _records.Values)
            {
                if (record.WarlordId != warlordId && record.Level >= LegitimacyLevel.Warlord)
                {
                    if (record.LegitimacyPoints >= myPoints)
                    {
                        isStrongest = false;
                        break;
                    }
                }
            }
            return isStrongest;
        }

        private void SyncAllMilitiaBannerPrestige()
        {
            foreach (var warlord in WarlordSystem.Instance.GetAllWarlords())
            {
                if (warlord == null || !warlord.IsAlive)
                    continue;

                ApplyMilitiaBannerPrestige(warlord, GetLevel(warlord.StringId));
            }
        }

        private static void ApplyMilitiaBannerPrestige(Warlord warlord, LegitimacyLevel level)
        {
            foreach (var militia in warlord.CommandedMilitias)
            {
                if (militia?.PartyComponent is MilitiaPartyComponent comp)
                {
                    comp.WarlordId = warlord.StringId;
                    comp.SetBannerPrestigeLevel(level);
                }
            }
        }

        public LegitimacyLevel GetLevel(string warlordId)
        {
            if (_records.TryGetValue(warlordId, out var record))
                return record.Level;
            return LegitimacyLevel.Outlaw;
        }

        public float GetPoints(string warlordId)
        {
            if (_records.TryGetValue(warlordId, out var record))
                return record.LegitimacyPoints;
            return 0f;
        }

        public float GetProgressToNextLevel(string warlordId)
        {
            var record = GetOrCreateRecord(warlordId);
            float current = record.LegitimacyPoints;

            return record.Level switch
            {
                LegitimacyLevel.Outlaw => current / THRESH_REBEL,
                LegitimacyLevel.FamousBandit => (current - THRESH_FAMOUS) / (THRESH_WARLORD - THRESH_FAMOUS),
                LegitimacyLevel.Warlord => (current - THRESH_WARLORD) / (THRESH_TANINMIS - THRESH_WARLORD),
                _ => 1.0f
            };
        }

        private WarlordLegitimacyRecord GetOrCreateRecord(string warlordId)
        {
            if (!_records.TryGetValue(warlordId, out var record))
            {
                record = new WarlordLegitimacyRecord { WarlordId = warlordId };
                _records[warlordId] = record;
            }
            return record;
        }

        public override void SyncData(IDataStore dataStore)
        {
            _ = dataStore.SyncData("_legitimacy_records_v1", ref _records);

            if (dataStore.IsLoading && _records == null)
                _records = new Dictionary<string, WarlordLegitimacyRecord>();
        }

        public override string GetDiagnostics()
        {
            var levels = _records.Values.GroupBy(r => r.Level)
                .Select(g => $"{g.Key}: {g.Count()}");

            return $"LegitimacySystem:\n" +
                   $"  Tracked Warlords: {_records.Count}\n" +
                   $"  Levels: {string.Join(", ", levels)}";
        }
    }

    // ── WarlordCareerSystem ─────────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════
    //  WARLORD CAREER SYSTEM — 6-TIER PROMOTION & EFFECT ENGINE
    //
    //  [0] OUTLAW     — Common bandit. No effect.
    //  [1] REBEL      — Fear spreads in region. +2k gold.
    //  [2] WARLORD    — Personality assigned. BanditBrain active. Swarm starts.
    //  [3] MINOR LORD — Name changes. Black market + propaganda unlocked.
    //  [4] RECOGNIZED — Elite equipment. +20k. Kingdoms offer alliance.
    //  [5] CONQUEROR  — Unique warlord title. Red notification.
    // ═══════════════════════════════════════════════════════════════════

    [Serializable]
    public class CareerRecord
    {
        [SaveableProperty(1)] public string WarlordId { get; set; } = "";
        [SaveableProperty(2)] public CareerTier Tier { get; set; } = CareerTier.Eskiya;
        [SaveableProperty(3)] public CareerTier PreviousTier { get; set; } = CareerTier.Eskiya;
        [SaveableProperty(4)] public CampaignTime LastPromoTime { get; set; } = CampaignTime.Zero;
        [SaveableProperty(5)] public int AllianceOffers { get; set; } = 0;
        [SaveableProperty(6)] public bool BrainActivated { get; set; } = false;
        [SaveableProperty(7)] public bool NameChanged { get; set; } = false;
        [SaveableProperty(8)] public bool IsFatih { get; set; } = false;
    }

    public enum CareerTier
    {
        Eskiya = 0, Rebel = 1, FamousBandit = 2,
        Warlord = 3, Taninmis = 4, Fatih = 5
    }

    public class WarlordCareerSystem : MilitiaModuleBase
    {
        public override string ModuleName => "WarlordCareer";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 88;

        private static readonly Lazy<WarlordCareerSystem> _inst = new(() => new WarlordCareerSystem());
        public static WarlordCareerSystem Instance => _inst.Value;

        private Dictionary<string, CareerRecord> _records = new();

        // Constants from WarlordCareerRules — single source
        private const float ALLIANCE_COOLDOWN_DAYS = 30f;
        private const int MAX_ALLIANCE_OFFERS = 3;

        private WarlordCareerSystem() { }

        public override void Initialize()
        {
            EventBus.Instance.Subscribe<WarlordFallenEvent>(OnWarlordFallen);
            EventBus.Instance.Subscribe<WarlordLevelChangedEvent>(OnLevelChanged);
        }

        public override void Cleanup()
        {
            EventBus.Instance.Unsubscribe<WarlordFallenEvent>(OnWarlordFallen);
            EventBus.Instance.Unsubscribe<WarlordLevelChangedEvent>(OnLevelChanged);
            _records.Clear();
        }

        private void OnLevelChanged(WarlordLevelChangedEvent evt)
        {
            if (evt?.Warlord == null) return;
            OnTierPromotion(evt.Warlord, evt.NewLevel);
        }

        // LegitimacySystem calls this method after each promotion
        public void OnTierPromotion(Warlord warlord, LegitimacyLevel newLevel)
        {
            if (warlord == null) return;
            var tier = LevelToTier(newLevel);
            var rec = GetOrCreate(warlord.StringId);
            if (tier <= rec.Tier) return;

            rec.PreviousTier = rec.Tier;
            rec.Tier = tier;
            rec.LastPromoTime = CampaignTime.Now;
            ApplyPromotion(warlord, rec, tier);
        }

        // —— Promotion effects ———————————————————————————————
        private void ApplyPromotion(Warlord w, CareerRecord rec, CareerTier tier)
        {
            // Büyük ekran duyurusu (Inquiry) — İlk rütbe değilse ve ralli modu açıksa
            InformationManager.ShowInquiry(new InquiryData(
                "Rütbe Yükselişi!",
                $"{w.FullName}, rütbesini {tier} seviyesine çıkardı! Calradia bu yükselişi konuşuyor.",
                true, false, "Kabul Et", "", null, null
            ));

            // Gold bonus from single source — WarlordCareerRules
            w.Gold += WarlordCareerRules.GetPromoGold(tier);

            // NEW: Multi-tiered Passive Bonuses (User Request)
            int tierIdx = (int)tier;
            w.WageDiscount = tierIdx * 0.08f; // Max 40% discount at tier 5
            w.XpMultiplier = 1.0f + (tierIdx * 0.15f); // Max +75% XP at tier 5
            w.SpeedBonus = tierIdx * 0.04f; // Max +20% speed at tier 5

            switch (tier)
            {
                case CareerTier.Rebel:
                    ActivateRegionalFear(w);
                    Notify(w, $"[Ä°SYANCI] {w.Name} bir 'Ä°syancÄ±' olarak yÃ¼kseldi! BÃ¶lge korkuyla sarÄ±ldÄ±.", Colors.Yellow);
                    w.IsLordHunting = false;
                    break;

                case CareerTier.FamousBandit:
                    Notify(w, $"[ÃœNLÃœ HAYDUT] {w.Name} artÄ±k bir 'ÃœnlÃ¼ Haydut'! Av BÃ¶lgesi mekaniÄŸi etkin.", Colors.Yellow);
                    UpgradeEquipment(w, LegitimacyLevel.FamousBandit);
                    break;

                case CareerTier.Warlord:
                    AssignPersonalityFromBackstory(w);
                    ActivateBrain(w, rec);
                    ActivateSwarm(w);
                    SetTitle(w, rec, "SavaÅž Lordu");
                    RenameParties(w, $"{w.Name}'In Ordusu");
                    TryActivateBlackMarket(w);
                    TryActivatePropaganda(w);
                    UpgradeEquipment(w, LegitimacyLevel.Warlord);
                    Notify(w, $"[SAVAÅž LORDU] {w.Name} artık bir 'SavaÅž Lordu'! Vassallar ve stratejik sistemler devrede.", Colors.Magenta);
                    break;

                case CareerTier.Taninmis:
                    SetTitle(w, rec, "Sovereign");
                    UpgradeEquipment(w, LegitimacyLevel.Recognized);
                    TrySendAllianceOffer(w, rec);
                    w.IsLordHunting = true;
                    Notify(w, $"[EGEMEN] {w.Name} artık bir 'TanÄ±nmÄ±ÅŸ Egemen'! Lordları avlamaya baÅŸlıyor.", Colors.Magenta);
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[UYARI] {w.Name} egemen bir gÃ¼Ã§ oldu. KrallÄ±klar mÃ¼zakereleri dÃ¼ÅŸÃ¼nÃ¼yor.", Colors.Red));
                    break;

                case CareerTier.Fatih:
                    MakeFatih(w, rec);
                    UpgradeEquipment(w, LegitimacyLevel.Recognized);
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[FATH] {w.Name} 'FATÄ°H' unvanÄ±nÄ± aldÄ±! TÃ¼m krallÄ±klar bÃ¼yÃ¼k bir tehdit altında.", Colors.Red));
                    break;
            }

            // Publish event
            var evt = EventBus.Instance.Get<CareerTierChangedEvent>() ?? new CareerTierChangedEvent();
            evt.Warlord = w;
            evt.PreviousTier = rec.PreviousTier;
            evt.NewTier = tier;
            EventBus.Instance.Publish(evt);
            EventBus.Instance.Return(evt);

            DebugLogger.Info("WarlordCareer", $"[PROMOTION] {w.Name} -> {tier} | Gold={w.Gold:F0}");
        }

        // —— Daily passive effects ———————————————————————————
        public override void OnDailyTick()
        {
            if (!IsEnabled) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;
    
            foreach (var w in WarlordSystem.Instance.GetAllWarlords())
            {
                if (!w.IsAlive) continue;
                var rec = GetOrCreate(w.StringId);

                switch (rec.Tier)
                {
                    case CareerTier.Rebel: PassiveRebel(w); break;
                    case CareerTier.FamousBandit: PassiveFamousBandit(w, rec); break;
                    case CareerTier.Warlord: PassiveWarlord(w, rec); break;
                    case CareerTier.Taninmis: PassiveTaninmis(w, rec); break;
                    case CareerTier.Fatih: PassiveFatih(w); break;
                }
            }

            CheckFatihCondition();
        }

        private static void PassiveRebel(Warlord w)
        {
            if (FearSystem.Instance?.IsEnabled != true || w.AssignedHideout == null) return;
            var hideoutPos = CompatibilityLayer.GetSettlementPosition(w.AssignedHideout);
            foreach (var v in ModuleManager.Instance.VillageCache)
            {
                if (v == null) continue;
                if (hideoutPos.Distance(CompatibilityLayer.GetSettlementPosition(v)) > 50f) continue;
                FearSystem.Instance.ApplyPressureEvent(v, w.StringId, 0.002f, 0f, "Rebel pressure");
            }
        }

        private static void PassiveFamousBandit(Warlord w, CareerRecord rec)
        {
            if (!rec.BrainActivated) ActivateBrain(w, rec);
        }

        private static void PassiveWarlord(Warlord w, CareerRecord rec)
        {
            if (!rec.NameChanged) RenameParties(w, $"{w.Name}'s Forces");
        }

        private static void PassiveTaninmis(Warlord w, CareerRecord rec)
        {
            float daysSince = (float)(CampaignTime.Now - rec.LastPromoTime).ToDays;
            if (!WarlordCareerRules.CanSendAllianceOffer(
                    rec.AllianceOffers, MAX_ALLIANCE_OFFERS, daysSince, ALLIANCE_COOLDOWN_DAYS))
                return;
            TrySendAllianceOffer(w, rec);
        }

        private static void PassiveFatih(Warlord w)
        {
            foreach (var m in w.CommandedMilitias.Where(m => m?.IsActive == true))
                m.Aggressiveness = MathF.Min(1f, m.Aggressiveness + 0.005f);
        }

        // —— Effect helpers ——————————————————————————————————

        private static void ActivateRegionalFear(Warlord w)
        {
            if (FearSystem.Instance?.IsEnabled != true || w.AssignedHideout == null) return;
            var pos = CompatibilityLayer.GetSettlementPosition(w.AssignedHideout);
            foreach (var v in ModuleManager.Instance.VillageCache)
            {
                if (v == null) continue;
                if (pos.Distance(CompatibilityLayer.GetSettlementPosition(v)) > 45f) continue;
                FearSystem.Instance.ApplyPressureEvent(v, w.StringId, 0.08f, 0f, "Rebel emergence");
            }
        }

        private static void AssignPersonalityFromBackstory(Warlord w)
        {
            // Backstory -> personality mapping; skip if already assigned
            if (w.Personality != 0 && w.Personality != PersonalityType.Cunning) return;
            w.Personality = w.Backstory switch
            {
                BackstoryType.BetrayedNoble => PersonalityType.Vengeful,
                BackstoryType.FailedMercenary => PersonalityType.Aggressive,
                BackstoryType.ExiledLeader => PersonalityType.Cunning,
                BackstoryType.VengefulSurvivor => PersonalityType.Vengeful,
                BackstoryType.AmbitionDriven => PersonalityType.Cunning,
                _ => PersonalityType.Aggressive
            };
        }

        private static void ActivateBrain(Warlord w, CareerRecord rec)
        {
            if (rec.BrainActivated) return;
            var brain = ModuleManager.Instance.GetModule<Intelligence.Strategic.BanditBrain>();
            if (brain?.IsEnabled != true) return;
            // Brain tracks warlord via events; UpdatePlayerProfile triggers
            brain.UpdatePlayerProfile();
            rec.BrainActivated = true;
        }

        private static void ActivateSwarm(Warlord w)
        {
            // SwarmCoordinator auto-creates groups via militia spawn events.
            // On transition to Warlord tier, aggressiveness increase makes swarm more aggressive.
            foreach (var m in w.CommandedMilitias.Where(m => m?.IsActive == true))
                m.Aggressiveness = MathF.Min(1f, m.Aggressiveness + 0.15f);
        }

        private static void SetTitle(Warlord w, CareerRecord rec, string title)
        {
            w.Title = title;
            if (w.LinkedHero?.IsAlive != true) { rec.NameChanged = true; return; }
            var nameObj = new TextObject($"{title} {{NAME}}");
            _ = nameObj.SetTextVariable("NAME", w.LinkedHero.FirstName);
            w.LinkedHero.SetName(nameObj, nameObj);
            rec.NameChanged = true;
        }

        private static void RenameParties(Warlord w, string nameTemplate)
        {
            foreach (var m in w.CommandedMilitias)
            {
                if (m?.Party == null || !m.IsActive) continue;
                m.Party.SetCustomName(new TextObject(nameTemplate));
                if (m.PartyComponent is MilitiaPartyComponent comp)
                    comp.WarlordId = w.StringId;
            }
        }

        private static void TryActivateBlackMarket(Warlord w)
        {
            var bm = ModuleManager.Instance.GetModule<BlackMarketSystem>();
            if (bm?.IsEnabled != true) return;
            // BlackMarket already checks warlord on daily tick.
            // On tier transition, convert warlord gold to investment.
            if (w.Gold > 5000) w.Gold -= 2000; // initial investment
        }

        private static void TryActivatePropaganda(Warlord w)
        {
            // PropagandaSystem checks warlord gold in InitiateNewOperations.
            // No extra gold transfer needed for Minor Lord tier transition;
            // system activates on its own. Just log.
            DebugLogger.Info("WarlordCareer", $"[PROPAGANDA] Propaganda system enabled for {w.Name}.");
        }

        private static void UpgradeEquipment(Warlord w, LegitimacyLevel level)
        {
            var enh = BanditEnhancementSystem.Instance;
            if (enh?.IsEnabled != true) return;
            foreach (var m in w.CommandedMilitias.Where(m => m?.IsActive == true))
                enh.EnhanceWarlordParty(m, level, 0f, true);
        }

        private static void TrySendAllianceOffer(Warlord w, CareerRecord rec)
        {
            if (Campaign.Current == null) return;
            var kingdoms = Kingdom.All?
                .Where(k => !k.IsEliminated && k.Leader?.IsAlive == true
                            && k != Hero.MainHero?.Clan?.Kingdom)
                .ToList();
            if (kingdoms == null || kingdoms.Count == 0) return;

            var kingdom = kingdoms[MBRandom.RandomInt(kingdoms.Count)];
            rec.AllianceOffers++;

            InformationManager.DisplayMessage(new InformationMessage(
                $"[Diplomacy] {kingdom.Name} envoys are presenting an alliance offer to {w.Name}...",
                Colors.Cyan));

            var evt = EventBus.Instance.Get<AllianceOfferEvent>() ?? new AllianceOfferEvent();
            evt.Warlord = w;
            evt.KingdomId = kingdom.StringId;
            evt.OfferCount = rec.AllianceOffers;
            EventBus.Instance.Publish(evt);
            EventBus.Instance.Return(evt);
        }

        private static void MakeFatih(Warlord w, CareerRecord rec)
        {
            if (rec.IsFatih) return;
            rec.IsFatih = true;
            w.Name = "The Conqueror";
            w.Title = "The Conqueror";
            if (w.LinkedHero?.IsAlive == true)
                w.LinkedHero.SetName(new TextObject("{=BM_Conqueror}The Conqueror"), new TextObject("{=BM_Conqueror}The Conqueror"));
            RenameParties(w, "The Conqueror's Army");
        }

        // —— Conqueror condition ————————————————————————————————
        private void CheckFatihCondition()
        {
            if (!IsEnabled) return;
            var ml = WarlordSystem.Instance.GetAllWarlords()
                .Where(w => w.IsAlive && GetTier(w.StringId) >= CareerTier.Warlord)
                .ToList();
            if (ml.Count < 2) return;

            float topPts = float.MinValue;
            Warlord? top = null;
            foreach (var w in ml)
            {
                float pts = WarlordLegitimacySystem.Instance.GetPoints(w.StringId);
                if (pts > topPts) { topPts = pts; top = w; }
            }
            if (top == null) return;

            var rec = GetOrCreate(top.StringId);
            if (rec.IsFatih) return;

            // Pass all rival points to Rules
            float[] rivalPts = ml
                .Where(w => w.StringId != top.StringId)
                .Select(w => WarlordLegitimacySystem.Instance.GetPoints(w.StringId))
                .ToArray();

            if (!WarlordCareerRules.IsFatihSupreme(topPts, rivalPts)) return;

            rec.PreviousTier = rec.Tier;
            rec.Tier = CareerTier.Fatih;
            rec.LastPromoTime = CampaignTime.Now;
            ApplyPromotion(top, rec, CareerTier.Fatih);

            var evt = EventBus.Instance.Get<CareerFatihPromotionEvent>();
            evt.Warlord = top;
            EventBus.Instance.Publish(evt);
            EventBus.Instance.Return(evt);
        }

        // —— Event & helpers ———————————————————————————————————
        private void OnWarlordFallen(WarlordFallenEvent evt)
        {
            if (evt?.Warlord != null) _ = _records.Remove(evt.Warlord.StringId);
        }

        public CareerRecord GetOrCreate(string id)
        {
            if (!_records.TryGetValue(id, out var r))
                _records[id] = r = new CareerRecord { WarlordId = id };
            return r;
        }

        public CareerTier GetTier(string id) =>
            _records.TryGetValue(id, out var r) ? r.Tier : CareerTier.Eskiya;

        private static CareerTier LevelToTier(LegitimacyLevel l) => l switch
        {
            LegitimacyLevel.Rebel => CareerTier.Rebel,
            LegitimacyLevel.FamousBandit => CareerTier.FamousBandit,
            LegitimacyLevel.Warlord => CareerTier.Warlord,
            LegitimacyLevel.Recognized => CareerTier.Taninmis,
            _ => CareerTier.Eskiya
        };

        private static void Notify(Warlord w, string msg, Color color)
        {
            if (Settings.Instance?.ShowTestMessages != false)
                InformationManager.DisplayMessage(new InformationMessage(msg, color));
            DebugLogger.Info("WarlordCareer", msg);
        }

        public override void SyncData(IDataStore ds)
        {
            _ = ds.SyncData("_careerRecords_v1", ref _records);
            if (ds.IsLoading) _records ??= new();
        }

        public override string GetDiagnostics()
        {
            var dist = _records.Values.GroupBy(r => r.Tier)
                .Select(g => $"{g.Key}:{g.Count()}");
            return $"WarlordCareer: {_records.Count} | {string.Join(", ", dist)}";
        }
    }

    // ── WarlordCareerRules (inline) ──────────────────────────────
    /// <summary>
    /// WarlordCareerSystem için Bannerlord bağımsız pure logic.
    /// Tier hesaplamaları, Fatih koşulu ve kariyer geçiş kuralları.
    /// </summary>
    public static class WarlordCareerRules
    {
        // ── Kariyer eşikleri (LegitimacySystem ile senkron) ──────────
        public const float THRESH_REBEL = 300f;
        public const float THRESH_FAMOUS = 800f;
        public const float THRESH_WARLORD = 1500f;
        public const float THRESH_TANINMIS = 2500f;

        // Fatih — diğerlerinin en az bu oranı kadar geride olmalı
        public const float FATIH_SUPREMACY_RATIO = 0.67f;

        // Terfi bonusları
        public const float GOLD_REBEL = 2_000f;
        public const float GOLD_FAMOUS = 5_000f;
        public const float GOLD_WARLORD = 10_000f;
        public const float GOLD_TANINMIS = 20_000f;
        public const float GOLD_FATIH = 50_000f;

        // ── Tier dönüşümü ─────────────────────────────────────────────

        /// <summary>LegitimacyLevel → CareerTier dönüşümü.</summary>
        public static CareerTier LevelToTier(LegitimacyLevel level) => level switch
        {
            LegitimacyLevel.Rebel => CareerTier.Rebel,
            LegitimacyLevel.FamousBandit => CareerTier.FamousBandit,
            LegitimacyLevel.Warlord => CareerTier.Warlord,
            LegitimacyLevel.Recognized => CareerTier.Taninmis,
            _ => CareerTier.Eskiya
        };

        /// <summary>Tier terfiye uygun mu? (geriye gidiş yok)</summary>
        public static bool IsValidPromotion(CareerTier current, CareerTier proposed)
            => proposed > current;

        // ── Fatih koşulu ──────────────────────────────────────────────

        /// <summary>
        /// Verilen aday, diğer tüm rakiplerini FATIH_SUPREMACY_RATIO kadar geçiyor mu?
        /// </summary>
        public static bool IsFatihSupreme(float topPoints, float[] rivalPoints)
        {
            if (rivalPoints == null || rivalPoints.Length == 0) return false;
            foreach (float p in rivalPoints)
                if (p >= topPoints * FATIH_SUPREMACY_RATIO) return false;
            return true;
        }

        // ── Terfi altın bonusu ────────────────────────────────────────

        /// <summary>Tier terfisinde kazanılan altın miktarı.</summary>
        public static float GetPromoGold(CareerTier tier) => tier switch
        {
            CareerTier.Rebel => GOLD_REBEL,
            CareerTier.FamousBandit => GOLD_FAMOUS,
            CareerTier.Warlord => GOLD_WARLORD,
            CareerTier.Taninmis => GOLD_TANINMIS,
            CareerTier.Fatih => GOLD_FATIH,
            _ => 0f
        };

        // ── İttifak teklif cooldown ───────────────────────────────────

        /// <summary>
        /// Belirtilen teklif numarası için gün cinsinden bekleme süresi.
        /// </summary>
        public static float GetAllianceCooldownDays(int offerNumber, float baseDays = 30f)
            => baseDays * Math.Max(1, offerNumber + 1);

        /// <summary>İttifak teklifi yapılabilir mi?</summary>
        public static bool CanSendAllianceOffer(int offerCount, int maxOffers, float daysSincePromo, float baseCooldown)
            => offerCount < maxOffers
            && daysSincePromo >= GetAllianceCooldownDays(offerCount, baseCooldown);
    }

    // ── WarlordEconomyPolicy (inline) ──────────────────────────────

    public interface IWarlordEconomyPolicy
    {
        float GetRaidTreasuryPenalty(float currentGold);
        float GetExpansionChance(float baseChance, int activeNetworks);
    }

    public sealed class DefaultWarlordEconomyPolicy : IWarlordEconomyPolicy
    {
        public float GetRaidTreasuryPenalty(float currentGold)
        {
            if (currentGold > 60000f) return 0.65f;
            if (currentGold > 30000f) return 0.80f;
            return 1f;
        }

        public float GetExpansionChance(float baseChance, int activeNetworks)
        {
            float chance = baseChance / (1f + Math.Max(0, activeNetworks) * 0.75f);
            return Math.Max(0.01f, chance);
        }
    }

    public static class WarlordEconomyPolicy
    {
        public static IWarlordEconomyPolicy Current { get; set; } = new DefaultWarlordEconomyPolicy();
    }

}
