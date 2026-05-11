using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BanditMilitias.Core.Registry;
using BanditMilitias.Infrastructure;
using BanditMilitias.Lifecycle;

namespace BanditMilitias.Diagnostics
{
    /// <summary>
    /// Standalone Electrical Grid Auditor for BanditMilitias (WARLORD).
    /// EN: This tool probes the mod's internal state without modifying any code.
    /// TR: Modun koduna dokunmadan iç durumunu (şebekeyi) denetleyen bağımsız bir araçtır.
    /// </summary>
    public static class WarlordGridAuditor
    {
        public static void PerformFullAudit()
        {
            Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║   WARLORD POWER GRID AUDITOR v1.0 — STANDALONE PROBE     ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════╝");

            try
            {
                // 1. Check Main Transformer (Lifecycle)
                var lifecycle = ModLifecycleManager.Instance;
                Console.WriteLine($"[Grid] Transformer State : {lifecycle.CurrentState}");
                Console.WriteLine($"[Grid] Energized Status  : {(lifecycle.DeferredInitDone ? "FULL" : "DEFERRED")}");
                Console.WriteLine($"[Grid] AI System         : {(lifecycle.AiSystemEnabled ? "CONNECTED" : "OFF")}");

                // 2. Audit the Wiring (Module Registry)
                var registry = ModuleRegistry.Instance;
                var audit = registry.Audit();
                Console.WriteLine($"[Grid] Connected Modules : {audit.TotalRegistered}");
                Console.WriteLine($"[Grid] Healthy Modules   : {audit.Healthy.Count}");
                
                if (audit.HasProblems)
                {
                    Console.WriteLine($"[Grid] !!! LEAKAGE DETECTED !!!");
                    Console.WriteLine($"[Grid] Ghost (Unreg)     : {audit.Unregistered.Count}");
                    Console.WriteLine($"[Grid] Failed Modules    : {audit.Failed.Count}");
                    Console.WriteLine($"[Grid] Silent Broken     : {audit.SilentBroken.Count}");
                    Console.WriteLine($"[Grid] Stale/Dead        : {audit.Stale.Count + audit.Dead.Count}");
                    
                    if (audit.Unregistered.Count > 0)
                    {
                        Console.WriteLine("[Grid] GHOST MODULES (Discovered but not bolted):");
                        foreach (var ghost in audit.Unregistered)
                            Console.WriteLine($"  -> GHOST: {ghost.Name}");
                    }

                    foreach(var failed in audit.Failed)
                        Console.WriteLine($"  -> FAIL: {failed.Name} ({failed.FailReason})");
                }

                // 3. Audit Adapters (Compatibility Layer)
                AuditAdapters();

                // 4. Check Induction Hooks (Harmony)
                // Since Harmony is internal to the assembly, we check via reflection if possible
                var harmonyField = typeof(ModLifecycleManager).GetField("_harmonyBootstrapper", BindingFlags.NonPublic | BindingFlags.Instance);
                if (harmonyField != null)
                {
                    var bootstrapper = harmonyField.GetValue(lifecycle);
                    var patchedProp = bootstrapper?.GetType().GetProperty("IsPatched");
                    bool isPatched = (bool?)patchedProp?.GetValue(bootstrapper) ?? false;
                    Console.WriteLine($"[Grid] Harmony Induction : {(isPatched ? "STABLE" : "DISCONNECTED")}");
                }

                Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Grid] FATAL AUDIT ERROR: {ex.Message}");
            }
        }

        private static void AuditAdapters()
        {
            var fields = typeof(CompatibilityLayer).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            int total = 0, active = 0;

            foreach (var field in fields)
            {
                if (field.FieldType.Name.StartsWith("Lazy"))
                {
                    total++;
                    var isCreatedProp = field.FieldType.GetProperty("IsValueCreated");
                    if (isCreatedProp != null && (bool)isCreatedProp.GetValue(field.GetValue(null))!)
                    {
                        active++;
                    }
                }
            }
            Console.WriteLine($"[Grid] API Adapters      : {active}/{total} active (demand-based)");
        }
    }
}
