using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Systems.Fear;
using BanditMilitias.Intelligence.Strategic;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Systems.WarlordLegitimacy
{
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

    [BanditMilitias.Core.Components.ModuleDependency(
        typeof(BanditMilitias.Intelligence.Strategic.WarlordSystem),
        typeof(BanditMilitias.Systems.Fear.FearSystem),
        typeof(BanditMilitias.Systems.Progression.AscensionEvaluator))]
    [BanditMilitias.Core.Components.AutoRegister(Priority = 65, IsCritical = true)]
    public class WarlordLegitimacySystem : MilitiaModuleBase
    {
        public override string ModuleName => "LegitimacySystem";
        public override bool IsEnabled => Settings.Instance?.EnableLegitimacySystem ?? true;
        public override int Priority => 65;

        private static readonly Lazy<WarlordLegitimacySystem> _instance =
            new Lazy<WarlordLegitimacySystem>(() => new WarlordLegitimacySystem());
        public static WarlordLegitimacySystem Instance => _instance.Value;

        private Dictionary<string, WarlordLegitimacyRecord> _records = new();

        public static float THRESH_REBEL => Progression.WarlordCareerRules.THRESH_REBEL;
        public static float THRESH_FAMOUS => Progression.WarlordCareerRules.THRESH_FAMOUS;
        public static float THRESH_WARLORD => Progression.WarlordCareerRules.THRESH_WARLORD;
        public static float THRESH_RECOGNIZED => Progression.WarlordCareerRules.THRESH_RECOGNIZED;

        private const int MIN_GOLD_FAMOUS = 15000;
        private const int MIN_TROOPS_FAMOUS = 45;
        private const int MIN_GOLD_WARLORD = 40000;
        private const int MIN_TROOPS_WARLORD = 110;
        private const int MIN_GOLD_RECOGNIZED = 150000;
        private const int MIN_TROOPS_RECOGNIZED = 220;

        public struct PromotionDrive
        {
            public float WealthDrive;
            public float PowerDrive;
            public float HonorDrive;
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

            BanditMilitias.Core.Events.EventBus.Instance.Subscribe<WarlordBetrayedEvent>(OnWarlordBetrayed);
            DebugLogger.Info("LegitimacySystem", "WarlordLegitimacySystem initialized.");
        }

        public override void Cleanup()
        {
            BanditMilitias.Core.Events.EventBus.Instance.Unsubscribe<WarlordBetrayedEvent>(OnWarlordBetrayed);
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
            if (ModActivationManager.IsGameplayActivationDelayed()) return;

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
                Progression.AscensionEvaluator.Instance.RecalculateDaily(warlord);
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
                LegitimacyLevel.Recognized => THRESH_RECOGNIZED,
                _ => 10000f
            };
            int targetGold = drive.NextLevel switch {
                LegitimacyLevel.FamousBandit => MIN_GOLD_FAMOUS,
                LegitimacyLevel.Warlord => MIN_GOLD_WARLORD,
                LegitimacyLevel.Recognized => MIN_GOLD_RECOGNIZED,
                _ => 0
            };
            int targetTroops = drive.NextLevel switch {
                LegitimacyLevel.FamousBandit => Progression.AscensionEvaluator.Instance.GetDynamicTroopRequirement(warlord, MIN_TROOPS_FAMOUS),
                LegitimacyLevel.Warlord => Progression.AscensionEvaluator.Instance.GetDynamicTroopRequirement(warlord, MIN_TROOPS_WARLORD),
                LegitimacyLevel.Recognized => Progression.AscensionEvaluator.Instance.GetDynamicTroopRequirement(warlord, MIN_TROOPS_RECOGNIZED),
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

            if (Settings.Instance?.TestingMode == true)
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

            int dynFamous = Progression.AscensionEvaluator.Instance.GetDynamicTroopRequirement(warlord, MIN_TROOPS_FAMOUS);
            int dynWarlord = Progression.AscensionEvaluator.Instance.GetDynamicTroopRequirement(warlord, MIN_TROOPS_WARLORD);
            int dynRecognized = Progression.AscensionEvaluator.Instance.GetDynamicTroopRequirement(warlord, MIN_TROOPS_RECOGNIZED);

            bool isWarrior_Rec = (pts >= THRESH_RECOGNIZED * 0.8f && troops >= dynRecognized);
            bool isMerchant_Rec = (pts >= THRESH_RECOGNIZED * 0.6f && gold >= MIN_GOLD_RECOGNIZED);
            bool isDiplomat_Rec = (pts >= THRESH_RECOGNIZED * 1.5f && (gold >= MIN_GOLD_RECOGNIZED / 2 || troops >= dynRecognized / 2));

            bool isWarrior_War = (pts >= THRESH_WARLORD * 0.8f && troops >= dynWarlord);
            bool isMerchant_War = (pts >= THRESH_WARLORD * 0.6f && gold >= MIN_GOLD_WARLORD);
            bool isDiplomat_War = (pts >= THRESH_WARLORD * 1.4f && (gold >= MIN_GOLD_WARLORD / 2 || troops >= dynWarlord / 2));

            bool isWarrior_Fam = (pts >= THRESH_FAMOUS * 0.8f && troops >= dynFamous);
            bool isMerchant_Fam = (pts >= THRESH_FAMOUS * 0.6f && gold >= MIN_GOLD_FAMOUS);
            bool isDiplomat_Fam = (pts >= THRESH_FAMOUS * 1.3f && (gold >= MIN_GOLD_FAMOUS / 2 || troops >= dynFamous / 2));

            if (isWarrior_Rec || isMerchant_Rec || isDiplomat_Rec) newLevel = LegitimacyLevel.Recognized;
            else if (isWarrior_War || isMerchant_War || isDiplomat_War) newLevel = LegitimacyLevel.Warlord;
            else if (isWarrior_Fam || isMerchant_Fam || isDiplomat_Fam) newLevel = LegitimacyLevel.FamousBandit;
            else if (pts >= THRESH_REBEL) newLevel = LegitimacyLevel.Rebel;
            else newLevel = LegitimacyLevel.Outlaw;

            if (newLevel > oldLevel && !Progression.AscensionEvaluator.Instance.CanPromote(warlord, newLevel))
                newLevel = oldLevel;

            if (newLevel != oldLevel)
            {
                Progression.AscensionEvaluator.Instance.OnPromotionGranted(warlord, newLevel);
                record.Level = newLevel;
                record.LastLevelChangeTime = CampaignTime.Now;
                ApplyMilitiaBannerPrestige(warlord, newLevel);

                string titleId = newLevel switch
                {
                    LegitimacyLevel.Rebel => "BM_Title_Captain",
                    LegitimacyLevel.FamousBandit => "BM_Title_Famous",
                    LegitimacyLevel.Warlord => "BM_Title_Warlord",
                    LegitimacyLevel.Recognized => "BM_Title_Sovereign",
                    _ => "BM_Title_Outlaw"
                };

                if (Settings.Instance?.TestingMode == true)
                {
                    TextObject msg = new TextObject("{=BM_Legitimacy_Promotion}Dark Politics: {NAME} has reached the status of {TITLE}!");
                    _ = msg.SetTextVariable("NAME", warlord.Name);
                    _ = msg.SetTextVariable("TITLE", new TextObject("{=" + titleId + "}"));
                    InformationManager.DisplayMessage(new InformationMessage(msg.ToString(), Colors.Magenta));
                }

                var evt = BanditMilitias.Core.Events.EventBus.Instance.Get<WarlordLevelChangedEvent>();
                evt.Warlord = warlord;
                evt.OldLevel = oldLevel;
                evt.NewLevel = newLevel;
                Core.Neural.NeuralEventRouter.Instance.Publish(evt);
                BanditMilitias.Core.Events.EventBus.Instance.Return(evt);
            }
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
                if (militia?.PartyComponent is Components.MilitiaPartyComponent comp)
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
                LegitimacyLevel.Warlord => (current - THRESH_WARLORD) / (THRESH_RECOGNIZED - THRESH_WARLORD),
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
}


