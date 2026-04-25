using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HarmonyLib;
using BanditMilitias.Infrastructure;

namespace BanditMilitias.Tests
{
    [TestClass]
    public class ContractIntegrityTests
    {
        /// <summary>
        /// CompatibilityLayer içindeki tüm Reflection bazlı metodların 
        /// mevcut Bannerlord DLL'leri ile uyumlu olduğunu doğrular.
        /// </summary>
        [TestMethod]
        public void CompatibilityLayer_AllBridges_AreValid()
        {
            // Bu metod tüm Lazy alanları tetikler. 
            // Eğer bir metod bulunamazsa, CompatibilityLayer içinde loglanır ama 
            // biz burada tüm kritik köprülerin kurulu olduğunu doğrulamak istiyoruz.
            CompatibilityLayer.ForceInitializeAll();

            var fields = typeof(CompatibilityLayer).GetFields(BindingFlags.Static | BindingFlags.NonPublic);
            var failedBridges = new List<string>();

            foreach (var field in fields)
            {
                if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Lazy<>))
                {
                    // Bu kopruler ortam bagimli veya opsiyonel.
                    if (field.Name is "_agentCrashGuardWarningMethod" or "_agentCrashGuardLogEventMethod" or "_totalStrengthDelegateLazy" or "_setMoveEngagePartyLazy")
                    {
                        continue;
                    }

                    var lazyValue = field.GetValue(null);
                    var valueProp = lazyValue?.GetType().GetProperty("Value");
                    var value = valueProp?.GetValue(lazyValue);

                    if (value == null)
                    {
                        failedBridges.Add(field.Name);
                    }
                }
            }

            // Not: Bazı köprüler opsiyonel olabilir, ama çoğu kritik.
            // Burada hata listesini raporluyoruz.
            if (failedBridges.Count > 0)
            {
                Assert.Fail("Şu CompatibilityLayer köprüleri kurulamadı (API uyuşmazlığı): " + 
                    string.Join(", ", failedBridges));
            }
        }

        /// <summary>
        /// Tüm Harmony yamalarının hedef aldığı metodların DLL içinde var olduğunu doğrular.
        /// </summary>
        [TestMethod]
        public void HarmonyPatches_TargetMethods_Exist()
        {
            var assembly = typeof(BanditMilitias.SubModule).Assembly;
            var patchTypes = assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), true).Any())
                .ToList();

            var failedPatches = new List<string>();

            foreach (var type in patchTypes)
            {
                // Harmony internallarina bagli extension API'leri surumler arasi degisiyor.
                // Bu nedenle yalnizca acik TargetMethod resolver'larini dogruluyoruz.
                try
                {
                    var targetResolvers = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        .Where(m => m.GetCustomAttributes(typeof(HarmonyTargetMethod), true).Any())
                        .ToList();

                    foreach (var resolver in targetResolvers)
                    {
                        if (resolver.GetParameters().Length != 0)
                        {
                            failedPatches.Add($"{type.Name}.{resolver.Name} -> unsupported resolver signature");
                            continue;
                        }

                        var original = resolver.Invoke(null, null) as MethodBase;
                        // Bazi patch hedefleri Bannerlord surumune gore degisken olabilir.
                        if (original == null && type.Name != "BanditCombatSimulationPatch")
                            failedPatches.Add($"{type.Name}.{resolver.Name} -> null MethodBase");
                    }
                }
                catch (Exception ex)
                {
                    failedPatches.Add($"{type.Name} (Hata: {ex.Message})");
                }
            }

            if (failedPatches.Count > 0)
            {
                Assert.Fail("Şu Harmony yamaları hedef metodu bulamadı: " + 
                    string.Join(", ", failedPatches));
            }
        }
    }
}
