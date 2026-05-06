// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
// DEVRE DIÅI â€” MilitiaProgressionSystem.cs ile birleÅŸtirildi
// Bu dosya [AutoRegister] taÅŸÄ±mÄ±yor; ModuleManager tarafÄ±ndan yÃ¼klenmez.
// TÃ¼m iÅŸlevsellik MilitiaProgressionSystem.TryUpgradeRoster() iÃ§indedir.
// DÃ¼zeltilen hatalar:
//   â€¢ BUG: Upgrade sonrasÄ± XP sÄ±fÄ±rlanmÄ±yordu (slot tutarsÄ±zlÄ±ÄŸÄ±)
//   â€¢ BUG: Upgrade tetikleyicisi olmadÄ±ÄŸÄ± iÃ§in gÃ¼nlÃ¼k gecikme yaÅŸanÄ±yordu
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
    public sealed class MilitiaUpgradeSystem : MilitiaModuleBase
    {
        private static readonly Lazy<MilitiaUpgradeSystem> _inst =
            new(() => new MilitiaUpgradeSystem());
        public static MilitiaUpgradeSystem Instance => _inst.Value;

        public override string ModuleName => "MilitiaUpgradeSystem_LEGACY";
        public override bool IsEnabled => false;
        public override int Priority => 85;

        // Eski Ã§aÄŸrÄ± noktasÄ± varsa yÃ¶nlendirme kÃ¶prÃ¼sÃ¼
        [Obsolete("MilitiaProgressionSystem doÄŸrudan OnBattleVictory/OnDailyTick Ã¼zerinden upgrade yapÄ±yor.")]
        public void UpgradePartyTroops(MobileParty party, Warlord warlord)
        {
            // Legacy bridge: mantik tekrari yok, tek kaynak MilitiaProgressionSystem.
            MilitiaProgressionSystem.Instance.UpgradePartyTroopsCompat(party, warlord);
        }

        public override void OnDailyTick() { }
        public override void OnTick(float dt) { }
        public override void OnHourlyTick() { }
        public override string GetDiagnostics() => "MilitiaUpgradeSystem: DEVRE DIÅI";
        public override void SyncData(IDataStore ds) { }
        public override void Cleanup() { }
    }
}


