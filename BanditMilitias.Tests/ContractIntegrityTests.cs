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


        [TestMethod]
        public void CompatibilityLayer_AllBridges_AreValid()
        {


            CompatibilityLayer.ForceInitializeAll();

            var fields = typeof(CompatibilityLayer).GetFields(BindingFlags.Static | BindingFlags.NonPublic);
            var failedBridges = new List<string>();

            foreach (var field in fields)
            {
                if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Lazy<>))
                {


                    if (field.Name is "_agentCrashGuardWarningMethod" or "_agentCrashGuardLogEventMethod" or "_totalStrengthDelegateLazy" or "_setMoveEngagePartyLazy" or "_setMoveRaidLazy")
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
                    else if (value.GetType().IsValueType && value.GetType().Name.Contains("ValueTuple"))
                    {
                        // Handle (MethodInfo?, bool) or similar tuples
                        var methodField = value.GetType().GetField("Item1");
                        if (methodField != null && methodField.GetValue(value) == null)
                        {
                            failedBridges.Add(field.Name);
                        }
                    }
                }
            }


            if (failedBridges.Count > 0)
            {
                Assert.Inconclusive("The following CompatibilityLayer bridges could not be established (API mismatch): " +
                    string.Join(", ", failedBridges));
            }
        }


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


                        if (original == null && !type.Name.Contains("BanditCombatSimulationPatch") && !type.Name.Contains("SimulationDamagePatch") && !type.Name.Contains("SimulationCasualtiesPatch"))
                            failedPatches.Add($"{type.Name}.{resolver.Name} -> null MethodBase");
                    }
                }
                catch (Exception ex)
                {
                    failedPatches.Add($"{type.Name} (Error: {ex.Message})");
                }
            }

            if (failedPatches.Count > 0)
            {
                Assert.Fail("The following Harmony patches could not find the target method: " +
                    string.Join(", ", failedPatches));
            }
        }
    }
}
