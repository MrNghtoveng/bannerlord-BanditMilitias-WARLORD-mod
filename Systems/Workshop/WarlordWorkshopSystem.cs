using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Core.Neural;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Systems.Workshop
{
    public enum WorkshopType
    {
        WeaponSmith,    // Weapon Production
        ArmorSmith,     // Armor Production
        HorseBreeder,   // Horse Production
        SiegeWorks,     // Siege Engines (only T6)
        AlchemyLab,     // Moral/Fear boost items
        Fletchery       // Arrows/Bows
    }

    public class WarlordWorkshop
    {
        [SaveableProperty(1)] public string WarlordId { get; set; } = "";
        [SaveableProperty(2)] public WorkshopType Type { get; set; }
        [SaveableProperty(3)] public int Level { get; set; } = 1; // 1-3
        [SaveableProperty(4)] public float ProductionProgress { get; set; } = 0f;
        [SaveableProperty(5)] public CampaignTime LastProductionTime { get; set; } = CampaignTime.Zero;
    }

    [AutoRegister]
    public class WarlordWorkshopSystem : MilitiaModuleBase
    {
        public override string ModuleName => "WarlordWorkshopSystem";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 75;

        private static readonly Lazy<WarlordWorkshopSystem> _instance = 
            new Lazy<WarlordWorkshopSystem>(() => new WarlordWorkshopSystem());
        public static WarlordWorkshopSystem Instance => _instance.Value;

        private Dictionary<string, List<WarlordWorkshop>> _warlordWorkshops = new();

        private WarlordWorkshopSystem() { }

        public override void Initialize()
        {
            EventBus.Instance.Subscribe<MilitiaRaidCompletedEvent>(OnRaidCompleted);
        }

        public override void Cleanup()
        {
            EventBus.Instance.Unsubscribe<MilitiaRaidCompletedEvent>(OnRaidCompleted);
        }

        public override void SyncData(IDataStore dataStore)
        {
            _ = dataStore.SyncData("WarlordWorkshops_v1", ref _warlordWorkshops);
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled) return;

            foreach (var warlordId in _warlordWorkshops.Keys)
            {
                var warlord = WarlordSystem.Instance.GetWarlord(warlordId);
                if (warlord == null || !warlord.IsAlive) continue;

                ProcessProduction(warlord);
            }
        }

        private void ProcessProduction(Warlord w)
        {
            var workshops = GetWorkshops(w.StringId);
            foreach (var workshop in workshops)
            {
                // Daily Production Logic
                workshop.ProductionProgress += 0.2f * workshop.Level;

                if (workshop.ProductionProgress >= 1.0f)
                {
                    ProduceItems(w, workshop);
                    workshop.ProductionProgress = 0f;
                    workshop.LastProductionTime = CampaignTime.Now;
                }
            }
        }

        private void ProduceItems(Warlord w, WarlordWorkshop ws)
        {
            // Add items to militia parties or gold to warlord
            float value = 150f * ws.Level;
            w.Gold += value;

            if (ws.Type == WorkshopType.SiegeWorks && ws.Level >= 2)
            {
                // Special: Siege preparation ready
                var evt = EventBus.Instance.Get<SiegePreparationReadyEvent>();
                evt.Warlord = w;
                evt.WeaponLevel = ws.Level;
                NeuralEventRouter.Instance.Publish(evt);
                EventBus.Instance.Return(evt);
            }

            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("Workshop", $"[PRODUCTION] {w.Name}'s {ws.Type} level {ws.Level} produced goods worth {value:F0} gold.");
            }
        }

        public void AddWorkshop(string warlordId, WorkshopType type)
        {
            if (!_warlordWorkshops.TryGetValue(warlordId, out var list))
            {
                list = new List<WarlordWorkshop>();
                _warlordWorkshops[warlordId] = list;
            }

            if (list.Any(x => x.Type == type)) return;

            list.Add(new WarlordWorkshop { WarlordId = warlordId, Type = type, Level = 1 });
        }

        public List<WarlordWorkshop> GetWorkshops(string warlordId)
        {
            return _warlordWorkshops.TryGetValue(warlordId, out var list) ? list : new List<WarlordWorkshop>();
        }

        public float GetTotalDailyProduction(string warlordId)
        {
            if (!_warlordWorkshops.TryGetValue(warlordId, out var list)) return 0f;
            return list.Sum(ws => 150f * ws.Level);
        }

        private void OnRaidCompleted(MilitiaRaidCompletedEvent evt)
        {
            if (evt.WasSuccessful && evt.RaiderParty?.PartyComponent is MilitiaPartyComponent comp && comp.WarlordId != null)
            {
                // Successful raid adds small progress to workshops
                var workshops = GetWorkshops(comp.WarlordId);
                foreach (var ws in workshops)
                {
                    ws.ProductionProgress += 0.05f;
                }
            }
        }
        public override string GetDiagnostics()
        {
            int total = _warlordWorkshops.Values.Sum(l => l.Count);
            int active = _warlordWorkshops.Count(kv => kv.Value.Count > 0);
            float gpd = _warlordWorkshops.Values.SelectMany(l => l).Sum(ws => 150f * ws.Level);
            return $"WarlordWorkshop: {total} workshops / {active} warlords | ~{gpd:F0} gold/day";
        }

    }
}