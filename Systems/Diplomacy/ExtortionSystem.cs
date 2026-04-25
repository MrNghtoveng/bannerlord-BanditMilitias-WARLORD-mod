using BanditMilitias.Core.Events;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Core.Neural;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace BanditMilitias.Systems.Diplomacy
{
    [BanditMilitias.Core.Components.AutoRegister]
    public class ExtortionSystem : BanditMilitias.Core.Components.MilitiaModuleBase
    {
        private static ExtortionSystem? _instance;
        public static ExtortionSystem Instance => _instance ??= new ExtortionSystem();

        public override string ModuleName => "ExtortionSystem";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 50;

        private Dictionary<string, CampaignTime> _extortionCooldowns = new Dictionary<string, CampaignTime>();

        private ExtortionSystem() { }

        public override void Initialize() { }
        public override void OnTick(float deltaTime) { }
        public override void OnHourlyTick() { }
        public override void OnDailyTick()
        {
            CleanupCooldowns();
        }

        public override void Cleanup()
        {
            _extortionCooldowns.Clear();
        }

        public override string GetDiagnostics()
        {
            return $"Active Cooldowns: {_extortionCooldowns.Count}";
        }

        public override void SyncData(IDataStore dataStore)
        {
            _ = dataStore.SyncData("_extortionCooldowns", ref _extortionCooldowns);
        }

        public bool CanExtort(Settlement settlement)
        {
            if (!Infrastructure.CompatibilityLayer.IsGameplayActivationSwitchClosed())
                return false;

            if (settlement == null) return false;

            if (!settlement.IsVillage) return false;

            if (_extortionCooldowns.TryGetValue(settlement.StringId, out var releaseTime))
            {
                if (CampaignTime.Now < releaseTime) return false;
            }

            return true;
        }

        public int CalculateTributeAmount(Settlement settlement, Hero warlordHero)
        {
            if (settlement?.Village == null) return 0;

            float bribeCost = Settings.Instance?.BribeCostPerMan ?? 50f;
            float baseAmount = settlement.Village.Hearth * (bribeCost / 100f);
            float prosperity = settlement.Village.Bound?.Town?.Prosperity ?? 3000f;
            float prosperityFactor = TaleWorlds.Library.MathF.Clamp(prosperity / 4000f, 0.60f, 1.40f);

            if (warlordHero != null)
            {
                _ = WarlordSystem.Instance.GetWarlordForHideout(warlordHero.CurrentSettlement);

                float power = warlordHero.PartyBelongedTo != null ? CompatibilityLayer.GetTotalStrength(warlordHero.PartyBelongedTo) : 0;
                baseAmount += TaleWorlds.Library.MathF.Min(350f, power * 0.8f);
            }

            int total = (int)(baseAmount * prosperityFactor);
            return (int)TaleWorlds.Library.MathF.Clamp(total, 120, 2500);
        }

        public bool WillYield(Hero warlordHero, Settlement targetVillage)
        {
            if (warlordHero == null || targetVillage == null) return false;
            
            float intimidation = CalculateIntimidation(warlordHero, targetVillage);
            float resistance = CalculateResistance(targetVillage);

            float normalizedIntimidation = Math.Min(1f, intimidation / 800f);
            float normalizedResistance = Math.Min(1f, resistance / 400f);
            float roll = MBRandom.RandomFloat;

            return (normalizedIntimidation * 0.6f + roll * 0.4f) > normalizedResistance;
        }

        public int ExecuteExtortion(Hero warlordHero, Settlement targetVillage)
        {
            if (warlordHero == null || targetVillage == null || targetVillage.Village == null) return 0;

            int demand = CalculateTributeAmount(targetVillage, warlordHero);
            
            int cityGold = targetVillage.Village.Bound?.Town?.Gold ?? 0;
            int payment = Math.Min(cityGold > 0 ? cityGold : demand, demand);

            GiveGoldAction.ApplyBetweenCharacters(null, warlordHero, payment);

            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(warlordHero, targetVillage.OwnerClan.Leader, -10);

            TextObject msg = new TextObject("{=BM_Extort_Success}[Extortion] The village yields! {GOLD} gold secured.");
            _ = msg.SetTextVariable("GOLD", payment);
            InformationManager.DisplayMessage(new InformationMessage(msg.ToString(), Colors.Green));

            try
            {
                var warlord = WarlordSystem.Instance.GetWarlordForHero(warlordHero);
                if (warlord != null)
                {
                    BanditMilitias.Systems.Progression.WarlordLegitimacySystem.Instance.OnSuccessfulExtortion(warlord, targetVillage);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("Extortion", $"Legitimacy update failed: {ex.Message}");
            }

            _extortionCooldowns[targetVillage.StringId] = CampaignTime.DaysFromNow(7f);

            // Haraç başarılı → TributeCollectedEvent
            try
            {
                var tribEvt = EventBus.Instance.Get<BanditMilitias.Core.Events.TributeCollectedEvent>();
                if (tribEvt != null)
                {
                    var tw = WarlordSystem.Instance.GetWarlordForHero(warlordHero);
                    tribEvt.WarlordId = tw?.StringId;
                    tribEvt.Village = targetVillage;
                    tribEvt.Amount = payment;
                    NeuralEventRouter.Instance.Publish(tribEvt);
                    EventBus.Instance.Return(tribEvt);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("Extortion", $"Event publishing failed: {ex.Message}");
            }

            return payment;
        }

        public void ExecuteRefusal(Hero warlordHero, Settlement targetVillage)
        {
            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(warlordHero, targetVillage.OwnerClan.Leader, -20);
            InformationManager.DisplayMessage(new InformationMessage(new TextObject("{=BM_Extort_Fail}[Extortion] The villagers refuse! 'Come and take it if you dare!'").ToString(), Colors.Red));

            _extortionCooldowns[targetVillage.StringId] = CampaignTime.DaysFromNow(3f);
        }

        private float CalculateIntimidation(Hero warlord, Settlement village)
        {
            float power = warlord.PartyBelongedTo != null ? CompatibilityLayer.GetTotalStrength(warlord.PartyBelongedTo) : 0;
            float roguery = warlord.GetSkillValue(DefaultSkills.Roguery);
            return power + (roguery * 0.5f);
        }

        private float CalculateResistance(Settlement village)
        {
            float militia = village.Militia * 10f;
            float loyalty = village.Village.Bound.Town?.Loyalty ?? 50f;
            return militia + loyalty;
        }

        private void CleanupCooldowns()
        {
            List<string> toRemove = new List<string>();
            foreach (var kvp in _extortionCooldowns)
            {
                if (CampaignTime.Now > kvp.Value)
                {
                    toRemove.Add(kvp.Key);
                }
            }
            foreach (var key in toRemove)
            {
                _ = _extortionCooldowns.Remove(key);
            }
        }
    }
}
