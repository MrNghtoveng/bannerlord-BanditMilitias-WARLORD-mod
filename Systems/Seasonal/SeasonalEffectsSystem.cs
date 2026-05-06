using BanditMilitias.Core.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Fear;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Seasonal
{
    public enum MilitiaSeason
    {
        Spring,

        Summer,

        Autumn,

        Winter

    }


    [BanditMilitias.Core.Components.AutoRegister(Priority = 320, IsCritical = false)]
    public class SeasonalEffectsSystem : MilitiaModuleBase
    {
        public override string ModuleName => "SeasonalEffectsSystem";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 20;


        private static readonly Lazy<SeasonalEffectsSystem> _instance =
            new Lazy<SeasonalEffectsSystem>(() => new SeasonalEffectsSystem());
        public static SeasonalEffectsSystem Instance => _instance.Value;

        private MilitiaSeason _currentSeason = MilitiaSeason.Spring;
        private int _lastSeasonDay = -1;


        public float RaidLootMultiplier { get; private set; } = 1.0f;
        public float SpeedMultiplier { get; private set; } = 1.0f;
        public float WinterAttritionRisk { get; private set; } = 0f;
        public float FearDecayBonus { get; private set; } = 0f;


        public MilitiaSeason CurrentSeason => _currentSeason;

        private SeasonalEffectsSystem() { }

        public override void Initialize()
        {
            UpdateSeason();
            DebugLogger.Info("Seasonal", $"SeasonalEffectsSystem initialized. Season: {_currentSeason}");
        }

        public override void Cleanup() { }

        public override void SyncData(IDataStore dataStore)
        {


        }

        public override void OnDailyTick()
        {
            if (!IsEnabled) return;
            if (ModActivationManager.IsGameplayActivationDelayed()) return;

            UpdateSeason();

            if (_currentSeason == MilitiaSeason.Winter)
                ProcessWinterAttrition();
        }

        private void UpdateSeason()
        {
            int dayOfYear = GetDayOfYear();
            if (dayOfYear == _lastSeasonDay) return;
            _lastSeasonDay = dayOfYear;


            int seasonIndex = (dayOfYear / 21) % 4;
            var newSeason = (MilitiaSeason)seasonIndex;

            if (newSeason != _currentSeason)
            {
                var old = _currentSeason;
                _currentSeason = newSeason;
                ApplySeasonParameters(newSeason);
                OnSeasonChanged(old, newSeason);
            }
            else if (_lastSeasonDay == 0)
            {


                ApplySeasonParameters(_currentSeason);
            }
        }

        private void ApplySeasonParameters(MilitiaSeason season)
        {
            switch (season)
            {
                case MilitiaSeason.Spring:
                    RaidLootMultiplier = 1.0f;
                    SpeedMultiplier = 1.0f;
                    WinterAttritionRisk = 0f;
                    FearDecayBonus = 0f;
                    break;

                case MilitiaSeason.Summer:
                    RaidLootMultiplier = 0.9f;

                    SpeedMultiplier = 1.15f;

                    WinterAttritionRisk = 0f;
                    FearDecayBonus = 0f;
                    break;

                case MilitiaSeason.Autumn:
                    RaidLootMultiplier = 1.25f;

                    SpeedMultiplier = 1.0f;
                    WinterAttritionRisk = 0f;
                    FearDecayBonus = 0f;
                    break;

                case MilitiaSeason.Winter:
                    RaidLootMultiplier = 0.7f;

                    SpeedMultiplier = 0.88f;

                    WinterAttritionRisk = 0.03f;

                    FearDecayBonus = 0.01f;

                    break;
            }
        }

        private void OnSeasonChanged(MilitiaSeason old, MilitiaSeason newSeason)
        {
            string message = newSeason switch
            {
                MilitiaSeason.Spring  => "☀ Spring has arrived — militias are recovering.",
                MilitiaSeason.Summer  => "☀ Summer has started — militias move faster.",
                MilitiaSeason.Autumn  => "🍂 Autumn — villages are full of harvest, raid season is open!",
                MilitiaSeason.Winter  => "❄ Winter has struck — a tough period for militias begins.",
                _ => ""
            };

            if (!string.IsNullOrEmpty(message))
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Season] {message}",
                    newSeason == MilitiaSeason.Winter ? Colors.White : new Color(0.8f, 0.9f, 0.4f)));
            }

            DebugLogger.Info("Seasonal", $"Season changed: {old} → {newSeason}. " +
                $"Raid={RaidLootMultiplier:F2}, Speed={SpeedMultiplier:F2}, Winter={WinterAttritionRisk:F2}");
        }

        private void ProcessWinterAttrition()
        {


            foreach (var party in Infrastructure.CompatibilityLayer.GetSafeMobileParties())
            {
                if (party.PartyComponent is not Components.MilitiaPartyComponent comp) continue;

                int total = party.MemberRoster.TotalManCount;
                if (total < 5) continue;

                if (MBRandom.RandomFloat < WinterAttritionRisk)
                {
                    int loss = Math.Max(1, (int)(total * 0.02f));
                    try
                    {


                        var troopToKill = party.MemberRoster.GetTroopRoster()
                            .Where(e => !e.Character.IsHero && e.Number > 0)
                            .OrderBy(e => e.Character.Tier)
                            .FirstOrDefault();

                        if (troopToKill.Character != null)
                        {
                            int actualLoss = Math.Min(loss, troopToKill.Number);
                            party.MemberRoster.AddToCounts(troopToKill.Character, -actualLoss);

                            if (Settings.Instance?.TestingMode == true)
                            {
                                DebugLogger.Info("Seasonal",
                                    $"[Winter Attrition] {party.Name}: {actualLoss} troops froze.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Warning("Seasonal", $"Winter attrition error: {ex.Message}");
                    }
                }
            }
        }


        private static int GetDayOfYear()
        {
            try
            {
                double totalDays = CampaignTime.Now.ToDays;
                return (int)(totalDays % 84);
            }
            catch
            {
                return 0;
            }
        }

        public string GetSeasonDescription()
        {
            return _currentSeason switch
            {
                MilitiaSeason.Spring => "Spring — Normal conditions",
                MilitiaSeason.Summer => $"Summer — Speed +%{(SpeedMultiplier - 1f) * 100:F0}",
                MilitiaSeason.Autumn => $"Autumn — Raid income +%{(RaidLootMultiplier - 1f) * 100:F0}",
                MilitiaSeason.Winter => "Winter — Troop loss risk, slow movement",
                _ => "Unknown"
            };
        }

        public override string GetDiagnostics()
        {
            return $"SeasonalEffects:\n" +
                   $"  Season: {_currentSeason} ({GetSeasonDescription()})\n" +
                   $"  RaidMultiplier: {RaidLootMultiplier:F2}\n" +
                   $"  SpeedMultiplier: {SpeedMultiplier:F2}\n" +
                   $"  WinterAttrition: {WinterAttritionRisk:P0}";
        }
    }
}


