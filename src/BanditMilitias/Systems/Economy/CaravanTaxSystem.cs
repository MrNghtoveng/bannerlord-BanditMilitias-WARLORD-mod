using BanditMilitias.Core.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Fear;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Economy
{
    /// <summary>
    /// Kervan Haracı Sistemi — warlord kontrol ettiği bölgeden geçen kervanları
    /// tespit eder, haraç keser veya saldırı başlatır.
    /// FearSystem entegrasyonu: korku oranı haraç tutarını etkiler.
    /// </summary>
    [AutoRegister]
    public class CaravanTaxSystem : MilitiaModuleBase
    {
        public override string ModuleName => "CaravanTaxSystem";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 55;

        private static readonly Lazy<CaravanTaxSystem> _instance =
            new Lazy<CaravanTaxSystem>(() => new CaravanTaxSystem());
        public static CaravanTaxSystem Instance => _instance.Value;

        private Dictionary<string, CampaignTime> _lastTaxedCaravans = new();
        private Dictionary<string, int> _dailyTaxRevenue = new();

        private const float TAX_RADIUS = 12f;           // Haraç bölgesi yarıçapı (harita birimi)
        private const float BASE_TAX_RATE = 0.08f;      // Kargo değerinin %8'i
        private const float FEAR_TAX_BONUS = 0.06f;     // Max korku bonusu (%6 ek)
        private const float COMPLIANCE_THRESHOLD = 0.35f; // Bu seviyenin altındaki korku direnişe yol açar
        private const int   MIN_TAX_COOLDOWN_DAYS = 2;  // Aynı kervan için minimum bekleme süresi

        private CaravanTaxSystem() { }

        public override void Initialize()
        {
            DebugLogger.Info("CaravanTax", "CaravanTaxSystem başlatıldı.");
        }

        public override void Cleanup()
        {
            _lastTaxedCaravans.Clear();
            _dailyTaxRevenue.Clear();
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("CaravanTax_LastTaxed_v1", ref _lastTaxedCaravans);
            dataStore.SyncData("CaravanTax_DailyRevenue_v1", ref _dailyTaxRevenue);
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled) return;
            if (CompatibilityLayer.IsGameplayActivationDelayed()) return;

            // Eski kayıtları temizle (bellek sızıntısı önleme)
            CleanupStaleRecords();

            // Tüm aktif warlordlar için yakın kervanları tara
            var warlords = WarlordSystem.Instance.GetAllWarlords();
            foreach (var warlord in warlords)
            {
                if (!warlord.IsAlive) continue;
                ProcessWarlordCaravanTax(warlord);
            }
        }

        private void ProcessWarlordCaravanTax(Warlord warlord)
        {
            // Warlord'un aktif milisyalarından birini referans pozisyon olarak al
            var militias = CompatibilityLayer.GetSafeMobileParties()
                .Where(p => p.PartyComponent is Components.MilitiaPartyComponent comp
                         && comp.WarlordId == warlord.StringId)
                .ToList();

            if (militias.Count == 0) return;

            // Kontrol bölgesini tüm milisyaların ortalama konumundan hesapla
            Vec2 controlCenter = Vec2.Zero;
            foreach (var m in militias)
                controlCenter += CompatibilityLayer.GetPartyPosition(m);
            controlCenter *= (1f / militias.Count);

            // Yakındaki kervanları bul
            var nearbyParties = new List<MobileParty>();
            Systems.Grid.SpatialGridSystem.Instance.QueryNearby(controlCenter, TAX_RADIUS, nearbyParties);

            foreach (var caravan in nearbyParties)
            {
                if (!caravan.IsCaravan) continue;
                if (caravan.IsMainParty) continue; // Oyuncunun kervanına dokunma

                string caravanId = caravan.StringId;
                if (WasTaxedRecently(caravanId)) continue;

                TryTaxCaravan(warlord, caravan, controlCenter);
            }
        }

        private void TryTaxCaravan(Warlord warlord, MobileParty caravan, Vec2 controlCenter)
        {
            // Yakınlık kontrolü: kervan gerçekten bu bölgede mi?
            Vec2 caravanPos = CompatibilityLayer.GetPartyPosition(caravan);
            float dist = caravanPos.Distance(controlCenter);
            if (dist > TAX_RADIUS) return;

            // Fear bazlı haraç oranı hesapla
            // Yakın yerleşimlerin korku ortalamasını kullan
            float avgFear = GetNearbySettlementFear(warlord.StringId, controlCenter);
            float taxRate = BASE_TAX_RATE + avgFear * FEAR_TAX_BONUS;

            // Kervan değerini tahmin et (parti büyüklüğü * faktör)
            int caravanValue = EstimateCaravanValue(caravan);
            int taxAmount = (int)(caravanValue * taxRate);
            taxAmount = Math.Max(taxAmount, 50); // Minimum 50 altın

            // Düşük korku = direnç riski
            bool caravanComplies = avgFear >= COMPLIANCE_THRESHOLD
                || MBRandom.RandomFloat < avgFear * 2f;

            if (caravanComplies)
            {
                // Kervan boyun eğiyor — haraç ödüyor
                ApplyCaravanTax(warlord, caravan, taxAmount, avgFear);
            }
            else
            {
                // Kervan direniyor — en yakın milisyayı saldırıya yönlendir
                TriggerCaravanAttack(warlord, caravan, controlCenter);
            }

            // Kayıt et
            _lastTaxedCaravans[caravan.StringId] = CampaignTime.Now;
        }

        private void ApplyCaravanTax(Warlord warlord, MobileParty caravan, int taxAmount, float fearLevel)
        {
            // Altın transferi
            warlord.Gold += taxAmount;

            // Yakın yerleşimlere küçük korku etkisi — "warlord haraca bağlıyor" haberi yayılıyor
            ApplyFearRipple(warlord.StringId, CompatibilityLayer.GetPartyPosition(caravan), 0.01f);

            // Gün içi gelir kaydı
            if (!_dailyTaxRevenue.ContainsKey(warlord.StringId))
                _dailyTaxRevenue[warlord.StringId] = 0;
            _dailyTaxRevenue[warlord.StringId] += taxAmount;

            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("CaravanTax",
                    $"[HARAÇ] {warlord.Name} → {caravan.Name}: {taxAmount} altın " +
                    $"(korku={fearLevel:F2}, oran={BASE_TAX_RATE + fearLevel * FEAR_TAX_BONUS:P1})");
            }

            // Oyuncuya bildirim (sadece oyuncunun yakınındaki işlemler için)
            if (IsNearPlayer(CompatibilityLayer.GetPartyPosition(caravan), 20f))
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Haraç] {warlord.FullName}, {caravan.Name} kervanından {taxAmount} altın haraç kesti.",
                    new Color(0.9f, 0.7f, 0.2f)));
            }
        }

        private void TriggerCaravanAttack(Warlord warlord, MobileParty caravan, Vec2 controlCenter)
        {
            // En yakın milisyayı saldırıya yönlendir
            var closestMilitia = CompatibilityLayer.GetSafeMobileParties()
                .Where(p => p.PartyComponent is Components.MilitiaPartyComponent comp
                         && comp.WarlordId == warlord.StringId
                         && p.MemberRoster.TotalManCount > 20)
                .OrderBy(p => CompatibilityLayer.GetPartyPosition(p).Distance(
                              CompatibilityLayer.GetPartyPosition(caravan)))
                .FirstOrDefault();

            if (closestMilitia != null)
            {
                CompatibilityLayer.SetMoveEngageParty(closestMilitia, caravan);

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Info("CaravanTax",
                        $"[SALDIRI] {warlord.Name} → {caravan.Name} direndi, {closestMilitia.Name} saldırıya yönlendi.");
                }
            }
        }

        private float GetNearbySettlementFear(string warlordId, Vec2 position)
        {
            float totalFear = 0f;
            int count = 0;

            foreach (var settlement in Settlement.All)
            {
                if (!settlement.IsVillage && !settlement.IsTown && !settlement.IsCastle) continue;
                Vec2 sPos = CompatibilityLayer.GetSettlementPosition(settlement);
                if (sPos.Distance(position) > TAX_RADIUS * 1.5f) continue;

                totalFear += FearSystem.Instance.GetSettlementFear(settlement.StringId);
                count++;
            }

            return count > 0 ? totalFear / count : 0.1f;
        }

        private void ApplyFearRipple(string warlordId, Vec2 epicenter, float fearDelta)
        {
            foreach (var settlement in Settlement.All)
            {
                if (!settlement.IsVillage && !settlement.IsTown) continue;
                Vec2 sPos = CompatibilityLayer.GetSettlementPosition(settlement);
                float dist = sPos.Distance(epicenter);
                if (dist > TAX_RADIUS) continue;

                float distanceFactor = 1f - (dist / TAX_RADIUS);
                FearSystem.Instance.ApplyPressureEvent(
                    settlement,
                    warlordId,
                    fearDelta: fearDelta * distanceFactor,
                    respectDelta: 0.005f * distanceFactor,
                    reason: "Kervan haracı");
            }
        }

        private static int EstimateCaravanValue(MobileParty caravan)
        {
            // Parti büyüklüğüne göre kargo değeri tahmini
            int memberCount = caravan.MemberRoster.TotalManCount;
            return memberCount * 120 + 800; // 800 temel + kişi başı 120
        }

        private bool WasTaxedRecently(string caravanId)
        {
            if (!_lastTaxedCaravans.TryGetValue(caravanId, out var lastTime)) return false;
            return (CampaignTime.Now - lastTime).ToDays < MIN_TAX_COOLDOWN_DAYS;
        }

        private static bool IsNearPlayer(Vec2 position, float radius)
        {
            if (Hero.MainHero?.PartyBelongedTo == null) return false;
            Vec2 playerPos = CompatibilityLayer.GetPartyPosition(Hero.MainHero.PartyBelongedTo);
            return playerPos.Distance(position) <= radius;
        }

        private void CleanupStaleRecords()
        {
            var staleKeys = _lastTaxedCaravans
                .Where(kv => (CampaignTime.Now - kv.Value).ToDays > 30)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in staleKeys)
                _lastTaxedCaravans.Remove(key);

            // Günlük gelir sıfırla (her gün)
            _dailyTaxRevenue.Clear();
        }

        public int GetTotalDailyRevenue(string warlordId)
        {
            return _dailyTaxRevenue.TryGetValue(warlordId, out var v) ? v : 0;
        }

        public override string GetDiagnostics()
        {
            int totalTracked = _lastTaxedCaravans.Count;
            return $"CaravanTaxSystem:\n" +
                   $"  Takip edilen kervan: {totalTracked}\n" +
                   $"  Aktif gelir kayıtları: {_dailyTaxRevenue.Count}";
        }
    }
}
