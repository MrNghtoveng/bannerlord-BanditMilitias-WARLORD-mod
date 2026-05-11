using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Systems.Behavior
{
    [BanditMilitias.Core.Components.ModuleDependency(
        typeof(BanditMilitias.Intelligence.Strategic.WarlordSystem),
        typeof(BanditMilitias.Systems.Progression.WarlordCareerSystem))]
    [BanditMilitias.Core.Components.AutoRegister(Priority = 82, IsCritical = false)]
    public class WarlordBehaviorSystem : MilitiaModuleBase
    {
        public override string ModuleName => "WarlordBehaviorSystem";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 82;

        private static readonly Lazy<WarlordBehaviorSystem> _instance =
            new Lazy<WarlordBehaviorSystem>(() => new WarlordBehaviorSystem());
        public static WarlordBehaviorSystem Instance => _instance.Value;


        private Dictionary<string, CampaignTime> _lastVassalScanTime = new();
        private const float VASSAL_SCAN_INTERVAL_DAYS = 7f;

        private WarlordBehaviorSystem() { }

        public override void Initialize()
        {
            BanditMilitias.Core.Events.EventBus.Instance.Subscribe<SiegePreparationReadyEvent>(OnSiegeReady);
        }

        public override void Cleanup()
        {
            BanditMilitias.Core.Events.EventBus.Instance.Unsubscribe<SiegePreparationReadyEvent>(OnSiegeReady);
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled) return;


            foreach (var w in WarlordSystem.Instance.GetAllWarlords())
            {

                var tier = WarlordCareerSystem.Instance.GetTier(w.StringId);

                if (tier >= CareerTier.FamousBandit)

                {
                    ProcessVillageInfluence(w);
                }

                if (tier >= CareerTier.Warlord)

                {
                    ProcessLordBehaviors(w, tier);
                    ProcessPropertyGovernance(w);
                }
            }
        }

        private void ProcessVillageInfluence(Warlord w)
        {
            if (w.InfluencedVillages == null || w.InfluencedVillages.Count == 0) return;

            foreach (var v in w.InfluencedVillages)
            {
                if (v == null || v.IsUnderSiege || v.Village?.VillageState != Village.VillageStates.Normal) continue;


                if (MBRandom.RandomFloat < 0.14f)

                {
                    float tax = 150f + (MBRandom.RandomFloat * 300f);
                    w.Gold += tax;

                    if (Settings.Instance?.TestingMode == true)
                    {
                        DebugLogger.TestLog($"[ECONOMY] {w.FullName} collected {tax:F0} gold tax from {v.Name}.", Colors.Yellow);
                    }
                }


                if (MBRandom.RandomFloat < 0.05f)
                {


                    var recruit = v.Culture?.BasicTroop;
                    if (recruit != null)
                    {
                        var party = w.CommandedMilitias.OrderBy(p => p.MemberRoster.TotalManCount).FirstOrDefault();


                        int limit = party?.Party != null ? Infrastructure.CompatibilityLayer.GetPartyMemberSizeLimit(party.Party) : 50;
                        if (party != null && party.MemberRoster.TotalManCount < limit)
                        {
                            party.MemberRoster.AddToCounts(recruit, 1);
                        }
                    }
                }
            }
        }

        private void ProcessPropertyGovernance(Warlord w)
        {
            if (w.OwnedSettlement == null) return;


            float income = w.OwnedSettlement.IsTown ? 1200f : 500f;
            w.Gold += income;


            if (w.OwnedSettlement.Town?.GarrisonParty != null)
            {
                var garrison = w.OwnedSettlement.Town.GarrisonParty;
                if (garrison.MemberRoster.TotalManCount < 200)
                {


                    var banditType = CharacterObject.All.FirstOrDefault(c => c.Occupation == Occupation.Bandit && c.Culture == w.OwnedSettlement.Culture) ??
                                     CharacterObject.All.FirstOrDefault(c => c.Occupation == Occupation.Bandit);
                    if (banditType != null)
                    {
                        garrison.MemberRoster.AddToCounts(banditType, 2);
                    }
                }
            }
        }

        private void ProcessLordBehaviors(Warlord w, CareerTier tier)
        {


            if (tier >= CareerTier.Recognized)
            {
                ManageVassals(w);
            }


            if (tier >= CareerTier.Recognized && w.AssignedHideout != null)
            {
                AssessGarrison(w);
            }


            if (tier >= CareerTier.Conqueror)
            {
                ProcessRoyalStrategy(w);
            }
        }

        private void ManageVassals(Warlord king)
        {


            if (_lastVassalScanTime.TryGetValue(king.StringId, out var lastScan))
            {
                if ((CampaignTime.Now - lastScan).ToDays < VASSAL_SCAN_INTERVAL_DAYS)
                    return;
            }
            _lastVassalScanTime[king.StringId] = CampaignTime.Now;

            if (king.AssignedHideout == null) return;
            var kingPos = CompatibilityLayer.GetSettlementPosition(king.AssignedHideout);


            foreach (var w in WarlordSystem.Instance.GetAllWarlords())
            {
                if (w.StringId == king.StringId || !string.IsNullOrEmpty(w.VassalOf)) continue;
                if (w.AssignedHideout == null) continue;

                float dist = kingPos.Distance(
                    CompatibilityLayer.GetSettlementPosition(w.AssignedHideout));

                if (dist < 40f)
                {
                    w.VassalOf = king.StringId;
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[Diplomacy] {w.Name} has pledged allegiance to {king.Name}!", Colors.Magenta));
                    DebugLogger.Info("WarlordBehavior",
                        $"[VASSAL] {w.Name} → {king.Name} (dist={dist:F0})");
                }
            }
        }

        public override void SyncData(IDataStore dataStore)
        {
            _ = dataStore.SyncData("_lastVassalScanTime_v1", ref _lastVassalScanTime);
            if (dataStore.IsLoading && _lastVassalScanTime == null)
                _lastVassalScanTime = new Dictionary<string, CampaignTime>();
        }

        private void AssessGarrison(Warlord w)
        {


            if (w.CommandedMilitias.Count >= 2 && w.AssignedHideout != null)
            {
                var patrolParty = w.CommandedMilitias[0];
                if (patrolParty.IsActive)
                {
                    CompatibilityLayer.SetMovePatrolAroundSettlement(patrolParty, w.AssignedHideout);
                }
            }
        }

        private void ProcessRoyalStrategy(Warlord king)
        {


            if (king.Gold < 50000) return;


            var kingFaction = king.AssignedHideout?.MapFaction;
            Settlement? weakestTown = null;
            int weakestGarrison = int.MaxValue;
            foreach (var s in BanditMilitias.Infrastructure.ModuleManager.Instance.TownCache)
            {
                if (s.IsHideout || s.MapFaction == null || s.MapFaction == kingFaction) continue;
                int garrison = s.Town?.GarrisonParty?.MemberRoster.TotalManCount ?? 999;
                if (garrison < weakestGarrison) { weakestGarrison = garrison; weakestTown = s; }
            }

            if (weakestTown != null && king.AssignedHideout != null)
            {
                float dist = CompatibilityLayer.GetSettlementPosition(king.AssignedHideout)
                    .Distance(CompatibilityLayer.GetSettlementPosition(weakestTown));

                if (dist < 60f)
                {


                    foreach (var m in king.CommandedMilitias)
                    {
                        CompatibilityLayer.SetMoveGoToSettlement(m, weakestTown);
                    }


                    var vassals = WarlordSystem.Instance.GetAllWarlords().Where(v => v.VassalOf == king.StringId);
                    foreach (var v in vassals)
                    {
                        foreach (var vm in v.CommandedMilitias)
                        {
                            CompatibilityLayer.SetMoveGoToSettlement(vm, weakestTown);
                        }
                    }
                }
            }
        }

        private void OnSiegeReady(SiegePreparationReadyEvent evt)
        {
            if (evt.Warlord == null) return;
            var king = evt.Warlord;

            InformationManager.DisplayMessage(new InformationMessage(
                $"[Siege] {king.Name} has completed siege preparations — forces are converging!",
                Colors.Red));


            var kingFaction = king.AssignedHideout?.MapFaction;
            Settlement? target = null;
            int weakestGarrison = int.MaxValue;

            foreach (var s in ModuleManager.Instance.TownCache)
            {
                if (s.IsHideout || s.MapFaction == null || s.MapFaction == kingFaction) continue;
                int garrison = s.Town?.GarrisonParty?.MemberRoster.TotalManCount ?? 999;
                if (garrison < weakestGarrison) { weakestGarrison = garrison; target = s; }
            }

            if (target == null || king.AssignedHideout == null) return;

            float dist = CompatibilityLayer.GetSettlementPosition(king.AssignedHideout)
                             .Distance(CompatibilityLayer.GetSettlementPosition(target));
            if (dist > 80f) return;


            foreach (var m in king.CommandedMilitias.Where(m => m?.IsActive == true))
                CompatibilityLayer.SetMoveGoToSettlement(m, target);


            foreach (var vassal in WarlordSystem.Instance.GetAllWarlords()
                         .Where(v => v.VassalOf == king.StringId && v.IsAlive))
            {
                foreach (var vm in vassal.CommandedMilitias.Where(m => m?.IsActive == true))
                    CompatibilityLayer.SetMoveGoToSettlement(vm, target);
            }


            foreach (var m in king.CommandedMilitias.Where(
                m => m?.IsActive == true && m.PartyComponent is MilitiaPartyComponent))
            {
                if (m.PartyComponent is MilitiaPartyComponent comp)
                    comp.WakeUp();
            }

            DebugLogger.Info("WarlordBehavior",
                $"[SIEGE] {king.Name} → {target.Name} (garrison={weakestGarrison}, dist={dist:F0})");
        }
    }
}


