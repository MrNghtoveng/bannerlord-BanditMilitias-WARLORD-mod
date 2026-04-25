using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.AI;
using BanditMilitias.Systems.Progression;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class AdaptiveDoctrineRulesTests
    {
        [DataTestMethod]
        [DataRow(0.60f, 0.10f, 0.05f, "InfantryWall")]
        [DataRow(0.10f, 0.50f, 0.05f, "RangedSkirmish")]
        [DataRow(0.10f, 0.10f, 0.50f, "CavalryShock")]
        [DataRow(0.30f, 0.30f, 0.30f, "MixedArms")]
        [DataRow(0.05f, 0.05f, 0.05f, "Unknown")]
        public void InferPlayerDoctrine_CorrectClassification(
            float inf, float rng, float cav, string expectedName)
        {
            var expected = (PlayerCombatDoctrine)System.Enum.Parse(typeof(PlayerCombatDoctrine), expectedName);
            Assert.AreEqual(expected, AdaptiveDoctrineRules.InferPlayerDoctrine(inf, rng, cav));
        }

        [TestMethod]
        public void InferPlayerDoctrine_CavalryDominates()
        {
            Assert.AreEqual(PlayerCombatDoctrine.CavalryShock,
                AdaptiveDoctrineRules.InferPlayerDoctrine(0.40f, 0.10f, 0.45f));
        }

        [TestMethod]
        public void InferPlayerDoctrine_NegativeRatios_ClampsToZero_ReturnsUnknown()
        {
            Assert.AreEqual(PlayerCombatDoctrine.Unknown,
                AdaptiveDoctrineRules.InferPlayerDoctrine(-0.5f, -0.5f, -0.5f));
        }

        [TestMethod]
        public void DetermineCounterDoctrine_HighThreat_AlwaysDefensiveDepth()
        {
            foreach (PlayerCombatDoctrine doctrine in System.Enum.GetValues(typeof(PlayerCombatDoctrine)))
            {
                var counter = AdaptiveDoctrineRules.DetermineCounterDoctrine(
                    doctrine, PlayStyle.Balanced, PersonalityType.Cunning, 2.20f, LegitimacyLevel.Warlord);
                Assert.AreEqual(CounterDoctrine.DefensiveDepth, counter,
                    $"High threat must always return DefensiveDepth, got {counter} for {doctrine}");
            }
        }

        [DataTestMethod]
        [DataRow(PlayerCombatDoctrine.CavalryShock, CounterDoctrine.SpearWall)]
        [DataRow(PlayerCombatDoctrine.RangedSkirmish, CounterDoctrine.FastFlank)]
        [DataRow(PlayerCombatDoctrine.InfantryWall, CounterDoctrine.HarassScreen)]
        public void DetermineCounterDoctrine_StandardMapping(PlayerCombatDoctrine observed, CounterDoctrine expected)
        {
            var counter = AdaptiveDoctrineRules.DetermineCounterDoctrine(
                observed, PlayStyle.Balanced, PersonalityType.Vengeful, 1.0f, LegitimacyLevel.Warlord);
            Assert.AreEqual(expected, counter);
        }

        [TestMethod]
        public void GetDecisionModifier_SpearWall_PositiveForDefend()
        {
            Assert.IsTrue(AdaptiveDoctrineRules.GetDecisionModifier(CounterDoctrine.SpearWall, AIDecisionType.Defend, true) > 0f);
        }

        [TestMethod]
        public void GetDecisionModifier_SpearWall_PenalisesRaid()
        {
            Assert.IsTrue(AdaptiveDoctrineRules.GetDecisionModifier(CounterDoctrine.SpearWall, AIDecisionType.Raid, true) < 0f);
        }

        [TestMethod]
        public void GetDecisionModifier_NonRaiderRaid_LowerThanRaider()
        {
            float raider   = AdaptiveDoctrineRules.GetDecisionModifier(CounterDoctrine.Balanced, AIDecisionType.Raid, true);
            float guardian = AdaptiveDoctrineRules.GetDecisionModifier(CounterDoctrine.Balanced, AIDecisionType.Raid, false);
            Assert.IsTrue(guardian < raider, "Guardian Raid modifier must be lower than raider's.");
        }

        [TestMethod]
        public void GetThreatRatioMultiplier_DefensiveDepth_BelowOne()
        {
            Assert.IsTrue(AdaptiveDoctrineRules.GetThreatRatioMultiplier(CounterDoctrine.DefensiveDepth) < 1f);
        }

        [TestMethod]
        public void GetThreatRatioMultiplier_ShockRaid_AboveOne()
        {
            Assert.IsTrue(AdaptiveDoctrineRules.GetThreatRatioMultiplier(CounterDoctrine.ShockRaid) > 1f);
        }

        [TestMethod]
        public void GetChaseDistanceMultiplier_FastFlank_HigherForRaiders()
        {
            float raider   = AdaptiveDoctrineRules.GetChaseDistanceMultiplier(CounterDoctrine.FastFlank, true);
            float guardian = AdaptiveDoctrineRules.GetChaseDistanceMultiplier(CounterDoctrine.FastFlank, false);
            Assert.IsTrue(raider > guardian);
        }

        [TestMethod]
        public void GetChaseDistanceMultiplier_DefensiveDepth_BelowOne()
        {
            Assert.IsTrue(AdaptiveDoctrineRules.GetChaseDistanceMultiplier(CounterDoctrine.DefensiveDepth, true) < 1f);
        }

        [TestMethod]
        public void ShouldSwitchDoctrine_SameDoctrine_NeverSwitches()
        {
            Assert.IsFalse(AdaptiveDoctrineRules.ShouldSwitchDoctrine(
                CounterDoctrine.Balanced, CounterDoctrine.Balanced, 0.9f, 100f, 1f));
        }

        [TestMethod]
        public void ShouldSwitchDoctrine_CooldownNotPassed_ReturnsFalse()
        {
            Assert.IsFalse(AdaptiveDoctrineRules.ShouldSwitchDoctrine(
                CounterDoctrine.Balanced, CounterDoctrine.SpearWall, 0.9f,
                hoursSinceSwitch: 2f, cooldownHours: 10f));
        }

        [TestMethod]
        public void ShouldSwitchDoctrine_LowConfidence_TriggersSwitch()
        {
            Assert.IsTrue(AdaptiveDoctrineRules.ShouldSwitchDoctrine(
                CounterDoctrine.Balanced, CounterDoctrine.SpearWall, 0.30f, 10f, 5f));
        }

        [TestMethod]
        public void UpdateConfidence_Success_Increases()
        {
            float before = 0.5f;
            Assert.IsTrue(AdaptiveDoctrineRules.UpdateConfidence(before, true, 1.0f) > before);
        }

        [TestMethod]
        public void UpdateConfidence_Failure_Decreases()
        {
            float before = 0.5f;
            Assert.IsTrue(AdaptiveDoctrineRules.UpdateConfidence(before, false, 1.0f) < before);
        }

        [TestMethod]
        public void UpdateConfidence_StaysWithinBounds()
        {
            float high = AdaptiveDoctrineRules.UpdateConfidence(0.98f, true, 1.0f);
            float low  = AdaptiveDoctrineRules.UpdateConfidence(0.06f, false, 1.0f);
            Assert.IsTrue(high <= 1.0f);
            Assert.IsTrue(low  >= 0.05f);
        }

        [TestMethod]
        public void UpdateConfidence_ZeroLearningRate_NoChange()
        {
            float before = 0.6f;
            Assert.AreEqual(before, AdaptiveDoctrineRules.UpdateConfidence(before, true, 0f), 0.001f);
        }
    }
}
