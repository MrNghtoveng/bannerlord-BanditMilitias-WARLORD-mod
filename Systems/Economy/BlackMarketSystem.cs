using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Economy
{
    [Serializable]
    public class BlackMarketRecord
    {
        public string WarlordId { get; set; } = string.Empty;
        public string TownId { get; set; } = string.Empty;
        public string GangLeaderId { get; set; } = string.Empty;
        public CampaignTime LastPaymentTime { get; set; }
        public float InfluenceLevel { get; set; } = 0f;
    }

    [BanditMilitias.Core.Components.AutoRegister]
    public class BlackMarketSystem : BanditMilitias.Core.Components.MilitiaModuleBase
    {
        public override string ModuleName => "BlackMarket";
        public override bool IsEnabled => Settings.Instance?.EnableBlackMarket ?? true;

        private Dictionary<string, BlackMarketRecord> _agreements = new Dictionary<string, BlackMarketRecord>();
        private float _exposedRisk = 0f;

        private const int DAILY_INCOME_PER_AGREEMENT = 200;
        private const float BASE_AGREEMENT_CHANCE = 0.04f;
        private const int MIN_WARLORD_GOLD_FOR_BRIBE = 3000;
        private const int BRIBE_COST = 1500;
        private const int MAX_AGREEMENTS_PER_WARLORD = 3;
        private const float INFLUENCE_DECAY_DAILY = 0.02f;
        private const float MIN_INFLUENCE_TO_KEEP = 0.1f;

        public static BlackMarketSystem Instance => Infrastructure.ModuleManager.Instance.GetModule<BlackMarketSystem>()!;

        public override void Initialize()
        {
            _agreements.Clear();
            _exposedRisk = 0f;
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled) return;

            _exposedRisk = MathF.Max(0f, _exposedRisk - 0.05f);

            ProcessExistingAgreements();
            TryFormNewAgreements();
        }

        private void ProcessExistingAgreements()
        {
            var expired = new List<string>(4);

            foreach (var kvp in _agreements)
            {
                var record = kvp.Value;
                var warlord = WarlordSystem.Instance.GetWarlord(record.WarlordId);

                if (warlord == null || !warlord.IsAlive)
                {
                    expired.Add(kvp.Key);
                    continue;
                }

                warlord.Gold += DAILY_INCOME_PER_AGREEMENT;

                record.InfluenceLevel = MathF.Max(0f, record.InfluenceLevel - INFLUENCE_DECAY_DAILY);
                record.LastPaymentTime = CampaignTime.Now;

                if (record.InfluenceLevel < MIN_INFLUENCE_TO_KEEP)
                {
                    expired.Add(kvp.Key);
                    if (Settings.Instance?.TestingMode == true)
                        DebugLogger.Info("BlackMarket", $"Agreement in {kvp.Key} expired: influence too low.");
                }
            }

            foreach (var key in expired)
                _ = _agreements.Remove(key);
        }

        private void TryFormNewAgreements()
        {
            var warlords = WarlordSystem.Instance.GetAllWarlords();

            foreach (var warlord in warlords)
            {
                if (!warlord.IsAlive) continue;

                int activeCount = _agreements.Values.Count(r => r.WarlordId == warlord.StringId);
                if (activeCount >= MAX_AGREEMENTS_PER_WARLORD) continue;

                if (warlord.Gold < MIN_WARLORD_GOLD_FOR_BRIBE) continue;

                if (MBRandom.RandomFloat > BASE_AGREEMENT_CHANCE) continue;

                var target = FindNearbyTown(warlord);
                if (target == null) continue;

                warlord.Gold -= BRIBE_COST;
                _exposedRisk += 0.1f;

                var record = new BlackMarketRecord
                {
                    WarlordId = warlord.StringId,
                    TownId = target.StringId,
                    LastPaymentTime = CampaignTime.Now,
                    InfluenceLevel = 0.5f + MBRandom.RandomFloat * 0.5f
                };

                _agreements[target.StringId] = record;

                if (Settings.Instance?.TestingMode == true)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"ğŸ•µï¸ Black Market: {warlord.Name} established a network in {target.Name}! (+{DAILY_INCOME_PER_AGREEMENT}g/day)",
                        Colors.Yellow));
                }
            }
        }

        private Settlement? FindNearbyTown(Warlord warlord)
        {
            var hideout = warlord.AssignedHideout;
            if (hideout == null) return null;

            var epicenterPos = CompatibilityLayer.GetSettlementPosition(hideout);

            return ModuleManager.Instance.TownCache
                .Where(s => s.IsTown &&
                            !_agreements.ContainsKey(s.StringId) &&
                            CompatibilityLayer.GetSettlementPosition(s).Distance(epicenterPos) < 100f)
                .OrderBy(s => CompatibilityLayer.GetSettlementPosition(s).Distance(epicenterPos))
                .FirstOrDefault();
        }

        /// <summary>AscensionEvaluator için: belirtilen warlord'un aktif kara borsa ağ sayısı.</summary>
        public int GetNetworkCount(string warlordId)
        {
            if (string.IsNullOrEmpty(warlordId)) return 0;
            int count = 0;
            foreach (var r in _agreements.Values)
                if (r.WarlordId == warlordId) count++;
            return count;
        }

                public override void SyncData(IDataStore dataStore)
        {
            _ = dataStore.SyncData("_blackMarketAgreements_v1", ref _agreements);
            if (dataStore.IsLoading && _agreements == null)
                _agreements = new Dictionary<string, BlackMarketRecord>();
        }

        public override string GetDiagnostics()
        {
            return $"BlackMarketSystem:\n" +
                   $"  Active Networks: {_agreements.Count}\n" +
                   $"  Exposed Risk: {_exposedRisk:F2}";
        }
    }
}