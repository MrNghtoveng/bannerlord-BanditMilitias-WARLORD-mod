using BanditMilitias.Intelligence.Strategic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class WarlordProgressionRulesTests
    {
        [DataTestMethod]
        [DataRow(30, 3, 50, true)]
        [DataRow(29, 3, 50, false)]
        [DataRow(30, 2, 50, false)]
        [DataRow(30, 3, 49, false)]
        [DataRow(35, 5, 80, true)]
        public void CanPromoteCaptainToWarlord_RespectsThresholds(int daysAlive, int battlesWon, int troops, bool expected)
        {
            bool result = WarlordProgressionRules.CanPromoteCaptainToWarlord(
                isMilitiaActive: true,
                hasPartyComponent: true,
                alreadyPromoted: false,
                hasHomeSettlement: true,
                hasExistingWarlordForHideout: false,
                hasLeaderHero: true,
                isLeaderAlive: true,
                daysAlive: daysAlive,
                battlesWon: battlesWon,
                troopCount: troops,
                minDaysAlive: 30,
                minBattlesWon: 3,
                minTroops: 50);

            Assert.AreEqual(expected, result);
        }

        [DataTestMethod]
        [DataRow(false, true, false, true, false, true, true)]
        [DataRow(true, false, false, true, false, true, true)]
        [DataRow(true, true, true, true, false, true, true)]
        [DataRow(true, true, false, false, false, true, true)]
        [DataRow(true, true, false, true, true, true, true)]
        [DataRow(true, true, false, true, false, false, true)]
        [DataRow(true, true, false, true, false, true, false)]
        public void CanPromoteCaptainToWarlord_RejectsInvalidState(
            bool isMilitiaActive,
            bool hasPartyComponent,
            bool alreadyPromoted,
            bool hasHomeSettlement,
            bool hasExistingWarlordForHideout,
            bool hasLeaderHero,
            bool isLeaderAlive)
        {
            bool result = WarlordProgressionRules.CanPromoteCaptainToWarlord(
                isMilitiaActive,
                hasPartyComponent,
                alreadyPromoted,
                hasHomeSettlement,
                hasExistingWarlordForHideout,
                hasLeaderHero,
                isLeaderAlive,
                daysAlive: 99,
                battlesWon: 99,
                troopCount: 99,
                minDaysAlive: 30,
                minBattlesWon: 3,
                minTroops: 50);

            Assert.IsFalse(result);
        }

        [DataTestMethod]
        [DataRow(0, false, 59f, 60, false)]
        [DataRow(0, false, 60f, 60, true)]
        [DataRow(1, false, 80f, 60, false)]
        [DataRow(0, false, 100f, 100, true)]
        public void ShouldRunWarlordFallback_EarlySeedPhase_UsesZeroWarlordGate(int warlordCount, bool hasWarlord, float elapsedDays, int fallbackDays, bool expected)
        {
            bool result = WarlordProgressionRules.ShouldRunWarlordFallback(warlordCount, hasWarlord, elapsedDays, fallbackDays);
            Assert.AreEqual(expected, result);
        }

        [DataTestMethod]
        [DataRow(0, false, 200f, 150, false)]
        [DataRow(1, true, 200f, 150, false)]
        [DataRow(1, false, 140f, 150, false)]
        [DataRow(2, false, 150f, 150, true)]
        [DataRow(3, false, 220f, 150, true)]
        public void ShouldRunWarlordFallback_LateEscalationPhase_RequiresCriticalMass(
            int warlordCount,
            bool hasWarlord,
            float elapsedDays,
            int fallbackDays,
            bool expected)
        {
            bool result = WarlordProgressionRules.ShouldRunWarlordFallback(warlordCount, hasWarlord, elapsedDays, fallbackDays);
            Assert.AreEqual(expected, result);
        }

        [TestMethod]
        public void ComputeFallbackCandidateScore_UsesWeightedFormula()
        {
            int score = WarlordProgressionRules.ComputeFallbackCandidateScore(daysAlive: 10, battlesWon: 2, troopCount: 15);
            Assert.AreEqual(55, score);
        }

        [TestMethod]
        public void SelectTopFallbackCandidates_IsDeterministic()
        {
            var candidates = new List<TestCandidate>
            {
                new TestCandidate("b", 10, 2, 20),
                new TestCandidate("a", 10, 2, 20),
                new TestCandidate("c", 9, 3, 20),
                new TestCandidate("d", 10, 2, 25),
            };

            List<TestCandidate> selected = WarlordProgressionRules.SelectTopFallbackCandidates(
                candidates,
                maxCount: 3,
                idSelector: c => c.Id,
                daysSelector: c => c.Days,
                battlesSelector: c => c.Battles,
                troopSelector: c => c.Troops);

            string orderedIds = string.Join(",", selected.Select(s => s.Id));
            Assert.AreEqual("c,d,a", orderedIds);
        }

        private sealed class TestCandidate
        {
            public TestCandidate(string id, int days, int battles, int troops)
            {
                Id = id;
                Days = days;
                Battles = battles;
                Troops = troops;
            }

            public string Id { get; }
            public int Days { get; }
            public int Battles { get; }
            public int Troops { get; }
        }
    }
}
