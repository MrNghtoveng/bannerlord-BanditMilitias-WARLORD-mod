using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.AI
{
    /// <summary>
    /// Milislerin bölgesel ve stratejik hafızasını yöneten merkezi sistem.
    /// Plan 6e83e5ce: Adaptif Hafıza Sistemi.
    /// </summary>
    public class MilitiaMemorySystem : MilitiaModuleBase
    {
        public static readonly MilitiaMemorySystem Instance = new MilitiaMemorySystem();

        private MilitiaMemorySystem() { }

        public override string ModuleName => "MilitiaMemorySystem";
        public override bool IsEnabled => true; // Hafıza her zaman açık olmalı
        public override int Priority => 10; // Düşük öncelik, diğer sistemler hazır olunca başlasın

        private MilitiaMemoryData _data = new();
        private int _lastWorldSettlementCount = 0;

        public override void Initialize()
        {
            EventBus.Instance.Subscribe<MilitiaSpawnedEvent>(OnMilitiaSpawned);
            EventBus.Instance.Subscribe<MilitiaKilledEvent>(OnMilitiaKilled);
            EventBus.Instance.Subscribe<HideoutClearedEvent>(OnHideoutCleared);
        }

        public override void Cleanup()
        {
            EventBus.Instance.Unsubscribe<MilitiaSpawnedEvent>(OnMilitiaSpawned);
            EventBus.Instance.Unsubscribe<MilitiaKilledEvent>(OnMilitiaKilled);
            EventBus.Instance.Unsubscribe<HideoutClearedEvent>(OnHideoutCleared);
            _data = new MilitiaMemoryData();
        }

        public override void OnSessionStart()
        {
            VerifyMemoryIntegrity();
            RefreshMilitiaPresenceSnapshot();
        }

        public override void OnHourlyTick()
        {
            RefreshMilitiaPresenceSnapshot();
        }

        /// <summary>
        /// Hafızanın mevcut oyun dünyasıyla uyumlu olup olmadığını kontrol eder.
        /// Eğer yerleşke sayısı değişmişse (yeni mod vb.), adaptif olarak hafızayı günceller.
        /// </summary>
        private void VerifyMemoryIntegrity()
        {
            int currentSettlementCount = Settlement.All?.Count ?? 0;

            if (_data.Settlements.Count == 0 && currentSettlementCount > 0)
            {
                RebuildSettlementSnapshot();
            }

            if (_lastWorldSettlementCount != currentSettlementCount && _lastWorldSettlementCount != 0)
            {
                DebugLogger.Info("MemorySystem", $"World changed! Settlement count: {_lastWorldSettlementCount} -> {currentSettlementCount}. Updating memory...");
                RebuildSettlementSnapshot();
            }

            _lastWorldSettlementCount = currentSettlementCount;

            // Orphaned (sahipsiz/silinmiş) verileri temizle
            int removedCount = _data.Settlements.RemoveAll(s => s.IsOrphaned);
            if (removedCount > 0)
            {
                DebugLogger.Warning("MemorySystem", $"Cleaned {removedCount} orphaned entries from memory.");
            }
        }

        private void RebuildSettlementSnapshot()
        {
            _data.Settlements.Clear();
            foreach (var settlement in Settlement.All.Where(s => s.IsVillage || s.IsTown || s.IsCastle || s.IsHideout))
            {
                _data.Settlements.Add(new KnownSettlementMemory
                {
                    SettlementId = settlement.StringId,
                    DangerScore = 0f,
                    LastVisited = CampaignTime.Never,
                    ActiveMilitiaCount = 0,
                    HasGarrison = false
                });
            }
            _data.LastFullScan = CampaignTime.Now;
        }

        private KnownSettlementMemory GetOrCreateSettlementMemory(Settlement settlement)
        {
            var mem = _data.Settlements.FirstOrDefault(s => s.SettlementId == settlement.StringId);
            if (mem != null) return mem;

            mem = new KnownSettlementMemory { SettlementId = settlement.StringId };
            _data.Settlements.Add(mem);
            return mem;
        }

        private void RefreshMilitiaPresenceSnapshot()
        {
            foreach (var settlementMemory in _data.Settlements)
            {
                settlementMemory.ActiveMilitiaCount = 0;
                settlementMemory.HasGarrison = false;
            }

            foreach (var militia in ModuleManager.Instance.ActiveMilitias)
            {
                if (militia?.IsActive != true || militia.PartyComponent is not BanditMilitias.Components.MilitiaPartyComponent component)
                    continue;

                var home = component.GetHomeSettlementRaw() ?? component.GetHomeSettlement();
                if (home == null) continue;

                var mem = GetOrCreateSettlementMemory(home);
                mem.ActiveMilitiaCount++;
                mem.HasGarrison = mem.ActiveMilitiaCount > 0;
            }
        }

        public override void SyncData(IDataStore dataStore)
        {
            _data ??= new MilitiaMemoryData();
            
            // Basit veri tiplerini SyncData ile sakla
            // Karmaşık listeler için BanditMilitiasSaveDefiner'a güveniyoruz
            var memoryData = _data;
            _ = dataStore.SyncData("_militiaMemory", ref memoryData);
            _data = memoryData;

            _ = dataStore.SyncData("_lastWorldSettlementCount", ref _lastWorldSettlementCount);
        }

        // ── Public API ────────────────────────────────────────────────

        public void RecordSettlementVisit(Settlement settlement)
        {
            if (settlement == null) return;
            var mem = GetOrCreateSettlementMemory(settlement);

            mem.LastVisited = CampaignTime.Now;
            // Tehlike puanını zamanla düşür (ziyaret edildiyse güvenlidir)
            mem.DangerScore = Math.Max(0, mem.DangerScore - 10f);
        }

        public void ReportDanger(Settlement settlement, float severity)
        {
            if (settlement == null) return;
            var mem = GetOrCreateSettlementMemory(settlement);

            mem.DangerScore = Math.Min(100, mem.DangerScore + severity);
            mem.LastRaid = CampaignTime.Now;
        }

        public int GetActiveMilitiaCount(Settlement settlement)
        {
            if (settlement == null) return 0;
            return GetOrCreateSettlementMemory(settlement).ActiveMilitiaCount;
        }

        public bool HasActiveMilitia(Settlement settlement)
        {
            return GetActiveMilitiaCount(settlement) > 0;
        }

        public void UpdateThreat(MobileParty party)
        {
            if (party == null || party.MapFaction == null) return;
            
            var threat = _data.ActiveThreats.FirstOrDefault(t => t.EntityId == party.StringId);
            if (threat == null)
            {
                threat = new ThreatMemory { EntityId = party.StringId, IsPlayer = party.IsMainParty };
                _data.ActiveThreats.Add(threat);
            }

            threat.LastKnownPosition = CompatibilityLayer.GetPartyPosition(party);
            threat.LastSpottedTime = CampaignTime.Now;
            threat.ReportedStrength = party.MemberRoster.TotalManCount;

            // 24 saatten eski tehditleri temizle
            _data.ActiveThreats.RemoveAll(t => t.LastSpottedTime.ElapsedHoursUntilNow > 24);
        }

        public List<ThreatMemory> GetNearbyThreats(Vec2 position, float radius)
        {
            float radiusSq = radius * radius;
            return _data.ActiveThreats
                .Where(t => t.LastKnownPosition.DistanceSquared(position) <= radiusSq)
                .ToList();
        }

        public KnownSettlementMemory? GetSettlementMemory(string stringId)
        {
            return _data.Settlements.FirstOrDefault(s => s.SettlementId == stringId);
        }

        private void OnMilitiaSpawned(MilitiaSpawnedEvent evt)
        {
            if (evt.HomeHideout == null) return;

            var mem = GetOrCreateSettlementMemory(evt.HomeHideout);
            mem.ActiveMilitiaCount++;
            mem.HasGarrison = mem.ActiveMilitiaCount > 0;
        }

        private void OnMilitiaKilled(MilitiaKilledEvent evt)
        {
            if (evt.HomeHideout == null) return;

            var mem = GetOrCreateSettlementMemory(evt.HomeHideout);
            mem.ActiveMilitiaCount = Math.Max(0, mem.ActiveMilitiaCount - 1);
            mem.HasGarrison = mem.ActiveMilitiaCount > 0;
            mem.LastRaid = CampaignTime.Now;
        }

        private void OnHideoutCleared(HideoutClearedEvent evt)
        {
            if (evt.Hideout == null) return;

            var mem = GetOrCreateSettlementMemory(evt.Hideout);
            mem.ActiveMilitiaCount = Math.Max(0, evt.SurvivingMilitias);
            mem.HasGarrison = mem.ActiveMilitiaCount > 0;
            mem.LastRaid = CampaignTime.Now;
        }
    }
}
