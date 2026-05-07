using BanditMilitias.Core.Components;
using BanditMilitias.Infrastructure;
using BanditMilitias.Debug;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Grid
{
    [BanditMilitias.Core.Components.AutoRegister(Priority = 30, IsCritical = true, IsSingleton = true)]
    public class SpatialGridSystem : MilitiaModuleBase
    {
        public static SpatialGridSystem Instance { get; } = new();
        public override string ModuleName => "SpatialGrid";
        public override bool IsEnabled => true;
        public override int Priority => 100;

        private const float DefaultCellSize = 50f;
        private const float MinCellSize = 25f;
        private const float MaxCellSize = 150f;
        private const int INITIAL_CAPACITY = 128;
        private const int MAX_POOL_SIZE = 400;

        // Double-buffer for grid rebuild: avoids allocating a new dictionary every hour
        private Dictionary<long, List<MobileParty>> _grid = new(INITIAL_CAPACITY);
        private Dictionary<long, List<MobileParty>> _gridBack = new(INITIAL_CAPACITY);
        private Dictionary<long, List<Settlement>> _settlementGrid = new(INITIAL_CAPACITY);
        private readonly Queue<List<MobileParty>> _pool = new();
        private readonly Queue<List<Settlement>> _settlementPool = new();
        private bool _disposed;
        private float _cellSize = DefaultCellSize;

        public override void Initialize()
        {
            _disposed = false;
            for (int i = 0; i < 60; i++)
                _pool.Enqueue(new List<MobileParty>(32));
            for (int i = 0; i < 30; i++)
                _settlementPool.Enqueue(new List<Settlement>(16));
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnPartyDestroyed);
        }

        public override void OnSessionStart()
        {
            if (_disposed) return;


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
            _cellSize = DefaultCellSize;
            ReturnAllToPool(_grid);
            ReturnAllSettlementsToPool(_settlementGrid);
            _grid = new Dictionary<long, List<MobileParty>>();
            _settlementGrid = new Dictionary<long, List<Settlement>>();
            _pool.Clear();
            _settlementPool.Clear();
            CampaignEvents.MobilePartyDestroyed.ClearListeners(this);
        }


        public override void OnHourlyTick()
        {
            if (_disposed || Campaign.Current == null) return;
            RebuildGrid();
            BanditMilitias.Intelligence.AI.PatrolDetection.RefreshPatrolCache();
        }

        private void RebuildGrid()
        {
            // Double-buffer swap: reuse the back dictionary instead of allocating new
            var newGrid = _gridBack;
            newGrid.Clear();
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

            var oldGrid = _grid;
            _grid = newGrid;
            _gridBack = oldGrid;
            ReturnAllToPool(oldGrid);

            if (skippedInvalid > 0 && Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Warning("SpatialGrid", $"RebuildGrid: {skippedInvalid} parties skipped due to invalid position.");
            }
        }

        public void RebuildSettlementGrid(
            IReadOnlyList<Settlement> hideouts,
            IReadOnlyList<Settlement> villages,
            IReadOnlyList<Settlement> towns,
            IReadOnlyList<Settlement> castles)
        {
            UpdateAdaptiveCellSize(hideouts, villages, towns, castles);

            var oldGrid = _settlementGrid;
            var newGrid = new Dictionary<long, List<Settlement>>(INITIAL_CAPACITY);

            AddSettlements(newGrid, hideouts);
            AddSettlements(newGrid, villages);
            AddSettlements(newGrid, towns);
            AddSettlements(newGrid, castles);

            _settlementGrid = newGrid;
            ReturnAllSettlementsToPool(oldGrid);
        }

        public void QueryNearby(Vec2 position, float radius, List<MobileParty> result)
        {
            if (result == null || _disposed) return;
            var grid = _grid;
            float radiusSq = radius * radius;
            int range = (int)Math.Ceiling(radius / _cellSize);
            int cx = (int)(position.X / _cellSize);
            int cy = (int)(position.Y / _cellSize);

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


            if (party == null || _disposed) return;
            var grid = _grid;
            Vec2 pos = CompatibilityLayer.GetPartyPosition(party);
            if (!pos.IsValid) return;
            long key = GetKey(pos);
            if (grid.TryGetValue(key, out var list))
                list.Remove(party);
        }

        private long GetKey(Vec2 pos) => GetKey((int)(pos.X / _cellSize), (int)(pos.Y / _cellSize));
        private long GetKey(int x, int y) => ((long)x << 32) | (uint)y;

        public List<Settlement> QueryNearbySettlements(Vec2 position, float radius, SettlementType type)
        {
            var results = new List<Settlement>();
            QueryNearbySettlements(position, radius, type, results);
            return results;
        }

        /// <summary>
        /// Zero-allocation overload: caller provides the result list to avoid GC pressure.
        /// </summary>
        public void QueryNearbySettlements(Vec2 position, float radius, SettlementType type, List<Settlement> result)
        {
            if (result == null || _disposed || !position.IsValid || radius <= 0f)
            {
                return;
            }

            float radiusSq = radius * radius;
            int range = (int)Math.Ceiling(radius / _cellSize);
            int cx = (int)(position.X / _cellSize);
            int cy = (int)(position.Y / _cellSize);

            for (int x = cx - range; x <= cx + range; x++)
            {
                for (int y = cy - range; y <= cy + range; y++)
                {
                    if (!_settlementGrid.TryGetValue(GetKey(x, y), out var list)) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        Settlement settlement = list[i];
                        if (settlement == null || !MatchesType(settlement, type)) continue;
                        Vec2 settlementPos = CompatibilityLayer.GetSettlementPosition(settlement);
                        if (settlementPos.IsValid && settlementPos.DistanceSquared(position) <= radiusSq && !result.Contains(settlement))
                        {
                            result.Add(settlement);
                        }
                    }
                }
            }
        }

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

        private void ReturnAllSettlementsToPool(Dictionary<long, List<Settlement>> grid)
        {
            foreach (var list in grid.Values)
            {
                list.Clear();
                if (_settlementPool.Count < MAX_POOL_SIZE) _settlementPool.Enqueue(list);
            }
            grid.Clear();
        }

        private void AddSettlements(Dictionary<long, List<Settlement>> grid, IReadOnlyList<Settlement> settlements)
        {
            if (settlements == null) return;

            for (int i = 0; i < settlements.Count; i++)
            {
                Settlement settlement = settlements[i];
                if (settlement == null) continue;
                Vec2 pos = CompatibilityLayer.GetSettlementPosition(settlement);
                if (!pos.IsValid) continue;
                long key = GetKey(pos);
                if (!grid.TryGetValue(key, out var list))
                {
                    list = _settlementPool.Count > 0 ? _settlementPool.Dequeue() : new List<Settlement>(8);
                    grid[key] = list;
                }
                list.Add(settlement);
            }
        }

        private void UpdateAdaptiveCellSize(params IReadOnlyList<Settlement>[] settlementGroups)
        {
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            int count = 0;

            foreach (var group in settlementGroups)
            {
                if (group == null) continue;

                for (int i = 0; i < group.Count; i++)
                {
                    Settlement settlement = group[i];
                    if (settlement == null) continue;

                    Vec2 pos = CompatibilityLayer.GetSettlementPosition(settlement);
                    if (!pos.IsValid) continue;

                    count++;
                    if (pos.X < minX) minX = pos.X;
                    if (pos.Y < minY) minY = pos.Y;
                    if (pos.X > maxX) maxX = pos.X;
                    if (pos.Y > maxY) maxY = pos.Y;
                }
            }

            if (count < 2)
            {
                _cellSize = DefaultCellSize;
                return;
            }

            float span = Math.Max(maxX - minX, maxY - minY);
            if (span <= 0f || float.IsNaN(span) || float.IsInfinity(span))
            {
                _cellSize = DefaultCellSize;
                return;
            }

            _cellSize = MathF.Clamp(span / 50f, MinCellSize, MaxCellSize);
        }

        private static bool MatchesType(Settlement settlement, SettlementType type)
        {
            return type switch
            {
                SettlementType.Hideout => settlement.IsHideout,
                SettlementType.Village => settlement.IsVillage,
                SettlementType.Town => settlement.IsTown,
                _ => true
            };
        }

        public bool IsEmpty => _grid.Count == 0;

        public override string GetDiagnostics()
            => $"SpatialGrid: {_grid.Count} cells | CellSize={_cellSize:F1} | Pool: {_pool.Count}";
    }
}


