using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Systems.Tracking
{
    // ── CaravanActivityTracker ─────────────────────────────────────────

    public class CaravanActivityTracker : BanditMilitias.Core.Components.MilitiaModuleBase
    {
        private static CaravanActivityTracker? _instance;
        public static CaravanActivityTracker Instance => _instance ??= new CaravanActivityTracker();

        public override string ModuleName => "CaravanActivityTracker";
        public override int Priority => 61;

        private Dictionary<long, float> _tradeIntensityGrid = new();
        private static readonly List<long> _keysToRemoveBuffer = new(128);


        private Queue<TradeEvent> _recentEvents = new();

        private const int MAX_EVENT_HISTORY = 100;
        private const float GRID_SIZE = 100f;
        private const float CARAVAN_ENTRY_INTENSITY = 0.5f;
        private const float TRADE_INTENSITY_CAP = 10.0f;
        private const float DECAY_RATE = 0.08f;

        private CaravanActivityTracker() { }

        public override void Initialize()
        {
            FileLogger.Log("[CaravanActivityTracker] System initialized.");
        }

        public override void RegisterCampaignEvents()
        {
            CampaignEvents.SettlementEntered.AddNonSerializedListener(this, OnSettlementEntered);

            FileLogger.Log("[CaravanActivityTracker] Campaign events registered.");
        }

        public override void OnDailyTick()
        {
            ApplyDecay();
            CleanupOldEvents();
        }

        public override void Cleanup()
        {
            _tradeIntensityGrid.Clear();
            _recentEvents = new Queue<TradeEvent>();
            CampaignEvents.SettlementEntered.ClearListeners(this);
        }

        private void OnSettlementEntered(MobileParty party, Settlement settlement, Hero hero)
        {
            try
            {
                if (party == null || !party.IsCaravan || settlement == null) return;

                if (!settlement.IsTown && !settlement.IsVillage) return;

                var position = CompatibilityLayer.GetSettlementPosition(settlement);

                RecordTradeEvent(position, CARAVAN_ENTRY_INTENSITY, settlement.Name.ToString());

            }
            catch (Exception ex)
            {
                DebugLogger.Error("CaravanActivityTracker", $"OnSettlementEntered failed: {ex.Message}");
            }
        }

        private void RecordTradeEvent(Vec2 position, float intensity, string locationName)
        {
            var tradeEvent = new TradeEvent
            {
                Position = position,
                Intensity = intensity,
                LocationName = locationName,
                Timestamp = CampaignTime.Now
            };

            _recentEvents.Enqueue(tradeEvent);

            while (_recentEvents.Count > MAX_EVENT_HISTORY)
            {
                if (_recentEvents.Count > 0) _ = _recentEvents.Dequeue();
            }

            long gridKey = GetGridKey(position);
            if (_tradeIntensityGrid.TryGetValue(gridKey, out float existingTradeIntensity))
                _tradeIntensityGrid[gridKey] = Math.Min(existingTradeIntensity + intensity, TRADE_INTENSITY_CAP);
            else
                _tradeIntensityGrid[gridKey] = intensity;
        }

        private void ApplyDecay()
        {
            _keysToRemoveBuffer.Clear();
            
            // Silinecekleri topla
            foreach (var kvp in _tradeIntensityGrid)
            {
                if (kvp.Value * (1.0f - DECAY_RATE) < 0.1f)
                    _keysToRemoveBuffer.Add(kvp.Key);
            }

            // Sil
            foreach (var key in _keysToRemoveBuffer)
                _ = _tradeIntensityGrid.Remove(key);

            // Kalanları güncelle
            foreach (var key in _tradeIntensityGrid.Keys.ToArray())
            {
                _tradeIntensityGrid[key] *= (1.0f - DECAY_RATE);
            }
        }

        private void CleanupOldEvents()
        {
            var cutoffTime = CampaignTime.DaysFromNow(-30);
            
            // OPTIMIZASYON: .ToList() ve new Queue() yerine Dequeue döngüsü
            while (_recentEvents.Count > 0 && _recentEvents.Peek().Timestamp <= cutoffTime)
            {
                _recentEvents.Dequeue();
            }
        }

        public float GetTradeIntensity(Vec2 position)
        {
            long gridKey = GetGridKey(position);
            return _tradeIntensityGrid.TryGetValue(gridKey, out float intensity) ? intensity : 0f;
        }

        private long GetGridKey(Vec2 position)
        {
            int gridX = (int)(position.x / GRID_SIZE);
            int gridY = (int)(position.y / GRID_SIZE);
            return ((long)gridX << 32) | (uint)gridY;
        }

        public override string GetDiagnostics()
        {
            int activeRegions = _tradeIntensityGrid.Count;
            float maxIntensity = _tradeIntensityGrid.Values.DefaultIfEmpty(0f).Max();
            return $"Trade Zones: {activeRegions} | Max Intensity: {maxIntensity:F1} | Events: {_recentEvents.Count}";
        }

        public override void SyncData(IDataStore dataStore)
        {
            Dictionary<long, float> gridForSave = new(_tradeIntensityGrid);
            _ = dataStore.SyncData("_tradeIntensityGrid", ref gridForSave);

            if (dataStore.IsLoading && gridForSave != null)
                _tradeIntensityGrid = new Dictionary<long, float>(gridForSave);

            List<TradeEvent> eventsList = _recentEvents.ToList();
            _ = dataStore.SyncData("_tradeEvents", ref eventsList);

            if (dataStore.IsLoading && eventsList != null)
                _recentEvents = new Queue<TradeEvent>(eventsList);
        }
    }

    [Serializable]
    public class TradeEvent
    {
        [SaveableField(1)]
        public Vec2 Position;
        [SaveableField(2)]
        public float Intensity;
        [SaveableField(3)]
        public string LocationName = string.Empty;
        [SaveableField(4)]
        public CampaignTime Timestamp;
    }

    // ── WarActivityTracker ─────────────────────────────────────────

    public class WarActivityTracker : BanditMilitias.Core.Components.MilitiaModuleBase
    {
        private static WarActivityTracker? _instance;
        public static WarActivityTracker Instance => _instance ??= new WarActivityTracker();

        public override string ModuleName => "WarActivityTracker";

        public override int Priority => 60;

        private Dictionary<long, float> _warIntensityGrid = new();
        private static readonly List<long> _keysToRemoveBuffer = new(128);


        private Queue<WarEvent> _recentEvents = new();

        private const int MAX_EVENT_HISTORY = 100;
        private const float GRID_SIZE = 100f;
        private const float BATTLE_INTENSITY = 1.0f;
        private const float SIEGE_INTENSITY = 2.0f;

        private const float INTENSITY_DECAY_RATE = 0.1f;

        private WarActivityTracker() { }

        public override void Initialize()
        {

            FileLogger.Log("[WarActivityTracker] System initialized.");
        }

        public override void RegisterCampaignEvents()
        {
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
            CampaignEvents.OnSiegeEventStartedEvent.AddNonSerializedListener(this, OnSiegeStarted);
            FileLogger.Log("[WarActivityTracker] Campaign events registered.");
        }

        public override void OnDailyTick()
        {
            ApplyIntensityDecay();
            CleanupOldEvents();
        }

        public override void Cleanup()
        {
            _warIntensityGrid.Clear();
            _recentEvents = new Queue<WarEvent>();
            CampaignEvents.MapEventEnded.ClearListeners(this);
            CampaignEvents.OnSiegeEventStartedEvent.ClearListeners(this);
        }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            try
            {
                if (mapEvent == null || !mapEvent.IsFieldBattle) return;

                int totalTroops = 0;
                if (mapEvent.StrengthOfSide != null && mapEvent.StrengthOfSide.Length >= 2)
                {
                    totalTroops = (int)(mapEvent.StrengthOfSide[0] + mapEvent.StrengthOfSide[1]);
                }

                if (totalTroops < 20) return;

                var position = new Vec2(mapEvent.Position.X, mapEvent.Position.Y);

                float intensity = BATTLE_INTENSITY * (totalTroops / 100f);
                intensity = Math.Min(intensity, 5.0f);

                RecordWarEvent(position, intensity, WarEventType.Battle);

                int winner = (int)mapEvent.WinningSide + 1;
                float side1Str = mapEvent.StrengthOfSide?[0] ?? 0f;
                float side2Str = mapEvent.StrengthOfSide?[1] ?? 0f;
                int side1Cas = 0;
                int side2Cas = 0;

                TelemetryBridge.LogBattle(
                    mapEvent.Id.ToString(),
                    position.x, position.y,
                    side1Str, side2Str,
                    side1Cas, side2Cas,
                    winner, "FieldBattle");

            }
            catch (Exception ex)
            {
                DebugLogger.Error("WarActivityTracker", $"OnMapEventEnded failed: {ex.Message}");
            }
        }

        private void OnSiegeStarted(SiegeEvent siegeEvent)
        {
            try
            {
                if (siegeEvent?.BesiegedSettlement == null) return;

                var position = CompatibilityLayer.GetSettlementPosition(siegeEvent.BesiegedSettlement);

                RecordWarEvent(position, SIEGE_INTENSITY, WarEventType.Siege);

            }
            catch (Exception ex)
            {
                DebugLogger.Error("WarActivityTracker", $"OnSiegeStarted failed: {ex.Message}");
            }
        }

        private void RecordWarEvent(Vec2 position, float intensity, WarEventType type)
        {

            var warEvent = new WarEvent
            {
                Position = position,
                Intensity = intensity,
                Type = type,
                Timestamp = CampaignTime.Now
            };

            _recentEvents.Enqueue(warEvent);

            while (_recentEvents.Count > MAX_EVENT_HISTORY)
            {
                if (_recentEvents.Count > 0) _ = _recentEvents.Dequeue();
            }

            long gridKey = GetGridKey(position);

            if (_warIntensityGrid.TryGetValue(gridKey, out float existingWarIntensity))
                _warIntensityGrid[gridKey] = Math.Min(existingWarIntensity + intensity, 10.0f);
            else
                _warIntensityGrid[gridKey] = intensity;
        }

        private void ApplyIntensityDecay()
        {
            _keysToRemoveBuffer.Clear();

            // Tahsis yapmadan anahtarları topla
            foreach (var kvp in _warIntensityGrid)
            {
                if (kvp.Value * (1.0f - INTENSITY_DECAY_RATE) < 0.1f)
                    _keysToRemoveBuffer.Add(kvp.Key);
            }

            // Silinecekleri kaldır
            foreach (var key in _keysToRemoveBuffer)
                _ = _warIntensityGrid.Remove(key);

            // Geriye kalanları güncelle (ayrı döngüde çünkü foreach sırasında modifikasyon yasak)
            // Not: Dictionary entry'lerini güncellemek için .ToList() kullanmak zorundayız 
            // ya da Keys üzerinden gitmeliyiz. Ama en verimlisi Keys kopyasıdır.
            foreach (var key in _warIntensityGrid.Keys.ToArray())
            {
                _warIntensityGrid[key] *= (1.0f - INTENSITY_DECAY_RATE);
            }
        }

        public float GetIntensity(Vec2 position)
        {
            long gridKey = GetGridKey(position);

            if (_warIntensityGrid.TryGetValue(gridKey, out float intensity))
            {
                return intensity;
            }

            return 0f;
        }

        private long GetGridKey(Vec2 position)
        {
            int gridX = (int)(position.x / GRID_SIZE);
            int gridY = (int)(position.y / GRID_SIZE);
            return ((long)gridX << 32) | (uint)gridY;
        }

        public override string GetDiagnostics()
        {
            int activeRegions = _warIntensityGrid.Count;
            float maxIntensity = _warIntensityGrid.Values.DefaultIfEmpty(0f).Max();

            return $"War Zones: {activeRegions} | Max Intensity: {maxIntensity:F1} | Events: {_recentEvents.Count}";
        }

        public override void SyncData(IDataStore dataStore)
        {

            Dictionary<long, float> gridForSave = new(_warIntensityGrid);
            _ = dataStore.SyncData("_warIntensityGrid", ref gridForSave);

            if (dataStore.IsLoading && gridForSave != null)
            {
                _warIntensityGrid = new Dictionary<long, float>(gridForSave);
            }

            List<WarEvent> eventsList = _recentEvents.ToList();
            _ = dataStore.SyncData("_warEvents", ref eventsList);

            if (dataStore.IsLoading && eventsList != null)
            {
                _recentEvents = new Queue<WarEvent>(eventsList);
            }
        }

        public void CleanupOldEvents()
        {
            var cutoffTime = CampaignTime.DaysFromNow(-30);

            // OPTIMIZASYON: Dequeue döngüsü
            while (_recentEvents.Count > 0 && _recentEvents.Peek().Timestamp <= cutoffTime)
            {
                _recentEvents.Dequeue();
            }
        }

        private const float DESERTER_THRESHOLD_TROOPS = 100f;
        private const float DESERTER_MAX_MULTIPLIER = 2.5f;
        private const float DESERTER_DECAY_DAYS = 7f;

        public float GetDeserterSpawnModifier(Vec2 position, float radius = 80f)
        {
            if (!position.IsValid) return 1.0f;

            float totalModifier = 0f;
            float now = (float)CampaignTime.Now.ToDays;
            float radiusSq = radius * radius;

            foreach (var warEvent in _recentEvents)
            {

                if (warEvent.Type != WarEventType.Battle) continue;

                if (warEvent.Intensity < DESERTER_THRESHOLD_TROOPS / 100f) continue;

                float distSq = warEvent.Position.DistanceSquared(position);
                if (distSq > radiusSq) continue;

                float eventDay = (float)warEvent.Timestamp.ToDays;
                float daysSince = now - eventDay;
                if (daysSince < 0 || daysSince > DESERTER_DECAY_DAYS) continue;

                float timeFactor = 1.0f - (daysSince / DESERTER_DECAY_DAYS);

                float distFactor = 1.0f - (distSq / radiusSq);

                totalModifier += warEvent.Intensity * timeFactor * distFactor * 0.3f;
            }

            return 1.0f + Math.Min(totalModifier, DESERTER_MAX_MULTIPLIER - 1.0f);
        }
    }

    [Serializable]
    public class WarEvent
    {
        [SaveableField(1)]
        public Vec2 Position;
        [SaveableField(2)]
        public float Intensity;
        [SaveableField(3)]
        public WarEventType Type;
        [SaveableField(4)]
        public CampaignTime Timestamp;
    }

    public enum WarEventType
    {
        Battle,
        Siege
    }

}
