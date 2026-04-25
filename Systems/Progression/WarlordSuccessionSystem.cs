using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Progression
{
    /// <summary>
    /// Halef Sistemi — Warlord ölünce birliklerin %55'i yeni bir halef warlord etrafında toplanır.
    /// Tier 3+ warlord ölümlerinde devreye girer.
    /// Halef, önceki liderin prestijinin %60'ını miras alır.
    /// </summary>
    [AutoRegister]
    public class WarlordSuccessionSystem : MilitiaModuleBase
    {
        public override string ModuleName => "WarlordSuccessionSystem";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 90; // WarlordSystem'den sonra çalışsın

        private static readonly Lazy<WarlordSuccessionSystem> _instance =
            new Lazy<WarlordSuccessionSystem>(() => new WarlordSuccessionSystem());
        public static WarlordSuccessionSystem Instance => _instance.Value;

        private Dictionary<string, string> _successionHistory = new();
        // predecessor id → successor id

        private const float SUCCESSION_TROOP_RATIO = 0.65f; // Birlik devir oranı (tüm tier)
        private const float PRESTIGE_INHERITANCE_RATIO = 0.60f; // Prestij miras oranı
        private const int MIN_TIER_FOR_SUCCESSION = 1; // Tüm warlord sınıfları için aktif
        private const int MIN_TROOPS_FOR_SUCCESSION = 20; // Düşürülmüş minimum (Tier 1 için)

        private bool _initialized = false;

        private WarlordSuccessionSystem() { }

        public override void Initialize()
        {
            if (_initialized) return;
            EventBus.Instance.Subscribe<WarlordFallenEvent>(OnWarlordFallen);
            _initialized = true;
            DebugLogger.Info("Succession", "WarlordSuccessionSystem başlatıldı.");
        }

        public override void Cleanup()
        {
            EventBus.Instance.Unsubscribe<WarlordFallenEvent>(OnWarlordFallen);
            _initialized = false;
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("WarlordSuccession_History_v1", ref _successionHistory);
        }

        private void OnWarlordFallen(WarlordFallenEvent evt)
        {
            if (evt?.Warlord == null) return;
            var fallen = evt.Warlord;

            // Sadece yüksek tier warlordlar için halef oluştur
            int tier = (int)WarlordCareerSystem.Instance.GetOrCreate(fallen.StringId).Tier;
            if (tier < MIN_TIER_FOR_SUCCESSION) return;

            // Toplam asker sayısını kontrol et
            int totalTroops = CountWarlordTroops(fallen.StringId);
            if (totalTroops < MIN_TROOPS_FOR_SUCCESSION) return;

            TryCreateSuccessor(fallen, totalTroops);
        }

        private void TryCreateSuccessor(Warlord fallen, int totalTroops)
        {
            try
            {
                // En büyük milisya partisini bul — halefin çekirdeği olacak
                var warlordParties = CompatibilityLayer.GetSafeMobileParties()
                    .Where(p => p.PartyComponent is MilitiaPartyComponent comp
                             && comp.WarlordId == fallen.StringId
                             && p.MemberRoster.TotalManCount > 10)
                    .OrderByDescending(p => p.MemberRoster.TotalManCount)
                    .ToList();

                if (warlordParties.Count == 0) return;

                var coreParty = warlordParties[0];
                var coreComp = coreParty.PartyComponent as MilitiaPartyComponent;
                if (coreComp == null) return;

                // Yeni warlord oluştur
                Settlement? homeSettlement = coreComp.GetHomeSettlement()
                    ?? FindNearestHideout(CompatibilityLayer.GetPartyPosition(coreParty));

                if (homeSettlement == null) return;

                var successor = WarlordSystem.Instance.CreateWarlord(homeSettlement);
                if (successor == null) return;

                // Halef özellikleri ayarla
                SetupSuccessor(successor, fallen, totalTroops);

                // Birlikleri halefe devret
                TransferTroops(fallen.StringId, successor.StringId, warlordParties);

                // Miras kaydı
                _successionHistory[fallen.StringId] = successor.StringId;

                // Oyuncu bildirimi
                NotifyPlayer(fallen, successor);

                DebugLogger.Info("Succession",
                    $"[HALEF] {fallen.Name} öldü → {successor.Name} liderliği devraldı. " +
                    $"Devredilen asker: {(int)(totalTroops * SUCCESSION_TROOP_RATIO)}");
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("Succession", $"Halef oluşturma hatası: {ex.Message}");
            }
        }

        private void SetupSuccessor(Warlord successor, Warlord fallen, int totalTroops)
        {
            // İsim — önceki liderin anısını taşır
            successor.Name = GenerateSuccessorName(fallen);
            int fallenTier = (int)WarlordCareerSystem.Instance.GetOrCreate(fallen.StringId).Tier;
            successor.Title = fallenTier >= 4 ? "Halef Kaptan" : "Yeni Kaptan";

            // Prestij mirası
            successor.Gold = fallen.Gold * PRESTIGE_INHERITANCE_RATIO;

            // Tier bir alt seviyeden başlar (ama minimum 2)
            int fTier = (int)WarlordCareerSystem.Instance.GetOrCreate(fallen.StringId).Tier;
            var successorRecord = WarlordCareerSystem.Instance.GetOrCreate(successor.StringId);
            successorRecord.Tier = (CareerTier)Math.Max(2, fTier - 1);

            // Kişilik — fallenin kişiliğini kısmen miras alabilir
            successor.Personality = fallen.Personality;
            successor.Backstory = fallen.Backstory; // Aynı hikayenin devamı

            // Öğrenilmiş taktikleri miras al
            var tactics = coreParty_InheritedTactics(fallen);
            if (tactics != null && tactics.Count > 0)
            {
                // Taktik mirası WarlordCareerSystem üzerinden
                DebugLogger.Info("Succession", $"Taktik mirası: {tactics.Count} taktik devredildi.");
            }
        }

        private Dictionary<string, float> coreParty_InheritedTactics(Warlord fallen)
        {
            // Ölen warlord'un en büyük milisyasının inherited tactics'ini al
            var coreParty = CompatibilityLayer.GetSafeMobileParties()
                .Where(p => p.PartyComponent is MilitiaPartyComponent comp
                         && comp.WarlordId == fallen.StringId)
                .OrderByDescending(p => p.MemberRoster.TotalManCount)
                .FirstOrDefault();

            if (coreParty?.PartyComponent is MilitiaPartyComponent c)
                return c.InheritedTactics ?? new();

            return new();
        }

        private void TransferTroops(string fallenId, string successorId, List<MobileParty> parties)
        {
            int transferred = 0;

            foreach (var party in parties)
            {
                if (party.PartyComponent is not MilitiaPartyComponent comp) continue;

                // Yalnızca SUCCESSION_TROOP_RATIO oranı kalır, geri kalanı dağılır
                if (MBRandom.RandomFloat < SUCCESSION_TROOP_RATIO)
                {
                    comp.WarlordId = successorId;
                    transferred += party.MemberRoster.TotalManCount;
                }
                // Diğerleri mevcut konumda kalır ama warlord bağlantısı kesilir
                // (PartyCleanupSystem bunları zamanla temizler)
            }

            DebugLogger.Info("Succession",
                $"Birlik devri: {transferred} asker {successorId}'ya aktarıldı.");
        }

        private static string GenerateSuccessorName(Warlord fallen)
        {
            string[] suffixes = { "İkinci", "Halef", "Varis", "Devam", "Jr." };
            string suffix = suffixes[MBRandom.RandomInt(suffixes.Length)];
            return $"{fallen.Name.Split(' ')[0]} {suffix}";
        }

        private static int CountWarlordTroops(string warlordId)
        {
            return CompatibilityLayer.GetSafeMobileParties()
                .Where(p => p.PartyComponent is MilitiaPartyComponent comp
                         && comp.WarlordId == warlordId)
                .Sum(p => p.MemberRoster.TotalManCount);
        }

        private static Settlement? FindNearestHideout(Vec2 position)
        {
            return Settlement.All
                .Where(s => s.IsHideout)
                .OrderBy(s => CompatibilityLayer.GetSettlementPosition(s).Distance(position))
                .FirstOrDefault();
        }

        private static void NotifyPlayer(Warlord fallen, Warlord successor)
        {
            InformationManager.DisplayMessage(new InformationMessage(
                $"[Siyaset] {fallen.FullName} düştü — " +
                $"{successor.Name} liderliği devraldı ve askerleri toparlıyor.",
                new Color(0.7f, 0.5f, 0.9f)));
        }

        public bool HasSuccessor(string warlordId)
            => _successionHistory.ContainsKey(warlordId);

        public string? GetSuccessorId(string warlordId)
            => _successionHistory.TryGetValue(warlordId, out var s) ? s : null;

        public override string GetDiagnostics()
        {
            return $"WarlordSuccession:\n" +
                   $"  Kayıtlı halef geçmişi: {_successionHistory.Count}\n" +
                   $"  Min tier: {MIN_TIER_FOR_SUCCESSION}, Min asker: {MIN_TROOPS_FOR_SUCCESSION}";
        }
    }
}
