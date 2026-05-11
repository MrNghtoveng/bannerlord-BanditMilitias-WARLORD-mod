using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Systems.WarlordLegitimacy;
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
        WeaponSmith,

        ArmorSmith,

        HorseBreeder,

        SiegeWorks,

        AlchemyLab,

        Fletchery

    }

    public class WarlordWorkshop
    {
        [SaveableProperty(1)] public string WarlordId { get; set; } = "";
        [SaveableProperty(2)] public WorkshopType Type { get; set; }
        [SaveableProperty(3)] public int Level { get; set; } = 1;

        [SaveableProperty(4)] public float ProductionProgress { get; set; } = 0f;
        [SaveableProperty(5)] public CampaignTime LastProductionTime { get; set; } = CampaignTime.Zero;
    }

    [BanditMilitias.Core.Components.ModuleDependency(
        typeof(BanditMilitias.Intelligence.Strategic.WarlordSystem),
        typeof(BanditMilitias.Systems.WarlordLegitimacy.WarlordLegitimacySystem))]
    [BanditMilitias.Core.Components.AutoRegister(Priority = 75, IsCritical = false)]
    public class WarlordWorkshopSystem : MilitiaModuleBase
    {
        public override string ModuleName => "WarlordWorkshopSystem";
        public override bool IsEnabled    => Settings.Instance?.EnableWarlords ?? true;
        public override int  Priority     => 75;


        private static readonly int[] UpgradeCosts = { 0, 8_000, 25_000 };


        private const float BASE_GOLD_PER_LEVEL = 120f;


        private static float TypeGoldMult(WorkshopType t) => t switch
        {
            WorkshopType.WeaponSmith  => 1.2f,
            WorkshopType.ArmorSmith   => 1.3f,
            WorkshopType.Fletchery    => 0.9f,
            WorkshopType.HorseBreeder => 1.5f,
            WorkshopType.AlchemyLab   => 0.8f,
            WorkshopType.SiegeWorks   => 0.5f,
            _                         => 1.0f
        };

        private static readonly Lazy<WarlordWorkshopSystem> _instance =
            new Lazy<WarlordWorkshopSystem>(() => new WarlordWorkshopSystem());
        public static WarlordWorkshopSystem Instance => _instance.Value;

        private Dictionary<string, List<WarlordWorkshop>> _warlordWorkshops = new();

        private WarlordWorkshopSystem() { }

        public override void Initialize()
        {
            BanditMilitias.Core.Events.EventBus.Instance.Subscribe<MilitiaRaidCompletedEvent>(OnRaidCompleted);
        }

        public override void Cleanup()
        {
            BanditMilitias.Core.Events.EventBus.Instance.Unsubscribe<MilitiaRaidCompletedEvent>(OnRaidCompleted);
            _warlordWorkshops.Clear();
        }

        public override void SyncData(IDataStore dataStore)
        {
            _ = dataStore.SyncData("WarlordWorkshops_v1", ref _warlordWorkshops);
            if (dataStore.IsLoading && _warlordWorkshops == null)
                _warlordWorkshops = new Dictionary<string, List<WarlordWorkshop>>();
        }


        public override void OnDailyTick()
        {
            if (!IsEnabled) return;
            if (ModActivationManager.IsGameplayActivationDelayed()) return;

            foreach (var warlordId in _warlordWorkshops.Keys.ToList())
            {
                var warlord = WarlordSystem.Instance.GetWarlord(warlordId);
                if (warlord == null || !warlord.IsAlive) continue;

                ProcessProduction(warlord);
                TryAutoUpgrade(warlord);
            }
        }


        private void ProcessProduction(Warlord w)
        {
            foreach (var ws in GetWorkshops(w.StringId))
            {


                ws.ProductionProgress += 0.2f * ws.Level;

                if (ws.ProductionProgress >= 1.0f)
                {
                    ProduceOutput(w, ws);
                    ws.ProductionProgress = 0f;
                    ws.LastProductionTime = CampaignTime.Now;
                }
            }
        }

        private void ProduceOutput(Warlord w, WarlordWorkshop ws)
        {
            float gold = BASE_GOLD_PER_LEVEL * ws.Level * TypeGoldMult(ws.Type);
            w.Gold += gold;

            switch (ws.Type)
            {

                case WorkshopType.WeaponSmith:
                    ApplyEquipmentQualityBonus(w, ws.Level);
                    break;


                case WorkshopType.ArmorSmith:
                    ApplyEquipmentQualityBonus(w, ws.Level * 2);
                    break;


                case WorkshopType.Fletchery:
                    AddRangedTroops(w, ws.Level);
                    break;


                case WorkshopType.HorseBreeder:
                    AddMountedTroops(w, ws.Level);
                    break;


                case WorkshopType.AlchemyLab:
                    if (ModuleAccess.TryGetEnabled<WarlordLegitimacySystem>(out var legit))
                        legit.ApplyPoints(w, ws.Level * 1.5f, "AlchemyLab");
                    break;


                case WorkshopType.SiegeWorks:
                    if (ws.Level >= 2)
                    {
                        var evt = BanditMilitias.Core.Events.EventBus.Instance.Get<SiegePreparationReadyEvent>();
                        evt.Warlord = w;
                        evt.WeaponLevel = ws.Level;
                        NeuralEventRouter.Instance.Publish(evt);
                        BanditMilitias.Core.Events.EventBus.Instance.Return(evt);
                    }
                    break;
            }

            if (Settings.Instance?.TestingMode == true)
                DebugLogger.Info("Workshop",
                    $"[PROD] {w.Name} | {ws.Type} Lv{ws.Level} → {gold:F0}g");
        }


        private static void ApplyEquipmentQualityBonus(Warlord w, int levelPoints)
        {
            float bonus = levelPoints * 0.01f;
            var target = w.CommandedMilitias
                .Where(m => m?.IsActive == true && m.PartyComponent is MilitiaPartyComponent)
                .OrderBy(m => m.MemberRoster.TotalManCount)
                .FirstOrDefault();

            if (target?.PartyComponent is MilitiaPartyComponent comp)
                comp.EquipmentQuality = MathF.Min(5f, comp.EquipmentQuality + bonus);
        }

        private static void AddRangedTroops(Warlord w, int level)
        {
            var target = GetWeakestMilitia(w);
            if (target == null) return;

            var ranged = target.MemberRoster.GetTroopRoster()
                .FirstOrDefault(e => e.Character?.DefaultFormationClass == FormationClass.Ranged);
            if (ranged.Character == null) return;

            int limit = CompatibilityLayer.GetPartyMemberSizeLimit(target.Party);
            int toAdd = Math.Min(level, limit - target.MemberRoster.TotalManCount);
            if (toAdd > 0)
                target.MemberRoster.AddToCounts(ranged.Character, toAdd);
        }

        private static void AddMountedTroops(Warlord w, int level)
        {
            var target = GetWeakestMilitia(w);
            if (target == null) return;

            var cav = target.MemberRoster.GetTroopRoster()
                .FirstOrDefault(e =>
                    e.Character?.DefaultFormationClass == FormationClass.Cavalry ||
                    e.Character?.DefaultFormationClass == FormationClass.HeavyCavalry ||
                    e.Character?.DefaultFormationClass == FormationClass.LightCavalry);
            if (cav.Character == null) return;

            int limit = CompatibilityLayer.GetPartyMemberSizeLimit(target.Party);
            int toAdd = Math.Min(level, limit - target.MemberRoster.TotalManCount);
            if (toAdd > 0)
                target.MemberRoster.AddToCounts(cav.Character, toAdd);
        }

        private static MobileParty? GetWeakestMilitia(Warlord w)
            => w.CommandedMilitias
                .Where(m => m?.IsActive == true)
                .OrderBy(m => m.MemberRoster.TotalManCount)
                .FirstOrDefault();


        public bool UpgradeWorkshop(string warlordId, WorkshopType type)
        {
            var ws = GetWorkshops(warlordId).FirstOrDefault(x => x.Type == type);
            if (ws == null || ws.Level >= 3) return false;

            var warlord = WarlordSystem.Instance.GetWarlord(warlordId);
            if (warlord == null) return false;

            int cost = UpgradeCosts[ws.Level];
            if (warlord.Gold < cost) return false;

            warlord.Gold -= cost;
            ws.Level++;

            if (Settings.Instance?.TestingMode == true)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Workshop] {warlord.Name}: {ws.Type} → Level {ws.Level}! (-{cost:N0}g)",
                    Colors.Yellow));
            }

            DebugLogger.Info("Workshop",
                $"[UPGRADE] {warlord.Name} | {ws.Type} → Lv{ws.Level} | -{cost}g");
            return true;
        }

        private void TryAutoUpgrade(Warlord w)
        {
            if (w.Gold < 30_000) return;

            var candidate = GetWorkshops(w.StringId)
                .Where(ws => ws.Level < 3)
                .OrderBy(ws => ws.Level)
                .FirstOrDefault();

            if (candidate != null && MBRandom.RandomFloat < 0.10f)
                UpgradeWorkshop(w.StringId, candidate.Type);
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
            DebugLogger.Info("Workshop", $"[ADD] {type} workshop opened for {warlordId}");
        }

        public List<WarlordWorkshop> GetWorkshops(string warlordId)
            => _warlordWorkshops.TryGetValue(warlordId, out var list)
                ? list
                : new List<WarlordWorkshop>();

        public float GetTotalDailyProduction(string warlordId)
        {
            if (!_warlordWorkshops.TryGetValue(warlordId, out var list)) return 0f;
            return list.Sum(ws => BASE_GOLD_PER_LEVEL * ws.Level * TypeGoldMult(ws.Type));
        }


        private void OnRaidCompleted(MilitiaRaidCompletedEvent evt)
        {
            if (!evt.WasSuccessful) return;
            if (evt.RaiderParty?.PartyComponent is MilitiaPartyComponent comp && comp.WarlordId != null)
            {
                foreach (var ws in GetWorkshops(comp.WarlordId))
                    ws.ProductionProgress += 0.05f;
            }
        }


        public override string GetDiagnostics()
        {
            int total  = _warlordWorkshops.Values.Sum(l => l.Count);
            int active = _warlordWorkshops.Count(kv => kv.Value.Count > 0);
            float gpd  = _warlordWorkshops.Values.SelectMany(l => l)
                             .Sum(ws => BASE_GOLD_PER_LEVEL * ws.Level * TypeGoldMult(ws.Type));
            int maxed  = _warlordWorkshops.Values.SelectMany(l => l).Count(ws => ws.Level == 3);
            return $"WarlordWorkshop: {total} shops ({maxed} maxed) / {active} warlords | ~{gpd:F0} g/day";
        }
    }
}


