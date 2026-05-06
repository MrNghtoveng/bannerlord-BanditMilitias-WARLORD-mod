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
        Spring,  // İlkbahar — normal
        Summer,  // Yaz — hız +%15, morali yüksek
        Autumn,  // Sonbahar — baskın altın +%25 (hasat dolu köyler)
        Winter   // Kış — asker ölümü riski, korku azalması
    }

    /// <summary>
    /// Mevsimsel Etkiler Sistemi
    /// Bannerlord yılı 84 gündür; her mevsim 21 gün.
    /// Sonbahar: baskın geliri +%25 (hasat mevsimi)
    /// Kış: günlük asker kaybı riski, FearSystem zayıflaması
    /// Yaz: milisya hız bonusu +%15
    /// İlkbahar: toparlanma, normal parametreler
    /// </summary>
    [AutoRegister]
    public class SeasonalEffectsSystem : MilitiaModuleBase
    {
        public override string ModuleName => "SeasonalEffectsSystem";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 20; // Erken başlasın, diğer sistemler çarpanı alsın

        private static readonly Lazy<SeasonalEffectsSystem> _instance =
            new Lazy<SeasonalEffectsSystem>(() => new SeasonalEffectsSystem());
        public static SeasonalEffectsSystem Instance => _instance.Value;

        private MilitiaSeason _currentSeason = MilitiaSeason.Spring;
        private int _lastSeasonDay = -1;

        // Mevsim parametreleri
        public float RaidLootMultiplier { get; private set; } = 1.0f;
        public float SpeedMultiplier { get; private set; } = 1.0f;
        public float WinterAttritionRisk { get; private set; } = 0f;
        public float FearDecayBonus { get; private set; } = 0f; // Kış: korku daha hızlı düşer

        public MilitiaSeason CurrentSeason => _currentSeason;

        private SeasonalEffectsSystem() { }

        public override void Initialize()
        {
            UpdateSeason();
            DebugLogger.Info("Seasonal", $"SeasonalEffectsSystem başlatıldı. Mevsim: {_currentSeason}");
        }

        public override void Cleanup() { }

        public override void SyncData(IDataStore dataStore)
        {
            // Mevsim CampaignTime'dan hesaplanır, kaydetmeye gerek yok
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;

            UpdateSeason();

            if (_currentSeason == MilitiaSeason.Winter)
                ProcessWinterAttrition();
        }

        private void UpdateSeason()
        {
            int dayOfYear = GetDayOfYear();
            if (dayOfYear == _lastSeasonDay) return;
            _lastSeasonDay = dayOfYear;

            // Bannerlord yılı = 84 gün, her mevsim = 21 gün
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
                // İlk başlatma
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
                    RaidLootMultiplier = 0.9f;      // Köyler henüz hasat yapmadı
                    SpeedMultiplier = 1.15f;         // Sıcak yollar, hızlı hareket
                    WinterAttritionRisk = 0f;
                    FearDecayBonus = 0f;
                    break;

                case MilitiaSeason.Autumn:
                    RaidLootMultiplier = 1.25f;      // Hasat dolu ambarlar
                    SpeedMultiplier = 1.0f;
                    WinterAttritionRisk = 0f;
                    FearDecayBonus = 0f;
                    break;

                case MilitiaSeason.Winter:
                    RaidLootMultiplier = 0.7f;       // Kar yolları, az ganimet
                    SpeedMultiplier = 0.88f;         // Ağır kış koşulları
                    WinterAttritionRisk = 0.03f;     // Günde %3 asker kaybı riski
                    FearDecayBonus = 0.01f;          // Halk kışta daha az korkuyor (yurt içi meşgul)
                    break;
            }
        }

        private void OnSeasonChanged(MilitiaSeason old, MilitiaSeason newSeason)
        {
            string message = newSeason switch
            {
                MilitiaSeason.Spring  => "☀ İlkbahar geldi — milisyalar toparlanıyor.",
                MilitiaSeason.Summer  => "☀ Yaz başladı — milisyalar daha hızlı hareket ediyor.",
                MilitiaSeason.Autumn  => "🍂 Sonbahar — köyler hasat dolu, baskın sezonu açıldı!",
                MilitiaSeason.Winter  => "❄ Kış bastırdı — milisyalar için zorlu dönem başlıyor.",
                _ => ""
            };

            if (!string.IsNullOrEmpty(message))
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Mevsim] {message}",
                    newSeason == MilitiaSeason.Winter ? Colors.White : new Color(0.8f, 0.9f, 0.4f)));
            }

            DebugLogger.Info("Seasonal", $"Mevsim değişti: {old} → {newSeason}. " +
                $"Baskın={RaidLootMultiplier:F2}, Hız={SpeedMultiplier:F2}, Kış={WinterAttritionRisk:F2}");
        }

        private void ProcessWinterAttrition()
        {
            // Kış aşınması: her milisya günde %3 oranında asker kaybedebilir
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
                        // En düşük tier askerlerden kayıp ver
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
                                    $"[Kış Aşınması] {party.Name}: {actualLoss} asker dondu.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Warning("Seasonal", $"Kış aşınması hatası: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Bannerlord'da yıl 84 gündür. CampaignTime.Now.ToDays'ten günü hesaplıyoruz.
        /// </summary>
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
                MilitiaSeason.Spring => "İlkbahar — Normal koşullar",
                MilitiaSeason.Summer => $"Yaz — Hız +%{(SpeedMultiplier - 1f) * 100:F0}",
                MilitiaSeason.Autumn => $"Sonbahar — Baskın geliri +%{(RaidLootMultiplier - 1f) * 100:F0}",
                MilitiaSeason.Winter => "Kış — Asker kaybı riski, yavaş hareket",
                _ => "Bilinmeyen"
            };
        }

        public override string GetDiagnostics()
        {
            return $"SeasonalEffects:\n" +
                   $"  Mevsim: {_currentSeason} ({GetSeasonDescription()})\n" +
                   $"  RaidÇarpan: {RaidLootMultiplier:F2}\n" +
                   $"  HızÇarpan: {SpeedMultiplier:F2}\n" +
                   $"  KışAşınma: {WinterAttritionRisk:P0}";
        }
    }
}
