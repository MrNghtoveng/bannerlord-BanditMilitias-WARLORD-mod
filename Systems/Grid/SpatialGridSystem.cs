using BanditMilitias.Core.Components;
using BanditMilitias.Infrastructure;
using BanditMilitias.Debug;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Grid
{
    public class SpatialGridSystem : MilitiaModuleBase
    {
        public static readonly SpatialGridSystem Instance = new();
        public override string ModuleName => "SpatialGrid";
        public override bool IsEnabled => true;
        public override int Priority => 100;

        private const float CELL_SIZE = 50f;
        private const int INITIAL_CAPACITY = 128;
        private const int MAX_POOL_SIZE = 400;

        // Single-threaded: volatile/lock/ConcurrentDictionary yok
        private Dictionary<long, List<MobileParty>> _grid = new(INITIAL_CAPACITY);
        private readonly Queue<List<MobileParty>> _pool = new();
        private bool _disposed;

        public override void Initialize()
        {
            _disposed = false;
            for (int i = 0; i < 60; i++)
                _pool.Enqueue(new List<MobileParty>(32));
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnPartyDestroyed);
        }

        public void OnSessionLaunched()
        {
            if (_disposed) return;
            
            // v1.3.15 FIX: MapState is not always active during session launch.
            // Rebuilding the grid with invalid positions (NaN) will fail.
            if (!IsCampaignMapActive())
            {
                DebugLogger.Info("SpatialGrid", "MapState not active yet, deferring initial grid rebuild.");
                return;
            }

            RebuildGrid();
            DebugLogger.Info("SpatialGrid", "Grid rebuilt on session launch.");
        }

        public override void Cleanup()
        {
            _disposed = true;
            ReturnAllToPool(_grid);
            _grid = new Dictionary<long, List<MobileParty>>();
            _pool.Clear();
            CampaignEvents.MobilePartyDestroyed.ClearListeners(this);
        }

        // dt degil OnHourlyTick - tahmin edilebilir, campaign hizina bagli degil
        public override void OnHourlyTick()
        {
            if (_disposed || Campaign.Current == null) return;
            RebuildGrid();
            BanditMilitias.Intelligence.AI.PatrolDetection.RefreshPatrolCache();
        }

        private void RebuildGrid()
        {
            var oldGrid = _grid;
            var newGrid = new Dictionary<long, List<MobileParty>>(INITIAL_CAPACITY);
            int skippedInvalid = 0;

            foreach (var party in CompatibilityLayer.GetSafeMobileParties())
            {
                if (party == null || !party.IsActive) continue;
                Vec2 pos = CompatibilityLayer.GetPartyPosition(party);
                
                if (!pos.IsValid)
                {
                    skippedInvalid++;
                    continue;
                }

                long key = GetKey(pos);
                if (!newGrid.TryGetValue(key, out var list))
                {
                    list = _pool.Count > 0 ? _pool.Dequeue() : new List<MobileParty>(16);
                    newGrid[key] = list;
                }
                list.Add(party);
            }

            _grid = newGrid;
            ReturnAllToPool(oldGrid);

            if (skippedInvalid > 0 && Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Warning("SpatialGrid", $"RebuildGrid: {skippedInvalid} parties skipped due to invalid position.");
            }
        }

        public void QueryNearby(Vec2 position, float radius, List<MobileParty> result)
        {
            if (result == null || _disposed) return;
            var grid = _grid;
            float radiusSq = radius * radius;
            int range = (int)Math.Ceiling(radius / CELL_SIZE);
            int cx = (int)(position.X / CELL_SIZE);
            int cy = (int)(position.Y / CELL_SIZE);

            for (int x = cx - range; x <= cx + range; x++)
                for (int y = cy - range; y <= cy + range; y++)
                {
                    if (!grid.TryGetValue(GetKey(x, y), out var list)) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var p = list[i];
                        if (p == null || !p.IsActive) continue;
                        if (CompatibilityLayer.GetPartyPosition(p).DistanceSquared(position) <= radiusSq
                            && !result.Contains(p))
                            result.Add(p);
                    }
                }
        }

        private void OnPartyDestroyed(MobileParty party, PartyBase _)
        {
            // Yok edilen parti grid'den kaldır.
            // Bir sonraki RebuildGrid() zaten temizler, ama o zamana kadar
            // QueryNearby() null/inactive kontrol yaptığı için güvenli;
            // yine de hücreyi hemen temizlemek hafızayı azaltır.
            if (party == null || _disposed) return;
            var grid = _grid;
            Vec2 pos = CompatibilityLayer.GetPartyPosition(party);
            if (!pos.IsValid) return;
            long key = GetKey(pos);
            if (grid.TryGetValue(key, out var list))
                list.Remove(party);
        }

        private long GetKey(Vec2 pos) => GetKey((int)(pos.X / CELL_SIZE), (int)(pos.Y / CELL_SIZE));
        private long GetKey(int x, int y) => ((long)x << 32) | (uint)y;

        private static bool IsCampaignMapActive()
        {
            var stateManager = TaleWorlds.Core.Game.Current?.GameStateManager;
            return stateManager?.ActiveState is TaleWorlds.CampaignSystem.GameState.MapState;
        }

        private void ReturnAllToPool(Dictionary<long, List<MobileParty>> grid)
        {
            foreach (var list in grid.Values)
            {
                list.Clear();
                if (_pool.Count < MAX_POOL_SIZE) _pool.Enqueue(list);
            }
            grid.Clear();
        }

        public bool IsEmpty => _grid.Count == 0;

        public override string GetDiagnostics()
            => $"SpatialGrid: {_grid.Count} hücre | Pool: {_pool.Count}";
    }
}