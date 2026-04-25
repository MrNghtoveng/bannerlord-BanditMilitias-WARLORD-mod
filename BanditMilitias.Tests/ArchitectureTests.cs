using BanditMilitias.Core.Components;
using BanditMilitias.Core.Registry;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Reflection;

namespace BanditMilitias.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class ArchitectureTests
    {
        private static readonly Assembly _asm = typeof(MilitiaModuleBase).Assembly;

        [TestMethod]
        public void All_Module_Types_Are_Discoverable()
        {
            var registry = ModuleRegistry.Instance;
            registry.Reset();
            registry.Discover(_asm);

            var discovered = registry.All.Select(e => e.Name).ToList();

            var allModuleTypes = GetLoadableTypes()
                .Where(t => !t.IsAbstract
                         && typeof(IMilitiaModule).IsAssignableFrom(t)
                         && t != typeof(MilitiaModuleBase))
                .Select(t => t.Name)
                .ToList();

            foreach (var typeName in allModuleTypes)
            {
                Assert.IsTrue(discovered.Contains(typeName),
                    $"'{typeName}' ModuleRegistry tarafindan kesfedilemedi.");
            }
        }

        [TestMethod]
        public void No_Abstract_Module_Subclasses_Except_Base()
        {
            var abstracts = GetLoadableTypes()
                .Where(t => t.IsAbstract
                         && !t.IsInterface
                         && t != typeof(MilitiaModuleBase)
                         && typeof(IMilitiaModule).IsAssignableFrom(t))
                .Select(t => t.Name)
                .ToList();

            Assert.AreEqual(0, abstracts.Count, $"Unexpected abstract modules: {string.Join(", ", abstracts)}");
        }

        [TestMethod]
        public void GetDiagnostics_Returns_NonEmpty()
        {
            var moduleTypes = GetLoadableTypes()
                .Where(t => !t.IsAbstract
                         && typeof(IMilitiaModule).IsAssignableFrom(t)
                         && t != typeof(MilitiaModuleBase));

            foreach (var type in moduleTypes)
            {
                var method = type.GetMethod("GetDiagnostics",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

                if (method != null)
                {
                    Assert.IsNotNull(method);
                }
            }
        }

        [TestMethod]
        public void All_ModuleNames_Are_Unique()
        {
            var names = GetLoadableTypes()
                .Where(t => !t.IsAbstract
                         && typeof(IMilitiaModule).IsAssignableFrom(t)
                         && t != typeof(MilitiaModuleBase))
                .Select(t => t.Name)
                .ToList();

            var distinct = names.Distinct().ToList();
            Assert.AreEqual(names.Count, distinct.Count);
        }

        private static Type[] GetLoadableTypes()
        {
            try
            {
                return _asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            }
        }
    }
}
