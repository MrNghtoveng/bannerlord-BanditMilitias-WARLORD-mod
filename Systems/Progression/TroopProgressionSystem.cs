using BanditMilitias.Core.Components;
using BanditMilitias.Intelligence.Strategic;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace BanditMilitias.Systems.Progression
{
    [Obsolete("Use MilitiaProgressionSystem. This class is kept for transitional reference.")]
    public sealed class TroopProgressionSystem : MilitiaModuleBase
    {
        private static readonly Lazy<TroopProgressionSystem> _inst =
            new(() => new TroopProgressionSystem());
        public static TroopProgressionSystem Instance => _inst.Value;

        public override string ModuleName => "TroopProgressionSystem_LEGACY";
        public override bool IsEnabled => false;
        public override int Priority => 80;

        [Obsolete("Use MilitiaProgressionSystem.Instance.AddToHordePool.")]
        public void AddToSharedPool(Warlord warlord, int amount)
            => MilitiaProgressionSystem.Instance.AddToHordePool(warlord, amount);

        public override void OnDailyTick() { }
        public override void OnTick(float dt) { }
        public override void OnHourlyTick() { }
        public override string GetDiagnostics() => "TroopProgressionSystem: DEPRECATED";
        public override void SyncData(IDataStore ds) { }
        public override void Cleanup() { }
    }
}
