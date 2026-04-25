using BanditMilitias.Intelligence.AI;
using BanditMilitias.Components;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class DecisionRulesTests
    {
        [DataTestMethod]
        [DataRow(0, 0, false)]
        [DataRow(10, 0, false)]
        [DataRow(10, 7, false)]
        [DataRow(10, 8, true)]
        [DataRow(20, 12, false)]
        [DataRow(20, 14, true)]
        [DataRow(20, 21, true)]
        public void IsWounded_ReturnsExpected(int total, int wounded, bool expected)
        {
            bool result = DecisionRules.IsWounded(total, wounded);
            Assert.AreEqual(expected, result);
        }

        [DataTestMethod]
        [DataRow(100f, 50f, 1.6f, false)]
        [DataRow(100f, 170f, 1.6f, true)]
        [DataRow(0f, 10f, 1.6f, true)]
        [DataRow(100f, 0f, 1.6f, false)]
        public void IsOverwhelmingThreat_ReturnsExpected(float own, float enemy, float ratio, bool expected)
        {
            bool result = DecisionRules.IsOverwhelmingThreat(own, enemy, ratio);
            Assert.AreEqual(expected, result);
        }

        [DataTestMethod]
        [DataRow(100f, 80f, 1.1f, true)]
        [DataRow(100f, 95f, 1.1f, false)]
        [DataRow(0f, 50f, 1.1f, false)]
        [DataRow(50f, 0f, 1.1f, true)]
        public void IsAdvantageousEngagement_ReturnsExpected(float own, float enemy, float ratio, bool expected)
        {
            bool result = DecisionRules.IsAdvantageousEngagement(own, enemy, ratio);
            Assert.AreEqual(expected, result);
        }

        [TestMethod]
        public void GetChaseDistance_RoleSpecific()
        {
            Assert.AreEqual(25f, DecisionRules.GetChaseDistance(MilitiaPartyComponent.MilitiaRole.Raider, 0f));
            Assert.AreEqual(10f, DecisionRules.GetChaseDistance(MilitiaPartyComponent.MilitiaRole.Guardian, 0f));
        }
    }
}