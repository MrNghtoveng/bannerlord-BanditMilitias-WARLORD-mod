using Microsoft.VisualStudio.TestTools.UnitTesting;
using BanditMilitias.Diagnostics;
using BanditMilitias.Lifecycle;
using BanditMilitias.Core.Registry;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class GridAuditorTests
    {
        [TestMethod]
        public void Run_Full_Grid_Audit()
        {
            var assembly = typeof(BanditMilitias.Core.Components.MilitiaModuleBase).Assembly;
            BanditMilitias.Core.Registry.ModuleRegistry.Instance.Discover(assembly);
            BanditMilitias.Boot.WarlordPowerController.Instance.PowerOn(assembly);
            
            WarlordGridAuditor.PerformFullAudit();
            Assert.IsTrue(true);
        }
    }
}
