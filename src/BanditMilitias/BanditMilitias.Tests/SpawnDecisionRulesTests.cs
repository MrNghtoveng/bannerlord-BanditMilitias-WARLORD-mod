using BanditMilitias.Systems.Spawning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class SpawnDecisionRulesTests
    {
        [DataTestMethod]
        [DataRow(0.99f, false)]
        [DataRow(1.0f, true)]
        [DataRow(2.5f, true)]
        public void ShouldResetDailySpawnCounter_ReturnsExpected(float elapsedDays, bool expected)
        {
            bool result = SpawnDecisionRules.ShouldResetDailySpawnCounter(elapsedDays);
            Assert.AreEqual(expected, result);
        }
    }
}