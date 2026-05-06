using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Fear;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Systems.Diplomacy
{
    [Serializable]
    public class PropagandaRecord
    {
        [SaveableProperty(1)]
        public string WarlordId { get; set; } = string.Empty;
        [SaveableProperty(2)]
        public string TownId { get; set; } = string.Empty;
        [SaveableProperty(3)]
        public CampaignTime StartTime { get; set; } = CampaignTime.Zero;
        [SaveableProperty(4)]
        public float Intensity { get; set; } = 0f;
    }

    [BanditMilitias.Core.Components.AutoRegister]
    public class PropagandaSystem : BanditMilitias.Core.Components.MilitiaModuleBase
    {
        public override string ModuleName => "Propaganda";
        public override bool IsEnabled => Settings.Instance?.EnablePropaganda ?? true;

        public static PropagandaSystem Instance => Infrastructure.ModuleManager.Instance.GetModule<PropagandaSystem>()!;

        private Dictionary<string, PropagandaRecord> _activeOperations = new Dictionary<string, PropagandaRecord>();

        private const int OPERATION_COST_DAILY = 150;
        private const float BASE_LOYALTY_PENALTY_DAILY = 0.5f;

        public override void Initialize()
        {
            _activeOperations.Clear();
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled) return;

            ProcessOperations();
            InitiateNewOperations();
            ApplyLoyaltyPenalties();
        }

        private void ProcessOperations()
        {
            var expired = new List<string>();

            foreach (var kvp in _activeOperations)
            {
                var record = kvp.Value;
                var warlord = WarlordSystem.Instance.GetWarlord(record.WarlordId);

                if (warlord == null || !warlord.IsAlive)
                {
                    expired.Add(kvp.Key);
                    continue;
                }

                if (warlord.Gold >= OPERATION_COST_DAILY)
                {
                    warlord.Gold -= OPERATION_COST_DAILY;
                }
                else
                {
                    expired.Add(kvp.Key);
                    if (Settings.Instance?.TestingMode == true)
                        DebugLogger.Info("Propaganda", $"Propaganda in {kvp.Key} cancelled: Warlord {warlord.Name} broke.");
                }
            }

            foreach (var key in expired)
            {
                _ = _activeOperations.Remove(key);
            }
        }

        private void ApplyLoyaltyPenalties()
        {
            foreach (var kvp in _activeOperations)
            {
                var record = kvp.Value;
                var town = Settlement.Find(record.TownId);
                if (town?.Town == null) continue;

                float penalty = BASE_LOYALTY_PENALTY_DAILY * record.Intensity;
                town.Town.Loyalty = MathF.Max(0f, town.Town.Loyalty - penalty);

                // FearSystem: propaganda başarılı olunca çevre köylere saygı/korku dalgası
                try
                {
                    if (town.BoundVillages != null)
                    {
                        foreach (var village in town.BoundVillages)
                        {
                            if (village?.Settlement == null) continue;
                            Fear.FearSystem.Instance.ApplyPressureEvent(
                                village.Settlement,
                                record.WarlordId,
                                fearDelta: 0.005f * record.Intensity,
                                respectDelta: 0.008f * record.Intensity,
                                reason: "Propaganda dalgası");
                        }
                    }
                }
                catch { }

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Info("Propaganda",
                        $"[{town.Name}] Loyalty -{penalty:F1} | intensity={record.Intensity:F2}");
                }
            }
        }

        private void InitiateNewOperations()
        {
            var warlords = WarlordSystem.Instance.GetAllWarlords();

            foreach (var warlord in warlords)
            {
                int activeForWarlord = CountOperationsForWarlord(warlord.StringId);
                if (activeForWarlord >= 2) continue;

                if (warlord.Gold < 5000 + activeForWarlord * 2500) continue;

                float operationChance = 0.05f;
                if (MBRandom.RandomFloat > operationChance) continue;

                var targetTown = FindVulnerableTown(warlord);
                if (targetTown != null)
                {
                    StartOperation(warlord, targetTown);
                }
            }
        }

        private Settlement? FindVulnerableTown(Warlord warlord)
        {
            var epicenter = warlord.AssignedHideout;
            if (epicenter == null) return null;

            var candidates = Infrastructure.ModuleManager.Instance.TownCache
                .Where(s => s.IsTown && !_activeOperations.ContainsKey(s.StringId) && s.Town.Loyalty < 60f && !s.MapFaction.IsRebelClan)
                .OrderBy(s => CompatibilityLayer.GetSettlementPosition(s).DistanceSquared(CompatibilityLayer.GetSettlementPosition(epicenter)))
                .ToList();

            if (candidates.Count > 0 && CompatibilityLayer.GetSettlementPosition(candidates[0]).Distance(CompatibilityLayer.GetSettlementPosition(epicenter)) < 80f)
            {
                return candidates[0];
            }

            return null;
        }

        private void StartOperation(Warlord warlord, Settlement town)
        {
            var record = new PropagandaRecord
            {
                WarlordId = warlord.StringId,
                TownId = town.StringId,
                StartTime = CampaignTime.Now,
                Intensity = 0.5f + (MBRandom.RandomFloat * 0.5f)
            };

            _activeOperations[town.StringId] = record;

            if (Settings.Instance?.TestingMode == true)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"?? Propaganda Alert: {warlord.Name} has begun inciting unrest in {town.Name}!",
                    Colors.Magenta));
            }
        }

        public override void SyncData(IDataStore dataStore)
        {
            _ = dataStore.SyncData("_propagandaOperations_v1", ref _activeOperations);

            if (dataStore.IsLoading && _activeOperations == null)
                _activeOperations = new Dictionary<string, PropagandaRecord>();
        }

        public override string GetDiagnostics()
        {
            return $"PropagandaSystem:\n  Active Ops: {_activeOperations.Count}";
        }

        private int CountOperationsForWarlord(string warlordId)
        {
            return _activeOperations.Values.Count(o => o.WarlordId == warlordId);
        }
    }
}