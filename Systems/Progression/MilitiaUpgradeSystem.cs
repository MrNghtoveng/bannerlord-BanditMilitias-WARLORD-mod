using BanditMilitias.Core.Components;
using BanditMilitias.Intelligence.Strategic;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace BanditMilitias.Systems.Progression
{


    [Obsolete("Use MilitiaProgressionSystem instead. This class is kept for legacy compatibility.")]
    public sealed class MilitiaUpgradeSystem : MilitiaModuleBase
    {
        private static readonly Lazy<MilitiaUpgradeSystem> _inst =
            new(() => new MilitiaUpgradeSystem());
        public static MilitiaUpgradeSystem Instance => _inst.Value;

        public override string ModuleName => "MilitiaUpgradeSystem_LEGACY";
        public override bool IsEnabled => false;
        public override int Priority => 85;

        [Obsolete("MilitiaProgressionSystem handles upgrades directly via OnBattleVictory/OnDailyTick.")]
        public void UpgradePartyTroops(MobileParty party, Warlord warlord)
        {
            MilitiaProgressionSystem.Instance.UpgradePartyTroopsCompat(party, warlord);
        }

        public override void OnDailyTick() { }
        public override void OnTick(float dt) { }
        public override void OnHourlyTick() { }
        public override string GetDiagnostics() => "MilitiaUpgradeSystem: DISABLED";
        public override void SyncData(IDataStore ds) { }
        public override void Cleanup() { }
    }
}
