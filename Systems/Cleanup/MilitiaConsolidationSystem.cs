using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Config;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Fear;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Systems.WarlordLegitimacy;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Cleanup
{
    [BanditMilitias.Core.Components.AutoRegister(Priority = 150, IsCritical = false)]
    public class MilitiaConsolidationSystem : MilitiaModuleBase
    {
        public override string ModuleName => "ConsolidationSystem";
        public override bool IsEnabled => Settings.Instance?.MilitiaSpawn ?? true;
        public override int Priority => 40;

        private static readonly Lazy<MilitiaConsolidationSystem> _instance =
            new Lazy<MilitiaConsolidationSystem>(() => new MilitiaConsolidationSystem());
        public static MilitiaConsolidationSystem Instance => _instance.Value;

        private const int INFIGHTING_THRESHOLD = 1200;
        private const int CONSOLIDATION_THRESHOLD = 2000;
        private const float MIN_FEAR_DRAFT = 0.65f;

        public override void OnHourlyTick()
        {
            if (!IsEnabled || ModActivationManager.IsGameplayActivationDelayed()) return;

            int totalParties = Campaign.Current.MobileParties.Count;


            if (totalParties > INFIGHTING_THRESHOLD && CampaignTime.Now.GetHourOfDay % 6 == 0)
            {
                PerformVanillaDrafting();
            }


            if (totalParties > CONSOLIDATION_THRESHOLD)
            {
                PerformConsolidation(totalParties - 1800);
            }


            PerformRapidMerge();


            UpdateMagnetSignals();
        }

        private void PerformRapidMerge()
        {


            var smallMilitias = ModuleManager.Instance.ActiveMilitias
                .Where(m => m.IsActive && m.MemberRoster.TotalManCount <= 12 && m.LeaderHero == null)
                .ToList();

            foreach (var source in smallMilitias)
            {
                if (!source.IsActive) continue;


                var target = ModuleManager.Instance.ActiveMilitias
                    .Where(m => m != source && m.IsActive)
                    .OrderByDescending(m => m.MemberRoster.TotalManCount)

                    .ThenBy(m => CompatibilityLayer.GetPartyPosition(m).DistanceSquared(CompatibilityLayer.GetPartyPosition(source)))
                    .FirstOrDefault();

                if (target != null)
                {
                    float distSq = CompatibilityLayer.GetPartyPosition(source).DistanceSquared(CompatibilityLayer.GetPartyPosition(target));


                    float mergeRange = (source.MemberRoster.TotalManCount <= 5) ? 400f : 100f;

                    if (distSq < mergeRange)
                    {
                        MergeParties(source, target);
                        CheckAndAssignCaptain(target);
                    }
                }
            }
        }

        private void UpdateMagnetSignals()
        {


            foreach (var party in ModuleManager.Instance.ActiveMilitias)
            {
                if (party == null || !party.IsActive || party.MemberRoster.TotalManCount >= 25 || party.MapEvent != null) continue;

                var comp = party.PartyComponent as MilitiaPartyComponent;
                if (comp == null || comp.HomeSettlement == null) continue;


                if (party.CurrentSettlement == null && party.ShortTermTargetSettlement != comp.HomeSettlement)
                {
                    party.SetMoveGoToSettlement(comp.HomeSettlement, default, true);
                    if (Settings.Instance?.TestingMode == true)
                        DebugLogger.TestLog($"[MAGNET] {party.Name} is being pulled towards the hideout (Swarming).");
                }
            }
        }

        private void CheckAndAssignCaptain(MobileParty party)
        {
            if (party.MemberRoster.TotalManCount >= 25 && party.LeaderHero == null)
            {


                WarlordSystem.Instance.TryAssignCaptainToParty(party);
            }
        }

        private void PerformVanillaDrafting()
        {
            var fearSystem = FearSystem.Instance;
            if (fearSystem == null || !fearSystem.IsEnabled) return;

            foreach (var settlement in ModuleManager.Instance.VillageCache.Concat(ModuleManager.Instance.TownCache))
            {
                if (settlement == null || !settlement.IsActive) continue;

                float fear = fearSystem.GetSettlementFear(settlement.StringId);
                string? warlordId = fearSystem.GetControllingWarlordId(settlement.StringId);

                if (fear >= MIN_FEAR_DRAFT && !string.IsNullOrEmpty(warlordId))
                {


                    int draftCount = Math.Max(2, (int)(settlement.Militia * 0.05f));
                    if (draftCount > 10) draftCount = 10;

                    if (settlement.Militia >= draftCount)
                    {
                        var nearestMilitia = FindNearestWarlordMilitia(settlement, warlordId!);
                        if (nearestMilitia != null)
                        {
                            DraftToMilitia(settlement, nearestMilitia, draftCount);
                        }
                    }
                }
            }
        }

        private MobileParty? FindNearestWarlordMilitia(Settlement s, string warlordId)
        {
            float minDist = 1000000f;
            MobileParty? best = null;
            Vec2 sPos = CompatibilityLayer.GetSettlementPosition(s);

            foreach (var m in ModuleManager.Instance.ActiveMilitias)
            {
                if (m == null || !m.IsActive) continue;
                if (m.PartyComponent is MilitiaPartyComponent comp && comp.WarlordId == warlordId)
                {
                    float dist = CompatibilityLayer.GetPartyPosition(m).DistanceSquared(sPos);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        best = m;
                    }
                }
            }
            return best;
        }

        private void DraftToMilitia(Settlement s, MobileParty m, int count)
        {
            try
            {


                s.Militia -= count;


                var troop = Globals.BasicInfantry.FirstOrDefault() ?? CharacterObject.All.FirstOrDefault(c => c.IsSoldier && c.Level < 10);
                if (troop != null)
                {
                    m.MemberRoster.AddToCounts(troop, count);
                }

                if (Settings.Instance?.TestingMode == true)
                    DebugLogger.Info("Consolidation", $"Drafted {count} troops from {s.Name} to {m.Name}. Fear: {FearSystem.Instance.GetSettlementFear(s.StringId):P0}");
            }
            catch (Exception ex)
            {
                DebugLogger.Error("Consolidation", $"Drafting failed: {ex.Message}");
            }
        }

        private void PerformConsolidation(int targetReduction)
        {
            var militias = ModuleManager.Instance.ActiveMilitias
                .Where(m => m.IsActive && m.MemberRoster.TotalManCount < 30)
                .OrderBy(m => m.MemberRoster.TotalManCount)
                .Take(Math.Min(targetReduction, 20))
                .ToList();

            foreach (var source in militias)
            {
                var target = FindConsolidationTarget(source);
                if (target != null)
                {
                    MergeParties(source, target);
                }
            }
        }

        private MobileParty? FindConsolidationTarget(MobileParty source)
        {
            return ModuleManager.Instance.ActiveMilitias
                .Where(m => m != source && m.IsActive && m.MemberRoster.TotalManCount >= 50)
                .OrderBy(m => CompatibilityLayer.GetPartyPosition(m).DistanceSquared(CompatibilityLayer.GetPartyPosition(source)))
                .FirstOrDefault();
        }

        private void MergeParties(MobileParty source, MobileParty target)
        {
            try
            {
                target.MemberRoster.Add(source.MemberRoster);
                target.PrisonRoster.Add(source.PrisonRoster);

                if (source.PartyComponent is MilitiaPartyComponent sComp && target.PartyComponent is MilitiaPartyComponent tComp)
                {
                    tComp.Gold += sComp.Gold;


                    if (!string.IsNullOrEmpty(tComp.WarlordId))
                    {
                        var warlord = WarlordSystem.Instance.GetWarlord(tComp.WarlordId);
                        if (warlord != null)
                        {
                            WarlordLegitimacySystem.Instance.ApplyPoints(warlord, 15f, "Consolidation (Force Merge)");
                        }
                    }
                }

                if (Settings.Instance?.TestingMode == true)
                    DebugLogger.Info("Consolidation", $"Merged {source.Name} into {target.Name}. New size: {target.MemberRoster.TotalManCount}");

                CompatibilityLayer.DestroyParty(source);
            }
            catch (Exception ex)
            {
                DebugLogger.Error("Consolidation", $"Merge failed: {ex.Message}");
                // If the merge threw after troop transfer but before DestroyParty,
                // source is still Active but possibly empty — destroy it to prevent a zombie.
                try
                {
                    if (source != null && source.IsActive)
                        CompatibilityLayer.DestroyParty(source);
                }
                catch (Exception destroyEx)
                {
                    DebugLogger.Warning("Consolidation", $"Zombie cleanup also failed for {source?.Name}: {destroyEx.Message}");
                }
            }
        }

        public void ConsolidateToReserves(MobileParty party)
        {
            if (party == null || !party.IsActive) return;

            try
            {
                if (party.PartyComponent is MilitiaPartyComponent comp && !string.IsNullOrEmpty(comp.WarlordId))
                {
                    var warlord = WarlordSystem.Instance.GetWarlord(comp.WarlordId);
                    if (warlord != null)
                    {


                        int totalTroops = party.MemberRoster.TotalManCount;
                        warlord.ReserveManpower += totalTroops;


                        warlord.Gold += comp.Gold;

                        if (Settings.Instance?.TestingMode == true)
                        {
                            DebugLogger.Info("Consolidation", $"[REINTEGRATION] {party.Name} consolidated to {warlord.Name}'s reserves. (+{totalTroops} manpower)");
                        }
                    }
                }

                CompatibilityLayer.DestroyParty(party);
            }
            catch (Exception ex)
            {
                DebugLogger.Error("Consolidation", $"ConsolidateToReserves failed for {party.Name}: {ex.Message}");
            }
        }
    }
}


