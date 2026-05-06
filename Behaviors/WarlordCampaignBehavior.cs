using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using System.Collections.Generic;
using System.Linq;
using BanditMilitias.Intelligence.Strategic;

namespace BanditMilitias.Behaviors
{
    public class WarlordCampaignBehavior : CampaignBehaviorBase
    {
        private Queue<MobileParty> _partiesToCalculate = new Queue<MobileParty>();
        private bool _hourlyRegistered = false;
        private bool _dailyRegistered = false;

        public override void RegisterEvents()
        {


            bool hourlyRemoved = false;
            bool dailyRemoved = false;
            try
            {
                Infrastructure.MbEventExtensions.RemoveListenerSafe(CampaignEvents.HourlyTickEvent, this, OnHourlyTick);
                hourlyRemoved = true;
                Infrastructure.MbEventExtensions.RemoveListenerSafe(CampaignEvents.DailyTickEvent, this, OnDailyTick);
                dailyRemoved = true;
            }
            catch { }

            if (hourlyRemoved || !_hourlyRegistered)
            {
                CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
                _hourlyRegistered = true;
            }

            if (dailyRemoved || !_dailyRegistered)
            {
                CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
                _dailyRegistered = true;
            }
        }


        private List<string> _savedWarlordIds = new List<string>();
        private int _savedQueueSize = 0;

        public override void SyncData(IDataStore dataStore)
        {
            try
            {
                if (dataStore.IsSaving)
                {


                    _savedWarlordIds = WarlordSystem.Instance.GetAllWarlords()
                        .Where(w => w != null && w.IsAlive)
                        .Select(w => w.StringId)
                        .ToList();
                    _savedQueueSize = _partiesToCalculate.Count;
                }

                _ = dataStore.SyncData("BM_WarlordIds",    ref _savedWarlordIds);
                _ = dataStore.SyncData("BM_WarlordQueueSz", ref _savedQueueSize);

                if (dataStore.IsLoading)
                {
                    // Guard: key may be absent if save was created before this behavior existed.
                    _savedWarlordIds ??= new List<string>();

                    _partiesToCalculate.Clear();
                    Debug.DebugLogger.Info("WarlordCampaignBehavior",
                        $"SyncData loaded: {_savedWarlordIds.Count} warlords persisted.");
                }
            }
            catch (Exception ex)
            {
                Debug.DebugLogger.Warning("WarlordCampaignBehavior", $"SyncData error: {ex.Message}");
            }
        }

        private void OnDailyTick()
        {
            _partiesToCalculate.Clear();


            foreach (var warlord in WarlordSystem.Instance.GetAllWarlords())
            {
                if (warlord != null && warlord.IsAlive)
                {
                    foreach (var party in warlord.CommandedMilitias.ToList())

                    {
                        if (party != null && party.IsActive)
                        {
                            _partiesToCalculate.Enqueue(party);
                        }
                    }
                }
            }
        }

        private void OnHourlyTick()
        {


            int calculationsPerTick = 3;

            for (int i = 0; i < calculationsPerTick; i++)
            {
                if (_partiesToCalculate.Count > 0)
                {
                    MobileParty party = _partiesToCalculate.Dequeue();
                    if (party != null && party.IsActive)
                    {


                        StrategyEngine.UpdateWarlordStrategy(party);
                    }
                }
            }
        }
    }
}
