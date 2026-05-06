// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// DEVRE DIÅI â€” MilitiaProgressionSystem.cs ile birleÅŸtirildi
// Bu dosya [AutoRegister] taÅŸÄ±mÄ±yor; ModuleManager tarafÄ±ndan yÃ¼klenmez.
// TÃ¼m iÅŸlevsellik MilitiaProgressionSystem iÃ§indedir:
//   â€¢ PassiveTraining   â†’ MilitiaProgressionSystem.OnDailyTick()
//   â€¢ DistributeHordeXp â†’ MilitiaProgressionSystem.DistributeHordePool()
//   â€¢ AddToSharedPool   â†’ MilitiaProgressionSystem.AddToHordePool()
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

using BanditMilitias.Core.Components;
using BanditMilitias.Intelligence.Strategic;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace BanditMilitias.Systems.Progression
{
    // [AutoRegister] kaldÄ±rÄ±ldÄ± â€” sistem pasif/Ã¶lÃ¼.
    [Obsolete("MilitiaProgressionSystem kullanÄ±n. Bu sÄ±nÄ±f geÃ§iÅŸ dÃ¶nemi referansÄ± iÃ§in tutulmaktadÄ±r.")]
    public sealed class TroopProgressionSystem : MilitiaModuleBase
    {
        private static readonly Lazy<TroopProgressionSystem> _inst =
            new(() => new TroopProgressionSystem());
        public static TroopProgressionSystem Instance => _inst.Value;

        public override string ModuleName => "TroopProgressionSystem_LEGACY";
        public override bool IsEnabled => false;
        public override int Priority => 80;

        /// <summary>
        /// YÃ¶nlendirme kÃ¶prÃ¼sÃ¼: Eski Ã§aÄŸrÄ± noktalarÄ±nÄ± kÄ±rmadan yeni sisteme yÃ¶nlendirir.
        /// </summary>
        [Obsolete("MilitiaProgressionSystem.Instance.AddToHordePool kullanÄ±n.")]
        public void AddToSharedPool(Warlord warlord, int amount)
            => MilitiaProgressionSystem.Instance.AddToHordePool(warlord, amount);

        public override void OnDailyTick() { }
        public override void OnTick(float dt) { }
        public override void OnHourlyTick() { }
        public override string GetDiagnostics() => "TroopProgressionSystem: DEVRE DIÅI";
        public override void SyncData(IDataStore ds) { }
        public override void Cleanup() { }
    }
}


