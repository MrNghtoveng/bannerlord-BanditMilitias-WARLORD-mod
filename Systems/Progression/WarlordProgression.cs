using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.WarlordLegitimacy;
using BanditMilitias.Systems.Fear;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Systems.Progression
{
    [Serializable]
    public class CareerRecord
    {
        [SaveableProperty(1)] public string WarlordId { get; set; } = "";
        [SaveableProperty(2)] public CareerTier Tier { get; set; } = CareerTier.Outlaw;
        [SaveableProperty(3)] public CareerTier PreviousTier { get; set; } = CareerTier.Outlaw;
        [SaveableProperty(4)] public CampaignTime LastPromoTime { get; set; } = CampaignTime.Zero;
        [SaveableProperty(5)] public int AllianceOffers { get; set; } = 0;
        [SaveableProperty(6)] public bool BrainActivated { get; set; } = false;
        [SaveableProperty(7)] public bool NameChanged { get; set; } = false;
        [SaveableProperty(8)] public bool IsConqueror { get; set; } = false;
    }

    public enum CareerTier
    {
        Outlaw = 0, Rebel = 1, FamousBandit = 2,
        Warlord = 3, Recognized = 4, Conqueror = 5
    }

    [BanditMilitias.Core.Components.ModuleDependency(
        typeof(BanditMilitias.Intelligence.Strategic.WarlordSystem),
        typeof(BanditMilitias.Systems.WarlordLegitimacy.WarlordLegitimacySystem),
        typeof(BanditMilitias.Systems.Fear.FearSystem),
        typeof(BanditMilitias.Intelligence.Strategic.BanditBrain),
        typeof(BanditMilitias.Systems.Enhancement.BanditEnhancementSystem),
        typeof(BanditMilitias.Systems.Economy.BlackMarketSystem),
        typeof(BanditMilitias.Systems.Workshop.WarlordWorkshopSystem))]
    [BanditMilitias.Core.Components.AutoRegister(Priority = 88, IsCritical = true, IsSingleton = true)]
    public class WarlordCareerSystem : MilitiaModuleBase
    {
        public override string ModuleName => "WarlordCareer";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 88;

        private static readonly Lazy<WarlordCareerSystem> _inst = new(() => new WarlordCareerSystem());
        public static WarlordCareerSystem Instance => _inst.Value;

        private Dictionary<string, CareerRecord> _records = new();
        private bool _isCareerInitialized = false;

        private WarlordCareerSystem() { }

        public override void Initialize()
        {
            if (_isCareerInitialized) return;

            BanditMilitias.Core.Events.EventBus.Instance.Subscribe<WarlordFallenEvent>(OnWarlordFallen);
            BanditMilitias.Core.Events.EventBus.Instance.Subscribe<WarlordLevelChangedEvent>(OnLevelChanged);

            _isCareerInitialized = true;
        }

        public override void Cleanup()
        {
            BanditMilitias.Core.Events.EventBus.Instance.Unsubscribe<WarlordFallenEvent>(OnWarlordFallen);
            BanditMilitias.Core.Events.EventBus.Instance.Unsubscribe<WarlordLevelChangedEvent>(OnLevelChanged);
            _records.Clear();
            _isCareerInitialized = false;
        }

        private void OnLevelChanged(WarlordLevelChangedEvent evt)
        {
            if (evt?.Warlord == null) return;
            OnTierPromotion(evt.Warlord, evt.NewLevel);
        }

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

        private void ApplyPromotion(Warlord w, CareerRecord rec, CareerTier tier)
        {
            InformationManager.ShowInquiry(new InquiryData(
                new TextObject("{=BM_Inquiry_Promo_Title}Rank Promotion!").ToString(),
                new TextObject("{=BM_Inquiry_Promo_Body}{NAME} has risen to the rank of {TIER}! Calradia is buzzing with this news.")
                    .SetTextVariable("NAME", w.FullName)
                    .SetTextVariable("TIER", new TextObject("{=BM_Tier_" + tier.ToString() + "}" + tier.ToString()))
                    .ToString(),
                true, false, new TextObject("{=BM_Accept}Accept").ToString(), "", null, null
            ));

            w.Gold += WarlordCareerRules.GetPromoGold(tier);

            int tierIdx = (int)tier;
            w.WageDiscount = tierIdx * 0.08f;
            w.XpMultiplier = 1.0f + (tierIdx * 0.15f);
            w.SpeedBonus = tierIdx * 0.04f;

            switch (tier)
            {
                case CareerTier.Rebel:
                    ActivateRegionalFear(w);
                    Notify(w, new TextObject("{=BM_Notify_Rebel}[REBEL] {NAME} has risen as a 'Rebel'! The region is gripped by fear.")
                        .SetTextVariable("NAME", w.Name).ToString(), Colors.Yellow);
                    w.IsLordHunting = false;
                    break;

                case CareerTier.FamousBandit:
                    Notify(w, new TextObject("{=BM_Notify_Famous}[FAMOUS BANDIT] {NAME} is now a 'Famous Bandit'! Hunting Ground mechanic active.")
                        .SetTextVariable("NAME", w.Name).ToString(), Colors.Yellow);
                    UpgradeEquipment(w, LegitimacyLevel.FamousBandit);
                    TryAddWorkshop(w, Workshop.WorkshopType.Fletchery);
                    break;

                case CareerTier.Warlord:
                    AssignPersonalityFromBackstory(w);
                    ActivateBrain(w, rec);
                    ActivateSwarm(w);
                    SetTitle(w, rec, "{=BM_Title_Warlord}Warlord");
                    RenameParties(w, new TextObject("{=BM_Warlord_Army}{NAME}'s Army").SetTextVariable("NAME", w.Name).ToString());
                    TryActivateBlackMarket(w);
                    TryActivatePropaganda(w);
                    TryAddWorkshop(w, Workshop.WorkshopType.WeaponSmith);
                    UpgradeEquipment(w, LegitimacyLevel.Warlord);
                    Notify(w, new TextObject("{=BM_Notify_Warlord}[WARLORD] {NAME} is now a 'Warlord'! Vassals and strategic systems are now active.")
                        .SetTextVariable("NAME", w.Name).ToString(), Colors.Magenta);
                    break;

                case CareerTier.Recognized:
                    SetTitle(w, rec, "{=BM_Title_Sovereign}Sovereign");
                    UpgradeEquipment(w, LegitimacyLevel.Recognized);
                    TrySendAllianceOffer(w, rec);
                    TryAddWorkshop(w, Workshop.WorkshopType.ArmorSmith);
                    TryAddWorkshop(w, Workshop.WorkshopType.HorseBreeder);
                    TryAddWorkshop(w, Workshop.WorkshopType.AlchemyLab);
                    w.IsLordHunting = true;
                    Notify(w, new TextObject("{=BM_Notify_Sovereign}[SOVEREIGN] {NAME} is now a 'Recognized Sovereign'! Starting to hunt lords.")
                        .SetTextVariable("NAME", w.Name).ToString(), Colors.Magenta);
                    break;

                case CareerTier.Conqueror:
                    MakeConqueror(w, rec);
                    UpgradeEquipment(w, LegitimacyLevel.Recognized);
                    TryAddWorkshop(w, Workshop.WorkshopType.SiegeWorks);
                    InformationManager.DisplayMessage(new InformationMessage(
                        new TextObject("{=BM_Notify_Conqueror}[CONQUEROR] {NAME} has claimed the title of 'CONQUEROR'! All kingdoms are under a massive threat.")
                            .SetTextVariable("NAME", w.Name).ToString(), Colors.Red));
                    break;
            }

            var evt = BanditMilitias.Core.Events.EventBus.Instance.Get<CareerTierChangedEvent>();
            evt.Warlord = w;
            evt.OldTier = rec.PreviousTier;
            evt.NewTier = tier;
            Core.Neural.NeuralEventRouter.Instance.Publish(evt);
            BanditMilitias.Core.Events.EventBus.Instance.Return(evt);

            DebugLogger.Info("WarlordCareer", $"[PROMOTION] {w.Name} -> {tier} | Gold={w.Gold:F0}");
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled) return;
            if (ModActivationManager.IsGameplayActivationDelayed()) return;

            foreach (var w in WarlordSystem.Instance.GetAllWarlords())
            {
                if (!w.IsAlive) continue;
                var rec = GetOrCreate(w.StringId);

                switch (rec.Tier)
                {
                    case CareerTier.Rebel: PassiveRebel(w); break;
                    case CareerTier.FamousBandit: PassiveFamousBandit(w, rec); break;
                    case CareerTier.Warlord: PassiveWarlord(w, rec); break;
                    case CareerTier.Recognized: PassiveRecognized(w, rec); break;
                    case CareerTier.Conqueror: PassiveConqueror(w); break;
                }
            }

            CheckConquerorCondition();
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

        private static void PassiveRecognized(Warlord w, CareerRecord rec)
        {
            float daysSince = (float)(CampaignTime.Now - rec.LastPromoTime).ToDays;
            if (!WarlordCareerRules.CanSendAllianceOffer(
                    rec.AllianceOffers, 3, daysSince, 30f))
                return;
            TrySendAllianceOffer(w, rec);
        }

        private static void PassiveConqueror(Warlord w)
        {
            foreach (var m in w.CommandedMilitias.Where(m => m?.IsActive == true))
                m.Aggressiveness = MathF.Min(1f, m.Aggressiveness + 0.005f);
        }

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
            var brain = BanditBrain.Instance;
            if (brain?.IsEnabled != true) return;
            brain.UpdatePlayerProfile();
            rec.BrainActivated = true;
        }

        private static void ActivateSwarm(Warlord w)
        {
            foreach (var m in w.CommandedMilitias.Where(m => m?.IsActive == true))
                m.Aggressiveness = MathF.Min(1f, m.Aggressiveness + 0.15f);
        }

        private static void SetTitle(Warlord w, CareerRecord rec, string title)
        {
            var titleObj = new TextObject(title);
            w.Title = titleObj.ToString();
            if (w.LinkedHero?.IsAlive != true) { rec.NameChanged = true; return; }
            var nameObj = new TextObject("{=BM_Title_Format}{TITLE} {NAME}");
            _ = nameObj.SetTextVariable("TITLE", titleObj);
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
                if (m.PartyComponent is Components.MilitiaPartyComponent comp)
                    comp.WarlordId = w.StringId;
            }
        }

        private static void TryActivateBlackMarket(Warlord w)
        {
            var bm = Economy.BlackMarketSystem.Instance;
            if (bm?.IsEnabled != true) return;
            if (w.Gold > 5000) w.Gold -= 2000;
        }

        private static void TryActivatePropaganda(Warlord w)
        {
            DebugLogger.Info("WarlordCareer", $"[PROPAGANDA] Propaganda system enabled for {w.Name}.");
        }

        private static void TryAddWorkshop(Warlord w, Workshop.WorkshopType type)
        {
            try
            {
                Workshop.WarlordWorkshopSystem.Instance?.AddWorkshop(w.StringId, type);
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("WarlordCareer", $"Failed to open workshop ({type}): {ex.Message}");
            }
        }

        private static void UpgradeEquipment(Warlord w, LegitimacyLevel level)
        {
            var enh = Enhancement.BanditEnhancementSystem.Instance;
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

            var evt = BanditMilitias.Core.Events.EventBus.Instance.Get<AllianceOfferEvent>();
            evt.Warlord = w;
            evt.KingdomId = kingdom.StringId;
            evt.OfferCount = rec.AllianceOffers;
            Core.Neural.NeuralEventRouter.Instance.Publish(evt);
            BanditMilitias.Core.Events.EventBus.Instance.Return(evt);
        }

        private static void MakeConqueror(Warlord w, CareerRecord rec)
        {
            if (rec.IsConqueror) return;
            rec.IsConqueror = true;
            string conquerorStr = new TextObject("{=BM_Conqueror}The Conqueror").ToString();
            w.Name = conquerorStr;
            w.Title = conquerorStr;
            if (w.LinkedHero?.IsAlive == true)
            {
                var nameObj = new TextObject("{=BM_Conqueror}The Conqueror");
                w.LinkedHero.SetName(nameObj, nameObj);
            }
            RenameParties(w, new TextObject("{=BM_Conqueror_Army}The Conqueror's Army").ToString());
        }

        private void CheckConquerorCondition()
        {
            if (!IsEnabled) return;
            var ml = WarlordSystem.Instance.GetAllWarlords()
                .Where(w => GetTier(w.StringId) >= CareerTier.Warlord)
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
            if (rec.IsConqueror) return;

            float[] rivalPts = ml
                .Where(w => w.StringId != top.StringId)
                .Select(w => WarlordLegitimacySystem.Instance.GetPoints(w.StringId))
                .ToArray();

            if (!WarlordCareerRules.IsConquerorSupreme(topPts, rivalPts)) return;

            rec.PreviousTier = rec.Tier;
            rec.Tier = CareerTier.Conqueror;
            rec.LastPromoTime = CampaignTime.Now;
            ApplyPromotion(top, rec, CareerTier.Conqueror);

            var evt = BanditMilitias.Core.Events.EventBus.Instance.Get<CareerConquerorPromotionEvent>();
            evt.Warlord = top;
            Core.Neural.NeuralEventRouter.Instance.Publish(evt);
            BanditMilitias.Core.Events.EventBus.Instance.Return(evt);
        }

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
            _records.TryGetValue(id, out var r) ? r.Tier : CareerTier.Outlaw;

        private static CareerTier LevelToTier(LegitimacyLevel l) => l switch
        {
            LegitimacyLevel.Rebel => CareerTier.Rebel,
            LegitimacyLevel.FamousBandit => CareerTier.FamousBandit,
            LegitimacyLevel.Warlord => CareerTier.Warlord,
            LegitimacyLevel.Recognized => CareerTier.Recognized,
            _ => CareerTier.Outlaw
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

    public static class WarlordCareerRules
    {
        public const float THRESH_REBEL = 300f;
        public const float THRESH_FAMOUS = 800f;
        public const float THRESH_WARLORD = 1500f;
        public const float THRESH_RECOGNIZED = 2500f;
        public const float CONQUEROR_SUPREMACY_RATIO = 0.67f;

        public const float GOLD_REBEL = 2_000f;
        public const float GOLD_FAMOUS = 5_000f;
        public const float GOLD_WARLORD = 10_000f;
        public const float GOLD_RECOGNIZED = 20_000f;
        public const float GOLD_CONQUEROR = 50_000f;

        public static CareerTier LevelToTier(LegitimacyLevel level) => level switch
        {
            LegitimacyLevel.Rebel => CareerTier.Rebel,
            LegitimacyLevel.FamousBandit => CareerTier.FamousBandit,
            LegitimacyLevel.Warlord => CareerTier.Warlord,
            LegitimacyLevel.Recognized => CareerTier.Recognized,
            _ => CareerTier.Outlaw
        };

        public static bool IsValidPromotion(CareerTier current, CareerTier proposed)
            => proposed > current;

        public static bool IsConquerorSupreme(float topPoints, float[] rivalPoints)
        {
            if (rivalPoints == null || rivalPoints.Length == 0) return false;
            foreach (float p in rivalPoints)
                if (p >= topPoints * CONQUEROR_SUPREMACY_RATIO) return false;
            return true;
        }

        public static float GetPromoGold(CareerTier tier) => tier switch
        {
            CareerTier.Rebel => GOLD_REBEL,
            CareerTier.FamousBandit => GOLD_FAMOUS,
            CareerTier.Warlord => GOLD_WARLORD,
            CareerTier.Recognized => GOLD_RECOGNIZED,
            CareerTier.Conqueror => GOLD_CONQUEROR,
            _ => 0f
        };

        public static float GetAllianceCooldownDays(int offerNumber, float baseDays = 30f)
            => baseDays * Math.Max(1, offerNumber + 1);

        public static bool CanSendAllianceOffer(int offerCount, int maxOffers, float daysSincePromo, float baseCooldown)
            => offerCount < maxOffers
            && daysSincePromo >= GetAllianceCooldownDays(offerCount, baseCooldown);
    }

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
