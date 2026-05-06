using System;
using System.Collections.Generic;
using System.Linq;
using BanditMilitias.Core.Events;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.WarlordLegitimacy;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace BanditMilitias.Systems.Progression
{
    /// <summary>Saveable record tracking a warlord's promotion history.</summary>
    public class AscensionRecord
    {
        [SaveableProperty(1)] public string WarlordId { get; set; } = "";
        [SaveableProperty(2)] public LegitimacyLevel HighestLevel { get; set; } = LegitimacyLevel.Outlaw;
        [SaveableProperty(3)] public int PromotionCount { get; set; }
        [SaveableProperty(4)] public CampaignTime LastPromotionTime { get; set; }
        [SaveableProperty(5)] public bool IsBlocked { get; set; }
    }

    /// <summary>
    /// Evaluates militia parties for warlord ascension, tracks dynamic troop
    /// requirements and promotion eligibility.  Accessible as a singleton via
    /// <see cref="Instance"/> for instance-based calls, or via the static
    /// <see cref="Evaluate"/> / <see cref="FindBestCandidate"/> helpers.
    /// </summary>
    public class AscensionEvaluator
    {
        // ── Singleton ────────────────────────────────────────────────────────
        private static readonly Lazy<AscensionEvaluator> _inst =
            new Lazy<AscensionEvaluator>(() => new AscensionEvaluator());
        public static AscensionEvaluator Instance => _inst.Value;

        // ── Thresholds ────────────────────────────────────────────────────────
        private const float MIN_SCORE_FOR_WARLORD = 200f;
        private const float MIN_GOLD_FOR_WARLORD  = 10000f;
        private const int   MIN_DAYS_ALIVE        = 14;

        // Dynamic requirement scaling: days without a new promotion push the bar up
        private readonly Dictionary<string, float> _dynamicScale = new();
        private readonly Dictionary<string, AscensionRecord> _records = new();

        // ── Static entry points (backwards compatibility) ─────────────────────
        public static AscensionResult Evaluate(MobileParty party)
            => Instance.EvaluateInternal(party);

        public static MobileParty? FindBestCandidate()
        {
            MobileParty? best      = null;
            float        bestScore = -1f;
            foreach (var party in ModuleManager.Instance.ActiveMilitias)
            {
                var result = Evaluate(party);
                if (result.IsSuccessful && result.Score > bestScore)
                {
                    bestScore = result.Score;
                    best      = party;
                }
            }
            return best;
        }

        // ── Instance methods ──────────────────────────────────────────────────

        /// <summary>
        /// Called every in-game day per warlord; recalculates the dynamic
        /// scaling factor based on how long the warlord has been at their
        /// current level.
        /// </summary>
        public void RecalculateDaily(Warlord warlord)
        {
            if (warlord == null) return;
            if (!_dynamicScale.ContainsKey(warlord.StringId))
                _dynamicScale[warlord.StringId] = 1.0f;

            // Increase requirements slightly each day (pressure to grow or fall)
            _dynamicScale[warlord.StringId] =
                Math.Min(2.0f, _dynamicScale[warlord.StringId] + 0.005f);
        }

        /// <summary>
        /// Returns the effective minimum troop count for the given base requirement,
        /// scaled by how long this warlord has been at their current tier.
        /// </summary>
        public int GetDynamicTroopRequirement(Warlord warlord, int baseRequirement)
        {
            if (warlord == null) return baseRequirement;
            float scale = _dynamicScale.TryGetValue(warlord.StringId, out float s) ? s : 1.0f;
            return (int)(baseRequirement * scale);
        }

        /// <summary>Returns true if this warlord is eligible for promotion to <paramref name="level"/>.</summary>
        public bool CanPromote(Warlord warlord, LegitimacyLevel level)
        {
            if (warlord == null) return false;
            if (_records.TryGetValue(warlord.StringId, out var rec) && rec.IsBlocked)
                return false;
            // Must not have already reached this level
            return WarlordLegitimacySystem.Instance.GetLevel(warlord.StringId) < level;
        }

        /// <summary>Records that the warlord received a promotion grant.</summary>
        public void OnPromotionGranted(Warlord warlord, LegitimacyLevel newLevel)
        {
            if (warlord == null) return;
            if (!_records.TryGetValue(warlord.StringId, out var rec))
            {
                rec = new AscensionRecord { WarlordId = warlord.StringId };
                _records[warlord.StringId] = rec;
            }
            rec.HighestLevel     = newLevel;
            rec.PromotionCount++;
            rec.LastPromotionTime = CampaignTime.Now;
            // Reset dynamic scaling after a successful promotion
            _dynamicScale[warlord.StringId] = 1.0f;
        }

        // ── Internal evaluation logic ─────────────────────────────────────────
        private AscensionResult EvaluateInternal(MobileParty party)
        {
            if (party?.PartyComponent is not Components.MilitiaPartyComponent comp)
                return AscensionResult.Fail("Invalid party component.");

            if (comp.AssignedWarlord != null)
                return AscensionResult.Fail("Already assigned to a Warlord.");

            if (comp.DaysAlive < MIN_DAYS_ALIVE)
                return AscensionResult.Fail($"Not experienced enough (Required: {MIN_DAYS_ALIVE} days).");

            if (party.MemberRoster.TotalManCount < 40)
                return AscensionResult.Fail("Insufficient troop count (At least 40 soldiers required).");

            float totalGold = party.LeaderHero?.Gold ?? 0;
            if (totalGold < MIN_GOLD_FOR_WARLORD)
                return AscensionResult.Fail($"Insufficient capital (Required: {MIN_GOLD_FOR_WARLORD} gold).");

            float score = CalculateAscensionScore(party, comp);
            if (score < MIN_SCORE_FOR_WARLORD)
                return AscensionResult.Fail($"Insufficient success score: {score:F1}/{MIN_SCORE_FOR_WARLORD}");

            if (comp.HomeSettlement == null || !comp.HomeSettlement.IsHideout || !comp.HomeSettlement.IsActive)
                return AscensionResult.Fail("Assigned hideout is invalid or inactive.");

            var existing = WarlordSystem.Instance.GetWarlordForHideout(comp.HomeSettlement);
            if (existing != null)
                return AscensionResult.Fail($"A Warlord ({existing.Name}) already rules in this hideout.");

            return AscensionResult.Pass(score);
        }

        private static float CalculateAscensionScore(MobileParty party, Components.MilitiaPartyComponent comp)
        {
            float score = 0f;
            score += comp.BattlesWon * 25f;
            score += comp.Renown * 5f;

            float avgTier = 1f;
            if (party.MemberRoster.TotalManCount > 0)
            {
                float totalTier = 0f;
                foreach (var element in party.MemberRoster.GetTroopRoster())
                    totalTier += element.Character.Tier * element.Number;
                avgTier = totalTier / party.MemberRoster.TotalManCount;
            }
            score += avgTier * 15f;

            if (party.LeaderHero != null)
            {
                score += party.LeaderHero.GetSkillValue(TaleWorlds.Core.DefaultSkills.Leadership) * 0.5f;
                score += party.LeaderHero.GetSkillValue(TaleWorlds.Core.DefaultSkills.Tactics) * 0.5f;
            }
            return score;
        }
    }

    public struct AscensionResult
    {
        public bool   IsSuccessful;
        public float  Score;
        public string Reason;

        public static AscensionResult Pass(float score)
            => new AscensionResult { IsSuccessful = true,  Score = score, Reason = "Ascension criteria met." };
        public static AscensionResult Fail(string reason)
            => new AscensionResult { IsSuccessful = false, Score = 0,     Reason = reason };
    }
}
