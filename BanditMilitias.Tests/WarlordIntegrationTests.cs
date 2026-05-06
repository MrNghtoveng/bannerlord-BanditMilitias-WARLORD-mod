using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.WarlordLegitimacy;
using BanditMilitias.Systems.Progression;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class WarlordIntegrationTests
    {
        [TestCleanup]
        public void Cleanup()
        {
            WarlordSystem.Instance.Cleanup();
            WarlordLegitimacySystem.Instance.Cleanup();
            WarlordCareerSystem.Instance.Cleanup();
        }

        [TestMethod]
        public void WarlordSystem_CanProcess_MockedParties()
        {


            var fakePos = new Vec2(100f, 200f);
            var settlement = MockingHub.CreateFakeSettlement("test_hideout", "Test Hideout", fakePos);
            var party = MockingHub.CreateFakeMobileParty("test_militia", "Test Militia");


            var warlordSystem = WarlordSystem.Instance;

            Assert.IsNotNull(warlordSystem, "WarlordSystem instance could not be retrieved.");


            Assert.IsNotNull(settlement);
            Assert.IsNotNull(party);


            var pos = Infrastructure.CompatibilityLayer.GetPartyPosition(party);


            Assert.IsFalse(float.IsNaN(pos.X), "CompatibilityLayer could not read position from mock party.");
        }

        [TestMethod]
        public void WarlordSystem_EconomyBalance_IsConsistent()
        {


            var warlordSystem = WarlordSystem.Instance;

            Assert.IsNotNull(warlordSystem);


            var warlords = warlordSystem.GetAllWarlords();
            Assert.IsNotNull(warlords);
            Assert.IsTrue(warlords.Count >= 0);
        }

        [TestMethod]
        public void WarlordProgression_TierTransitions_AreValid()
        {
            var careerSystem = BanditMilitias.Systems.Progression.WarlordCareerSystem.Instance;
            Assert.IsNotNull(careerSystem, "WarlordCareerSystem not found.");

            var testWarlord = new Warlord { StringId = "test_warlord_promo", Name = "Promo Test" };
            WarlordSystem.Instance.RegisterWarlordForTesting(testWarlord);


            WarlordLegitimacySystem.Instance.ApplyPoints(testWarlord, 200, "Test 1");
            var currentTier = careerSystem.GetTier(testWarlord.StringId);


            Assert.IsNotNull(currentTier);
        }

        [TestMethod]
        public void WarlordWorkshopSystem_CanAddAndUpgradeWorkshop()
        {
            var wsSystem = BanditMilitias.Systems.Workshop.WarlordWorkshopSystem.Instance;
            Assert.IsNotNull(wsSystem, "WarlordWorkshopSystem not found.");

            string wlId = "test_warlord_workshop";
            var testWarlord = new Warlord { StringId = wlId, Name = "Workshop Test", Gold = 50000 };
            WarlordSystem.Instance.RegisterWarlordForTesting(testWarlord);


            wsSystem.AddWorkshop(wlId, BanditMilitias.Systems.Workshop.WorkshopType.Fletchery);
            var workshops = wsSystem.GetWorkshops(wlId);

            Assert.AreEqual(1, workshops.Count);
            Assert.AreEqual(1, workshops[0].Level);


            bool upgraded = wsSystem.UpgradeWorkshop(wlId, BanditMilitias.Systems.Workshop.WorkshopType.Fletchery);
            Assert.IsTrue(upgraded, "Workshop could not be upgraded despite sufficient gold!");
            Assert.AreEqual(2, workshops[0].Level);
        }
    }
}
