using TaleWorlds.CampaignSystem.Party;
using BanditMilitias.Behaviors;

namespace BanditMilitias.Components
{
    public static class MilitiaExtensions
    {
        /// <summary>
        /// Retrieves the runtime MilitiaPartyComponent for a MobileParty if it is a militia.
        /// </summary>
        public static MilitiaPartyComponent? GetMilitiaComponent(this MobileParty? party)
        {
            if (party == null) return null;
            return MilitiaBehavior.Instance?.GetMilitiaComponent(party);
        }

        /// <summary>
        /// Checks if the MobileParty is a tracked militia.
        /// </summary>
        public static bool IsMilitia(this MobileParty? party)
        {
            return GetMilitiaComponent(party) != null;
        }
    }
}
