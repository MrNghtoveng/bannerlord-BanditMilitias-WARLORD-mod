using BanditMilitias.Components;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Party;

namespace BanditMilitias.Models
{
    /// <summary>
    /// Replaces MilitiaVisibilityPatch (Harmony) with a native GameModel override.
    /// Reduces spotting range of small militia parties to make them harder to detect.
    /// Registered via SystemInitCoordinator.RegisterGameModels.
    /// </summary>
    public class MilitiaVisibilityModel : DefaultMapVisibilityModel
    {
        // Removed IsPartyVisible override as it is not part of the MapVisibilityModel API in current Bannerlord versions.
        // Marker and Stealth logic should be handled via Harmony if a native GameModel equivalent is unavailable.

        public override ExplainedNumber GetPartySpottingRange(MobileParty party, bool includeDescriptions = false)
        {
            var result = base.GetPartySpottingRange(party, includeDescriptions);

            try
            {
                if (party?.PartyComponent is MilitiaPartyComponent &&
                    party.MemberRoster != null &&
                    party.MemberRoster.TotalManCount < 12)
                {
                    result.AddFactor(-0.4f, new TaleWorlds.Localization.TextObject("{=BM_Vis_Stealth}Bandit Stealth"));
                }
            }
            catch (Exception ex)
            {
                Infrastructure.FileLogger.LogWarning($"[MilitiaVisibilityModel] Error: {ex.Message}");
            }

            return result;
        }
    }
}
