using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Progression;
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
    [AutoRegister]
    public class WarlordBehaviorSystem : MilitiaModuleBase
    {
        public override string ModuleName => "WarlordBehaviorSystem";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 82;

        private static readonly Lazy<WarlordBehaviorSystem> _instance = 
            new Lazy<WarlordBehaviorSystem>(() => new WarlordBehaviorSystem());
        public static WarlordBehaviorSystem Instance => _instance.Value;

        private WarlordBehaviorSystem() { }

        public override void Initialize()
        {
            EventBus.Instance.Subscribe<SiegePreparationReadyEvent>(OnSiegeReady);
        }

        public override void Cleanup()
        {
            EventBus.Instance.Unsubscribe<SiegePreparationReadyEvent>(OnSiegeReady);
        }

        public override void OnDailyTick()
        {
            if (!IsEnabled) return;

            foreach (var w in WarlordSystem.Instance.GetAllWarlords())
            {
                if (!w.IsAlive) continue;

                var tier = WarlordCareerSystem.Instance.GetTier(w.StringId);
                
                if (tier >= CareerTier.FamousBandit) // T2+
                {
                    ProcessVillageInfluence(w);
                }

                if (tier >= CareerTier.Warlord) // T3+
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

                // Haftalık vergi toplama (DailyTick'te şans eseri)
                if (MBRandom.RandomFloat < 0.14f) // Haftada ~1 kez
                {
                    float tax = 150f + (MBRandom.RandomFloat * 300f);
                    w.Gold += tax;

                    if (Settings.Instance?.TestingMode == true)
                    {
                        DebugLogger.TestLog($"[ECONOMY] {w.FullName}, {v.Name} köyünden {tax:F0} altın vergi topladı.", Colors.Yellow);
                    }
                }

                // Asker toplama (Gönüllüler)
                if (MBRandom.RandomFloat < 0.05f) 
                {
                    // Bannerlord API: Volunteer types are usually per-hero, fallback to basic culture troop
                    var recruit = v.Culture?.BasicTroop;
                    if (recruit != null)
                    {
                        var party = w.CommandedMilitias.OrderBy(p => p.MemberRoster.TotalManCount).FirstOrDefault();
                        // Bannerlord 1.3.15+: Bridged via CompatibilityLayer (LimitedPartySize prioritized)
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

            // Şehir/Kale yönetimi geliri
            float income = w.OwnedSettlement.IsTown ? 1200f : 500f;
            w.Gold += income;

            // Garnizon güçlendirme
            if (w.OwnedSettlement.Town?.GarrisonParty != null)
            {
                var garrison = w.OwnedSettlement.Town.GarrisonParty;
                if (garrison.MemberRoster.TotalManCount < 200)
                {
                    // Otomatik haydut milis takviyesi
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
            // 1. Vassal Management (T5+)
            if (tier >= CareerTier.Taninmis)
            {
                ManageVassals(w);
            }

            // 2. Garrison Assessment (T5+)
            if (tier >= CareerTier.Taninmis && w.AssignedHideout != null)
            {
                AssessGarrison(w);
            }

            // 3. Strategic Movement (T6)
            if (tier >= CareerTier.Fatih)
            {
                ProcessRoyalStrategy(w);
            }
        }

        private void ManageVassals(Warlord king)
        {
            // Find lower tier warlords nearby and make them vassals
            var nearbyWarlords = WarlordSystem.Instance.GetAllWarlords()
                .Where(w => w.StringId != king.StringId && string.IsNullOrEmpty(w.VassalOf));

            foreach (var w in nearbyWarlords)
            {
                if (king.AssignedHideout != null && w.AssignedHideout != null)
                {
                    float dist = CompatibilityLayer.GetSettlementPosition(king.AssignedHideout)
                        .Distance(CompatibilityLayer.GetSettlementPosition(w.AssignedHideout));

                    if (dist < 40f) // Vassalizing range
                    {
                        w.VassalOf = king.StringId;
                        InformationManager.DisplayMessage(new InformationMessage(
                            $"[DIPLOMACY] {w.Name} has sworn fealty to {king.Name}!", Colors.Magenta));
                    }
                }
            }
        }

        private void AssessGarrison(Warlord w)
        {
            // Assign 1 militia to patrol around hideout
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
            // Find weakest town nearby and prepare siege
            if (king.Gold < 50000) return;

            var weakestTown = Settlement.All
                .Where(s => s.IsTown && !s.IsHideout && s.MapFaction != null && s.MapFaction != king.AssignedHideout?.MapFaction)
                .OrderBy(s => s.Town?.GarrisonParty?.MemberRoster.TotalManCount ?? 999)
                .FirstOrDefault();

            if (weakestTown != null && king.AssignedHideout != null)
            {
                float dist = CompatibilityLayer.GetSettlementPosition(king.AssignedHideout)
                    .Distance(CompatibilityLayer.GetSettlementPosition(weakestTown));

                if (dist < 60f)
                {
                    // Move forces towards the town
                    foreach (var m in king.CommandedMilitias)
                    {
                        CompatibilityLayer.SetMoveGoToSettlement(m, weakestTown);
                    }
                    
                    // Also move vassals
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

            InformationManager.DisplayMessage(new InformationMessage(
                $"[WAR] {evt.Warlord.Name} has completed siege preparations!", Colors.Red));
                
            // Trigger aggressive hunt/raid around target
        }
    }
}
