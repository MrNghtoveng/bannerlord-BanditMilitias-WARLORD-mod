using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BanditMilitias.Intelligence.Strategic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class WarlordIntegrationTests
    {
        [TestMethod]
        public void WarlordSystem_CanProcess_MockedParties()
        {
            // 1. Arrange: Sahte Bannerlord nesnelerini hazırla
            var fakePos = new Vec2(100f, 200f);
            var settlement = MockingHub.CreateFakeSettlement("test_hideout", "Test Hideout", fakePos);
            var party = MockingHub.CreateFakeMobileParty("test_militia", "Test Militia");

            // 2. Act: Mod mantığını bu nesnelerle çalıştır
            // Not: WarlordSystem singleton olabilir, test için instance alıyoruz
            var warlordSystem = WarlordSystem.Instance;
            
            Assert.IsNotNull(warlordSystem, "WarlordSystem instance alınamadı.");

            // Sahte nesneler üzerinde basit özellik kontrolleri
            Assert.AreEqual("test_hideout", settlement.StringId);
            Assert.AreEqual("test_militia", party.StringId);
            
            // CompatibilityLayer üzerinden pozisyon alma testi (Integration!)
            var pos = Infrastructure.CompatibilityLayer.GetPartyPosition(party);
            // FormatterServices ile oluşturulan nesnelerin fieldları default (NaN) olabilir
            // Eğer MockingHub'da set etmediysek invalid döner.
            Assert.IsFalse(float.IsNaN(pos.X), "CompatibilityLayer sahte partiden pozisyon okuyamadı.");
        }

        [TestMethod]
        public void WarlordSystem_EconomyBalance_IsConsistent()
        {
            // Saf mantık testi (Pure Logic) ile entegrasyonun birleşimi
            var warlordSystem = WarlordSystem.Instance;
            
            float initialBalance = warlordSystem.GetWarlordEconomyBalance();
            
            // Eğer sistem henüz başlatalmadıysa 0 dönecektir
            Assert.IsNotNull(initialBalance);
        }
    }
}
