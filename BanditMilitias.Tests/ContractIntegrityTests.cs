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
                var attr = (HarmonyPatch)type.GetCustomAttribute(typeof(HarmonyPatch), true);
                if (attr == null) continue;

                // Harmony'nin iç mantığını simüle ederek hedef metodu bulmaya çalışalım
                try 
                {
                    var info = HarmonyMethodExtensions.GetCombinedAttributes(type);
                    if (info == null || info.declaringType == null) continue;

                    MethodBase original;
                    if (string.IsNullOrEmpty(info.methodName))
                    {
                         // Constructor patch olabilir
                         original = info.declaringType.GetConstructor(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                             null, info.argumentTypes ?? Array.Empty<Type>(), null);
                    }
                    else 
                    {
                        original = AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes);
                    }

                    if (original == null)
                    {
                        failedPatches.Add($"{type.Name} -> {info.declaringType.Name}.{info.methodName}");
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
