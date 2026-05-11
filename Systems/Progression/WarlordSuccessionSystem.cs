using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Progression
{


    [BanditMilitias.Core.Components.ModuleDependency(
        typeof(BanditMilitias.Intelligence.Strategic.WarlordSystem),
        typeof(BanditMilitias.Systems.Progression.WarlordCareerSystem))]
    [BanditMilitias.Core.Components.AutoRegister(Priority = 90, IsCritical = false)]
    public class WarlordSuccessionSystem : MilitiaModuleBase
    {
        public override string ModuleName => "WarlordSuccessionSystem";
        public override bool IsEnabled => Settings.Instance?.EnableWarlords ?? true;
        public override int Priority => 90;


        private static readonly Lazy<WarlordSuccessionSystem> _instance =
            new Lazy<WarlordSuccessionSystem>(() => new WarlordSuccessionSystem());
        public static WarlordSuccessionSystem Instance => _instance.Value;

        private Dictionary<string, string> _successionHistory = new();


        private const float SUCCESSION_TROOP_RATIO = 0.65f;

        private const float PRESTIGE_INHERITANCE_RATIO = 0.60f;

        private const int MIN_TIER_FOR_SUCCESSION = 1;

        private const int MIN_TROOPS_FOR_SUCCESSION = 20;


        private bool _initialized = false;

        private WarlordSuccessionSystem() { }

        public override void Initialize()
        {
            if (_initialized) return;
            BanditMilitias.Core.Events.EventBus.Instance.Subscribe<WarlordFallenEvent>(OnWarlordFallen);
            _initialized = true;
            DebugLogger.Info("Succession", "WarlordSuccessionSystem initialized.");
        }

        public override void Cleanup()
        {
            BanditMilitias.Core.Events.EventBus.Instance.Unsubscribe<WarlordFallenEvent>(OnWarlordFallen);
            _initialized = false;
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("WarlordSuccession_History_v1", ref _successionHistory);
        }

        private void OnWarlordFallen(WarlordFallenEvent evt)
        {
            if (evt?.Warlord == null) return;
            var fallen = evt.Warlord;


            int tier = (int)WarlordCareerSystem.Instance.GetOrCreate(fallen.StringId).Tier;
            if (tier < MIN_TIER_FOR_SUCCESSION) return;


            int totalTroops = CountWarlordTroops(fallen.StringId);
            if (totalTroops < MIN_TROOPS_FOR_SUCCESSION) return;

            TryCreateSuccessor(fallen, totalTroops);
        }

        private void TryCreateSuccessor(Warlord fallen, int totalTroops)
        {
            try
            {


                var warlordParties = CompatibilityLayer.GetSafeMobileParties()
                    .Where(p => p.PartyComponent is MilitiaPartyComponent comp
                             && comp.WarlordId == fallen.StringId
                             && p.MemberRoster.TotalManCount > 10)
                    .OrderByDescending(p => p.MemberRoster.TotalManCount)
                    .ToList();

                if (warlordParties.Count == 0) return;

                var coreParty = warlordParties[0];
                var coreComp = coreParty.PartyComponent as MilitiaPartyComponent;
                if (coreComp == null) return;


                Settlement? homeSettlement = coreComp.GetHomeSettlement()
                    ?? FindNearestHideout(CompatibilityLayer.GetPartyPosition(coreParty));

                if (homeSettlement == null) return;

                var successor = WarlordSystem.Instance.CreateWarlord(homeSettlement);
                if (successor == null) return;


                SetupSuccessor(successor, fallen, totalTroops);


                TransferTroops(fallen.StringId, successor.StringId, warlordParties);


                _successionHistory[fallen.StringId] = successor.StringId;


                NotifyPlayer(fallen, successor);

                DebugLogger.Info("Succession",
                    $"[SUCCESSOR] {fallen.Name} died → {successor.Name} took over leadership. " +
                    $"Transferred troops: {(int)(totalTroops * SUCCESSION_TROOP_RATIO)}");
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("Succession", $"Successor creation error: {ex.Message}");
            }
        }

        private void SetupSuccessor(Warlord successor, Warlord fallen, int totalTroops)
        {


            successor.Name = GenerateSuccessorName(fallen);
            int fallenTier = (int)WarlordCareerSystem.Instance.GetOrCreate(fallen.StringId).Tier;
            successor.Title = fallenTier >= 4 ? "Successor Captain" : "New Captain";


            successor.Gold = Math.Max(500f, fallen.Gold * PRESTIGE_INHERITANCE_RATIO);


            int fTier = (int)WarlordCareerSystem.Instance.GetOrCreate(fallen.StringId).Tier;
            var successorRecord = WarlordCareerSystem.Instance.GetOrCreate(successor.StringId);
            successorRecord.Tier = (CareerTier)Math.Max(2, fTier - 1);


            successor.Personality = fallen.Personality;
            successor.Backstory = fallen.Backstory;


            var tactics = coreParty_InheritedTactics(fallen);
            if (tactics != null && tactics.Count > 0)
            {


                DebugLogger.Info("Succession", $"Tactics inheritance: {tactics.Count} tactics transferred.");
            }
        }

        private Dictionary<string, float> coreParty_InheritedTactics(Warlord fallen)
        {


            var coreParty = CompatibilityLayer.GetSafeMobileParties()
                .Where(p => p.PartyComponent is MilitiaPartyComponent comp
                         && comp.WarlordId == fallen.StringId)
                .OrderByDescending(p => p.MemberRoster.TotalManCount)
                .FirstOrDefault();

            if (coreParty?.PartyComponent is MilitiaPartyComponent c)
                return c.InheritedTactics ?? new();

            return new();
        }

        private void TransferTroops(string fallenId, string successorId, List<MobileParty> parties)
        {
            int transferred = 0;

            foreach (var party in parties)
            {
                if (party.PartyComponent is not MilitiaPartyComponent comp) continue;


                if (MBRandom.RandomFloat < SUCCESSION_TROOP_RATIO)
                {
                    comp.WarlordId = successorId;
                    transferred += party.MemberRoster.TotalManCount;
                }


            }

            DebugLogger.Info("Succession",
                $"Troop transfer: {transferred} troops transferred to {successorId}.");
        }

        private static string GenerateSuccessorName(Warlord fallen)
        {
            string[] suffixes = { "the Second", "Successor", "Heir", "II", "Jr." };
            string suffix = suffixes[MBRandom.RandomInt(suffixes.Length)];
            return $"{fallen.Name.Split(' ')[0]} {suffix}";
        }

        private static int CountWarlordTroops(string warlordId)
        {
            return CompatibilityLayer.GetSafeMobileParties()
                .Where(p => p.PartyComponent is MilitiaPartyComponent comp
                         && comp.WarlordId == warlordId)
                .Sum(p => p.MemberRoster.TotalManCount);
        }

        private static Settlement? FindNearestHideout(Vec2 position)
        {
            return Settlement.All
                .Where(s => s.IsHideout)
                .OrderBy(s => CompatibilityLayer.GetSettlementPosition(s).Distance(position))
                .FirstOrDefault();
        }

        private static void NotifyPlayer(Warlord fallen, Warlord successor)
        {
            if (Settings.Instance?.TestingMode == true)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Politics] {fallen.FullName} fell — " +
                    $"{successor.Name} took over leadership and is rallying the troops.",
                    new Color(0.7f, 0.5f, 0.9f)));
            }
        }

        public bool HasSuccessor(string warlordId)
            => _successionHistory.ContainsKey(warlordId);

        public string? GetSuccessorId(string warlordId)
            => _successionHistory.TryGetValue(warlordId, out var s) ? s : null;

        public override string GetDiagnostics()
        {
            return $"WarlordSuccession:\n" +
                   $"  Recorded succession history: {_successionHistory.Count}\n" +
                   $"  Min tier: {MIN_TIER_FOR_SUCCESSION}, Min troops: {MIN_TROOPS_FOR_SUCCESSION}";
        }
    }
}


