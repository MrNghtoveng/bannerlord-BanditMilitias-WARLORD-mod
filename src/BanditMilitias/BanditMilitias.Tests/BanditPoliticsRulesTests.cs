using BanditMilitias.Systems.Progression;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Diplomacy;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class BanditPoliticsRulesTests
    {
        [DataTestMethod]
        [DataRow("b", "a", "a|b")]
        [DataRow("alpha", "beta", "alpha|beta")]
        [DataRow("same", "same", "same|same")]
        public void MakePairKey_IsDeterministic(string a, string b, string expected)
        {
            string key = BanditPoliticsRules.MakePairKey(a, b);
            Assert.AreEqual(expected, key);
        }

        [DataTestMethod]
        [DataRow(10f, 0.04f, -0.4f)]
        [DataRow(-20f, 0.05f, 1.0f)]
        [DataRow(0f, 0.20f, 0f)]
        [DataRow(50f, 0f, 0f)]
        public void GetDailyNeutralDrift_ReturnsExpected(float relation, float rate, float expected)
        {
            float drift = BanditPoliticsRules.GetDailyNeutralDrift(relation, rate);
            Assert.AreEqual(expected, drift, 0.001f);
        }

        [DataTestMethod]
        [DataRow(45f, false, false, 2, 1, 40f, true)]
        [DataRow(39f, false, false, 2, 1, 40f, false)]
        [DataRow(45f, true, false, 2, 1, 40f, false)]
        [DataRow(45f, false, true, 2, 1, 40f, false)]
        [DataRow(45f, false, false, 0, 1, 40f, false)]
        public void ShouldFormAlliance_RespectsGuards(
            float relation,
            bool allied,
            bool rivals,
            int militiasA,
            int militiasB,
            float threshold,
            bool expected)
        {
            bool result = BanditPoliticsRules.ShouldFormAlliance(
                relation,
                allied,
                rivals,
                militiasA,
                militiasB,
                threshold);

            Assert.AreEqual(expected, result);
        }

        [DataTestMethod]
        [DataRow(-40f, false, false, -35f, true)]
        [DataRow(-34f, false, false, -35f, false)]
        [DataRow(-40f, true, false, -35f, false)]
        [DataRow(-40f, false, true, -35f, false)]
        public void ShouldDeclareRivalry_RespectsGuards(
            float relation,
            bool allied,
            bool rivals,
            float threshold,
            bool expected)
        {
            bool result = BanditPoliticsRules.ShouldDeclareRivalry(
                relation,
                allied,
                rivals,
                threshold);

            Assert.AreEqual(expected, result);
        }

        [TestMethod]
        public void ComputeBetrayalChance_StaysWithinBounds_AndScales()
        {
            float low = BanditPoliticsRules.ComputeBetrayalChance(-10f, 0.10f, 0.10f, 0.10f);
            float high = BanditPoliticsRules.ComputeBetrayalChance(-80f, 0.90f, 0.90f, 1.00f);

            Assert.IsTrue(low >= 0f && low <= 1f);
            Assert.IsTrue(high >= 0f && high <= 1f);
            Assert.IsTrue(high > low);
        }

        [DataTestMethod]
        [DataRow(PersonalityType.Cunning, PersonalityType.Cautious, true)]
        [DataRow(PersonalityType.Aggressive, PersonalityType.Cautious, false)]
        public void GetPersonalityCompatibility_EncodesExpectedBias(
            PersonalityType a,
            PersonalityType b,
            bool shouldBePositive)
        {
            float score = BanditPoliticsRules.GetPersonalityCompatibility(a, b);
            if (shouldBePositive)
                Assert.IsTrue(score > 0f);
            else
                Assert.IsTrue(score < 0f);
        }
    }
}
