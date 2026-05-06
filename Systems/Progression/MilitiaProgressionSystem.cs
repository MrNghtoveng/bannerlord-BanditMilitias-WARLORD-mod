using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Progression
{
    /// <summary>
    /// TroopProgressionSystem + MilitiaUpgradeSystem'ın birleşimi.
    /// XP dağıtımı ve upgrade tetiklemesi tek transaction içinde yönetilir;
    /// aralarındaki 24 saatlik gecikme ve XP sıfırlanmama hataları ortadan kalkar.
    /// </summary>
    [AutoRegister]
    public sealed class MilitiaProgressionSystem : MilitiaModuleBase
    {
        private static readonly Lazy<MilitiaProgressionSystem> _inst =
            new(() => new MilitiaProgressionSystem());
        public static MilitiaProgressionSystem Instance => _inst.Value;

        public override string ModuleName => "MilitiaProgressionSystem";
        public override bool IsEnabled => true;
        public override int Priority => 80;

        // Horde XP havuzu: warlordId → birikmiş XP
        private readonly Dictionary<string, int> _hordeXpPool = new();

        // ── Günlük tick ────────────────────────────────────────────────
        public override void OnDailyTick()
        {
            if (!IsEnabled) return;

            var snapshot = ModuleManager.Instance.ActiveMilitias;

            // 1. Tüm aktif milislere pasif antrenman XP'si ekle ve hemen upgrade dene
            for (int p = 0; p < snapshot.Count; p++)
            {
                var party = snapshot[p];
                if (party == null || !party.IsActive || party.MemberRoster == null) continue;

                if (party.PartyComponent is not MilitiaPartyComponent comp) continue;

                var warlord = comp.AssignedWarlord
                              ?? WarlordSystem.Instance.GetWarlordForParty(party);
                if (warlord == null) continue;

                int xp = CalculatePassiveTrainingXp(comp, warlord);
                bool anyAdded = ApplyXpToRoster(party, xp);

                // XP eklendi mi? Hemen upgrade dene — yarına bırakma
                if (anyAdded)
                    TryUpgradeRoster(party, warlord);
            }

            // 2. Horde havuzunu dağıt
            DistributeHordePool();
        }

        // ── Savaş zaferi çağrısı (Combat.cs ProcessVictory'den) ───────
        /// <summary>
        /// Savaş bittiğinde Combat.cs tarafından çağrılır.
        /// Kazanan partiye doğrudan XP uygulanır, horde havuzuna katkı da eklenir.
        /// </summary>
        public void OnBattleVictory(MobileParty winner, float enemyStrength, float winnerStrength)
        {
            if (winner == null || !winner.IsActive || winner.MemberRoster == null) return;
            if (winner.PartyComponent is not MilitiaPartyComponent comp) return;

            var warlord = comp.AssignedWarlord
                          ?? WarlordSystem.Instance.GetWarlordForParty(winner);
            if (warlord == null) return;

            // Anlamlı XP hesabı: asker sayısı değil, düşman gücü tabanlı
            int xpReward = (int)(enemyStrength * 50f);

            // Underdog bonusu: zayıf taraf güçlüyü yendiyse +%50
            if (winnerStrength > 0f && winnerStrength < enemyStrength * 0.8f)
                xpReward = (int)(xpReward * 1.5f);

            xpReward = Math.Max(xpReward, 200); // Minimum garanti

            // Doğrudan kazanan partiye uygula
            bool anyAdded = ApplyXpToRoster(winner, xpReward);
            if (anyAdded)
                TryUpgradeRoster(winner, warlord);

            // Horde havuzuna da katkı: savaş XP'sinin %15'i paylaşıma girer
            AddToHordePool(warlord, xpReward);

            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.TestLog(
                    $"[Progression] {winner.Name}: Savaş XP={xpReward} (düşman güç={enemyStrength:F0})",
                    Colors.Green);
            }
        }

        // ── Havuz yönetimi ─────────────────────────────────────────────
        public void AddToHordePool(Warlord warlord, int baseAmount)
        {
            if (warlord == null || baseAmount <= 0) return;

            if (!_hordeXpPool.ContainsKey(warlord.StringId))
                _hordeXpPool[warlord.StringId] = 0;

            _hordeXpPool[warlord.StringId] += (int)(baseAmount * 0.15f);
        }

        private void DistributeHordePool()
        {
            foreach (var kv in _hordeXpPool)
            {
                if (kv.Value <= 0) continue;

                var warlord = WarlordSystem.Instance.GetWarlordById(kv.Key);
                if (warlord == null) continue;

                var militias = warlord.CommandedMilitias;
                if (militias == null || militias.Count == 0) continue;

                int xpPerParty = kv.Value / militias.Count;
                if (xpPerParty <= 0) continue;

                for (int i = 0; i < militias.Count; i++)
                {
                    var party = militias[i];
                    if (party == null || !party.IsActive) continue;

                    bool anyAdded = ApplyXpToRoster(party, xpPerParty);
                    if (anyAdded)
                        TryUpgradeRoster(party, warlord);
                }
            }

            // Havuzu sıfırla — yeni döngü için temiz başlangıç
            var keys = new List<string>(_hordeXpPool.Keys);
            foreach (var k in keys)
                _hordeXpPool[k] = 0;
        }

        // ── Pasif antrenman XP hesabı ─────────────────────────────────
        private static int CalculatePassiveTrainingXp(MilitiaPartyComponent comp, Warlord warlord)
        {
            int xp = 80; // Minimum garanti

            // Warlord ekonomik gücüne göre eğitim kalitesi
            if (warlord.Gold > 20_000f) xp += 50;
            if (warlord.Gold > 50_000f) xp += 50;

            // VeteranCaptain rolü: tecrübeli komutan bonusu
            if (comp.Role == MilitiaPartyComponent.MilitiaRole.VeteranCaptain)
                xp += 40;

            return xp;
        }

        // ── XP uygulama ───────────────────────────────────────────────
        /// <summary>
        /// Roster'daki birimlere XP ekler. T0-T2 birimlere 1.5x çarpan uygulanır.
        /// Döndürür: herhangi bir birime XP eklendiyse true.
        /// </summary>
        private static bool ApplyXpToRoster(MobileParty party, int amount)
        {
            if (amount <= 0 || party?.MemberRoster == null) return false;

            bool anyAdded = false;
            int count = party.MemberRoster.Count;

            for (int i = 0; i < count; i++)
            {
                var element = party.MemberRoster.GetElementCopyAtIndex(i);
                if (element.Character == null || element.Number <= 0) continue;

                int xpToAdd = element.Character.Tier <= 2
                    ? (int)(amount * 1.5f) // T0-T2: Ölüm sarmalından çıkmaları için hızlandırılmış ilerleme
                    : amount;

                party.MemberRoster.AddXpToTroopAtIndex(xpToAdd, i);
                anyAdded = true;
            }

            return anyAdded;
        }

        // ── Upgrade ─────────────────────────────────────────────────────
        /// <summary>
        /// Roster'da yeterli XP biriken birimleri warlord'un altınına ve stratejisine göre yükseltir.
        /// Sondan başa iterasyon: AddToCounts() sonrası roster sırası kayabilir.
        /// </summary>
        private static void TryUpgradeRoster(MobileParty party, Warlord warlord)
        {
            if (party?.MemberRoster == null || warlord == null) return;

            bool upgradedAny = false;

            // Sondan başa: AddToCounts çağrısı slot sırasını kaydırabilir
            for (int i = party.MemberRoster.Count - 1; i >= 0; i--)
            {
                var element = party.MemberRoster.GetElementCopyAtIndex(i);
                if (element.Character == null || element.Number <= 0) continue;
                if (element.Character.UpgradeTargets == null
                    || element.Character.UpgradeTargets.Length == 0) continue;

                int xpCost = element.Character.GetUpgradeXpCost(party.Party, 0);
                if (xpCost <= 0) continue;

                int currentXp = CompatibilityLayer.GetElementXpAtIndex(party.MemberRoster, i);
                int upgradeReadyCount = currentXp / xpCost;
                if (upgradeReadyCount <= 0) continue;

                // Bir seferde upgrade edilecek asker sayısını kısıtla (denge için)
                int toUpgrade = Math.Min(upgradeReadyCount, Math.Max(1, element.Number / 3));

                // Altın kontrolü
                int goldCostPerTroop = Math.Max(1, element.Character.Tier * 10);
                int totalGoldCost = toUpgrade * goldCostPerTroop;

                if (warlord.Gold < totalGoldCost)
                {
                    toUpgrade = (int)(warlord.Gold / goldCostPerTroop);
                    totalGoldCost = toUpgrade * goldCostPerTroop;
                }

                if (toUpgrade <= 0) continue;

                var targetTroop = element.Character.UpgradeTargets[0];

                // Upgrade uygula
                party.MemberRoster.AddToCounts(element.Character, -toUpgrade);
                party.MemberRoster.AddToCounts(targetTroop, toUpgrade);
                warlord.Gold -= totalGoldCost;
                upgradedAny = true;

                // XP artığını koru: harcanan XP'yi düş, kalanı tutmaya çalış.
                // Not: AddToCounts sonrası i-nolu slot değişmiş olabilir.
                // Yeni slotu bul ve kalan XP'yi yaz.
                int remainingXp = currentXp - (toUpgrade * xpCost);
                TrySetRemainingXp(party, element.Character, remainingXp);

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.TestLog(
                        $"[Upgrade] {party.Name}: {toUpgrade}x {element.Character.Name} → {targetTroop.Name} (-{totalGoldCost} Altın)",
                        Colors.Yellow);
                }
            }

            if (upgradedAny)
                party.RecentEventsMorale += 5f;
        }

        /// <summary>
        /// Upgrade sonrası eski troop slotunda kalan XP'yi sıfırlar.
        /// Slot index'i kayabileceğinden karakter üzerinden bulunur.
        /// </summary>
        private static void TrySetRemainingXp(MobileParty party, CharacterObject character, int remainingXp)
        {
            if (remainingXp <= 0) return;

            try
            {
                for (int j = 0; j < party.MemberRoster.Count; j++)
                {
                    var el = party.MemberRoster.GetElementCopyAtIndex(j);
                    if (el.Character != character) continue;

                    // Mevcut XP'den fazlasını sil — slot'u remainingXp'ye getir
                    int current = CompatibilityLayer.GetElementXpAtIndex(party.MemberRoster, j);
                    if (current > remainingXp)
                    {
                        // AddXpToTroopAtIndex negatif değer almaz; sıfırdan yeniden kurmak gerekiyor.
                        // Mevcut Bannerlord API'sinde SetXp doğrudan yok; workaround:
                        // Sayıyı çıkar-ekle ile slot'u sıfırla, sonra remainingXp ekle.
                        int count = el.Number;
                        party.MemberRoster.AddToCounts(character, -count);
                        party.MemberRoster.AddToCounts(character, count);
                        // Yeni slot sıfır XP ile başlar; kalan XP'yi ekle
                        int newIdx = FindSlotIndex(party, character);
                        if (newIdx >= 0 && remainingXp > 0)
                            party.MemberRoster.AddXpToTroopAtIndex(remainingXp, newIdx);
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                // XP sıfırlama kritik değil — yutulabilir, birim zaten upgrade oldu
                DebugLogger.Warning("MilitiaProgressionSystem",
                    $"TrySetRemainingXp failed for {character?.Name}: {ex.Message}");
            }
        }

        private static int FindSlotIndex(MobileParty party, CharacterObject character)
        {
            for (int i = 0; i < party.MemberRoster.Count; i++)
            {
                if (party.MemberRoster.GetElementCopyAtIndex(i).Character == character)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Legacy compatibility bridge.
        /// Old callers can forward upgrade intent without duplicating logic.
        /// </summary>
        public void UpgradePartyTroopsCompat(MobileParty party, Warlord warlord)
        {
            if (!IsEnabled) return;
            TryUpgradeRoster(party, warlord);
        }

        // ── MilitiaModuleBase zorunlu override'lar ─────────────────────
        public override void OnTick(float dt) { }
        public override void OnHourlyTick() { }

        public override string GetDiagnostics()
        {
            int poolTotal = 0;
            foreach (var v in _hordeXpPool.Values) poolTotal += v;
            return $"MilitiaProgression: HordePool={poolTotal} XP | Warlords={_hordeXpPool.Count}";
        }

        public override void SyncData(IDataStore ds)
        {
            // Horde XP havuzu kalıcı değil (günlük sıfırlanır), kayıt gerekmez
        }

        public override void Cleanup()
        {
            _hordeXpPool.Clear();
        }
    }
}
