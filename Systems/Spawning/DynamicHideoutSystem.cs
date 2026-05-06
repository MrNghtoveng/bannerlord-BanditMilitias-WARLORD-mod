using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.AI;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Spawning
{
    // ── DynamicHideoutSystem ─────────────────────────────────────────

    public class DynamicHideoutSystem : BanditMilitias.Core.Components.MilitiaModuleBase
    {
        private static readonly Lazy<DynamicHideoutSystem> _instance =
            new(() => new DynamicHideoutSystem());
        public static DynamicHideoutSystem Instance => _instance.Value;

        public override string ModuleName => "DynamicHideoutSystem";
        public override bool IsEnabled => Settings.Instance?.EnableDynamicHideouts ?? true;
        public override int Priority => 85;

        private Dictionary<string, CampaignTime> _regionCooldowns = new();

        private int _formationAttempts = 0;
        private int _formationSuccesses = 0;

        private const float MIN_HIDEOUT_DISTANCE = 50f;
        private const float PATROL_DETECTION_RADIUS = 30f;
        private const float REGION_GRID_SIZE = 100f;

        private DynamicHideoutSystem() { }

        public override void Cleanup() { _regionCooldowns.Clear(); }

        public override void Initialize()
        {

            DebugLogger.Log("[DynamicHideout] System initialized.");
        }

        public override void OnDailyTick()
        {
            if (Settings.Instance?.EnableDynamicHideouts != true) return;
            if (Campaign.Current == null) return;

            var groups = FindEligiblePartyGroups();

            foreach (var group in groups)
            {
                AttemptHideoutFormation(group);
            }

            if (groups.Count > 0 && Settings.Instance?.TestingMode == true)
            {
                DebugLogger.TestLog($"[DynamicHideout] Processed {groups.Count} eligible groups.", Colors.Cyan);
            }
        }

        private List<PartyGroup> FindEligiblePartyGroups()
        {
            var groups = new List<PartyGroup>();
            var militias = ModuleManager.Instance.ActiveMilitias.ToList();

            if (militias.Count < (Settings.Instance?.MinPartiesForHideout ?? 4))
                return groups;

            var clustered = new HashSet<MobileParty>();

            foreach (var party in militias)
            {
                if (clustered.Contains(party)) continue;
                if (!IsPartyEligibleForFormation(party)) continue;

                var nearbyParties = FindNearbyMilitias(party, 20f);

                if (nearbyParties.Count >= (Settings.Instance?.MinPartiesForHideout ?? 4))
                {
                    var group = new PartyGroup
                    {
                        Parties = nearbyParties,
                        CenterPosition = CalculateCenterPosition(nearbyParties),
                        TotalStrength = nearbyParties.Sum(p => p.MemberRoster.TotalManCount)
                    };

                    groups.Add(group);

                    foreach (var p in nearbyParties)
                        _ = clustered.Add(p);
                }
            }

            return groups;
        }

        private bool IsPartyEligibleForFormation(MobileParty party)
        {
            if (party == null || !party.IsActive) return false;
            if (party.MemberRoster.TotalManCount < 10) return false;

            var component = party.PartyComponent as MilitiaPartyComponent;
            if (component == null) return false;

            if (party.MapEvent != null) return false;
            if (CustomMilitiaAI.IsPartyWounded(party)) return false;

            return true;
        }

        private List<MobileParty> FindNearbyMilitias(MobileParty center, float radius)
        {
            var result = new List<MobileParty>();
            var centerPos = CompatibilityLayer.GetPartyPosition(center);

            if (!centerPos.IsValid) return result;

            foreach (var militia in ModuleManager.Instance.ActiveMilitias)
            {
                if (militia == center) continue;

                var pos = CompatibilityLayer.GetPartyPosition(militia);
                if (!pos.IsValid) continue;

                if (pos.Distance(centerPos) <= radius)
                {
                    result.Add(militia);
                }
            }

            return result;
        }

        private Vec2 CalculateCenterPosition(List<MobileParty> parties)
        {
            float sumX = 0, sumY = 0;
            int count = 0;

            foreach (var party in parties)
            {
                var pos = CompatibilityLayer.GetPartyPosition(party);
                if (pos.IsValid)
                {
                    sumX += pos.x;
                    sumY += pos.y;
                    count++;
                }
            }

            return count > 0 ? new Vec2(sumX / count, sumY / count) : Vec2.Invalid;
        }

        private void AttemptHideoutFormation(PartyGroup group)
        {
            _formationAttempts++;

            string regionKey = GetRegionKey(group.CenterPosition);
            if (_regionCooldowns.ContainsKey(regionKey))
            {
                var cooldownEnd = _regionCooldowns[regionKey];
                if (CampaignTime.Now < cooldownEnd)
                {
                    return;
                }
            }

            if (IsAreaHeavilyPatrolled(group.CenterPosition))
            {
                if (Settings.Instance?.TestingMode == true)
                    DebugLogger.TestLog($"[DynamicHideout] Formation aborted: Area too heavily patrolled.");
                return;
            }

            Settlement? targetHideout = FindInactiveHideout(group.CenterPosition, 150f);

            if (targetHideout == null)
            {
                DebugLogger.Log($"[DynamicHideout] No empty hideout found near {group.CenterPosition}.");
                return;
            }

            if (OccupyHideout(targetHideout, group))
            {
                _formationSuccesses++;

                int cooldownDays = Settings.Instance?.HideoutFormationCooldown ?? 14;
                _regionCooldowns[regionKey] = CampaignTime.DaysFromNow(cooldownDays);

                if (Settings.Instance?.ShowTestMessages == true)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[BANDIT TAKEOVER] Militias have seized control of {targetHideout.Name}!",
                        Colors.Red));
                }

                DebugLogger.Log($"[DynamicHideout] SUCCESS! Occupied {targetHideout.Name}. Stats: {_formationSuccesses}/{_formationAttempts}");

                EventBus.Instance?.Publish(new HideoutFormedEvent
                {
                    Hideout = targetHideout,
                    FormingParties = group.Parties,
                    Position = CompatibilityLayer.GetSettlementPosition(targetHideout)
                });
            }
        }

        private Settlement? FindInactiveHideout(Vec2 center, float radius)
        {
            Settlement? best = null;
            float closestDist = float.MaxValue;

            foreach (var s in ModuleManager.Instance.HideoutCache)
            {
                if (s.IsActive) continue;

                float d = s.GatePosition.DistanceSquared(center);
                if (d < radius * radius && d < closestDist)
                {
                    closestDist = d;
                    best = s;
                }
            }
            return best;
        }

        private bool OccupyHideout(Settlement hideout, PartyGroup group)
        {
            try
            {

                var isActiveProp = typeof(Settlement).GetProperty("IsActive");
                if (isActiveProp != null && isActiveProp.CanWrite)
                {
                    isActiveProp.SetValue(hideout, true);
                }
                else
                {

                    var isActiveField = typeof(Settlement).GetField("_isVisible", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (isActiveField != null) isActiveField.SetValue(hideout, true);
                }

                hideout.IsVisible = true;
                if (hideout.Hideout != null) hideout.Hideout.IsSpotted = true;

                var clan = group.Parties
                    .Select(p => p.ActualClan)
                    .Where(c => c != null)
                    .GroupBy(c => c)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault()?.Key;
                if (clan != null)
                {

                }

                foreach (var party in group.Parties)
                {
                    if (party == null || !party.IsActive) continue;
                    BanditMilitias.Infrastructure.CompatibilityLayer.SetMoveGoToSettlement(party, hideout);
                }

                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Error("DynamicHideout", $"Failed to occupy hideout: {ex.Message}");
                return false;
            }
        }

        private string GetRegionKey(Vec2 position)
        {
            int gridX = (int)(position.x / REGION_GRID_SIZE);
            int gridY = (int)(position.y / REGION_GRID_SIZE);
            return $"{gridX}_{gridY}";
        }

        private Settlement? FindNearestSettlement(Vec2 position)
        {
            Settlement? nearest = null;
            float minDist = float.MaxValue;

            foreach (var settlement in CompatibilityLayer.GetSafeSettlements())
            {
                if (!settlement.IsActive || settlement.IsHideout) continue;

                var settPos = CompatibilityLayer.GetSettlementPosition(settlement);
                float dist = settPos.Distance(position);

                if (dist < minDist)
                {
                    minDist = dist;
                    nearest = settlement;
                }
            }

            return nearest;
        }

        private void CleanupExpiredCooldowns()
        {
            var expiredKeys = _regionCooldowns
                .Where(kvp => CampaignTime.Now >= kvp.Value)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _ = _regionCooldowns.Remove(key);
            }
        }

        public override void SyncData(IDataStore dataStore)
        {
            _ = dataStore.SyncData("_dynamicHideout_regionCooldowns", ref _regionCooldowns);
            _ = dataStore.SyncData("_dynamicHideout_attempts", ref _formationAttempts);
            _ = dataStore.SyncData("_dynamicHideout_successes", ref _formationSuccesses);
        }

        public override string GetDiagnostics()
        {
            return $"Hideout Formations: {_formationSuccesses}/{_formationAttempts} | Cooldowns: {_regionCooldowns.Count}";
        }

        private bool IsAreaHeavilyPatrolled(Vec2 position)
        {
            float maxDensity = Settings.Instance?.MaxPatrolDensityForHideout ?? 0.5f;
            if (maxDensity >= 2.0f) return false;

            int patrolCount = 0;
            float radius = PATROL_DETECTION_RADIUS * 2f;

            foreach (var party in CompatibilityLayer.GetSafeMobileParties())
            {
                if (!party.IsActive) continue;
                if (party.IsBandit || party.IsMilitia) continue;

                if (party.IsLordParty || party.IsGarrison || party.IsCaravan)
                {
                    if (CompatibilityLayer.GetPartyPosition(party).DistanceSquared(position) < radius * radius)
                    {
                        patrolCount++;
                    }
                }
            }

            return patrolCount * 0.2f > maxDensity;
        }
    }

    internal class PartyGroup
    {
        public List<MobileParty> Parties { get; set; } = new();
        public Vec2 CenterPosition { get; set; }
        public int TotalStrength { get; set; }
    }

    public class HideoutFormedEvent : IGameEvent
    {
        public Settlement? Hideout { get; set; }
        public List<MobileParty> FormingParties { get; set; } = new();
        public Vec2 Position { get; set; }

        public EventPriority Priority => EventPriority.Normal;
        public bool ShouldLog => true;
        public string GetDescription() => $"Hideout formed at {Position}";
    }

    // ── HardcoreDynamicHideoutSystem ─────────────────────────────────────────
    [BanditMilitias.Core.Components.AutoRegister]
    public class HardcoreDynamicHideoutSystem : MilitiaModuleBase
    {
        private static readonly Lazy<HardcoreDynamicHideoutSystem> _instance = new(() => new HardcoreDynamicHideoutSystem());
        public static HardcoreDynamicHideoutSystem Instance => _instance.Value;

        public override string ModuleName => "HardcoreDynamicHideoutSystem";
        public override bool IsEnabled => Settings.Instance?.EnableHardcoreDynamicHideouts ?? true;
        public override int Priority => 86;

        private const float MIN_HIDEOUT_DISTANCE = 50f;
        private const float PATROL_DETECTION_RADIUS = 30f;

        private HardcoreDynamicHideoutSystem() { }

        public override void Initialize()
        {
            DebugLogger.Log("[HardcoreHideouts] System initialized.");
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled) return;
            if (Campaign.Current == null) return;

            // Günde %5 şansla gizli kalmış bir sığınağı aktifleştirir
            if (MBRandom.RandomFloat < 0.05f)
            {
                try
                {
                    ActivateInactiveHideout();
                }
                catch (Exception ex)
                {
                    DebugLogger.Error("HardcoreHideouts", $"Crash during activation attempt: {ex}");
                    InformationManager.DisplayMessage(new InformationMessage($"[HardcoreHideout] Dengeleme hatasi: {ex.Message}", Colors.Red));
                }
            }
        }

        private void ActivateInactiveHideout()
        {
            var inactiveHideouts = ModuleManager.Instance.HideoutCache.Where(h => !h.IsActive).ToList();
            if (inactiveHideouts.Count == 0) return;

            var hideout = inactiveHideouts.GetRandomElement();
            if (hideout == null) return;
            
            var isActiveProp = typeof(Settlement).GetProperty("IsActive");
            if (isActiveProp != null && isActiveProp.CanWrite) isActiveProp.SetValue(hideout, true);
            else
            {
                var isActiveField = typeof(Settlement).GetField("_isVisible", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (isActiveField != null) isActiveField.SetValue(hideout, true);
            }
            
            hideout.IsVisible = true;
            if (hideout.Hideout != null) hideout.Hideout.IsSpotted = true;

            InformationManager.DisplayMessage(new InformationMessage($"[HARDCORE] Uyuyan bir siginak faaliyete gecti: {hideout.Name}", Colors.Red));
            DebugLogger.Log($"[HardcoreHideouts] Activated inactive Settlement: {hideout.Name}");
        }
    }

}
