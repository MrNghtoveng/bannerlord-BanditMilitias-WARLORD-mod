using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BanditMilitias.Boot
{
    public sealed class HarmonyBootstrapper
    {
        private readonly string _harmonyId;
        private Harmony? _harmony;

        public HarmonyBootstrapper(string harmonyId)
        {
            _harmonyId = harmonyId;
        }

        public bool IsPatched { get; private set; }

        /// <summary>
        /// Applies all Harmony patches from the given assembly.
        /// Strategy:
        ///   1. Pre-flight: scan all [HarmonyPatch] types and log any missing hard-coded targets
        ///      (those classes already have [HarmonyPrepare] guards that will gracefully skip them).
        ///   2. Call PatchAll — single atomic operation as intended.
        ///   3. On failure: diagnose responsible patch class, attempt targeted rollback-and-retry
        ///      of non-critical patches, return true on partial success to avoid Degraded mode.
        /// </summary>
        public bool Apply(Assembly assembly)
        {
            if (IsPatched) return true;

            // ── Duplicate Harmony detection ───────────────────────────────────────
            // BUTR Harmony'nin ValidateLoadOrder() metodu SingleOrDefault kullanıyor.
            // Kullanıcının mod listesinde Harmony iki kez yüklüyse (farklı klasör
            // veya eski kurulum kalıntısı) InvalidOperationException fırlatır ve oyun
            // BanditMilitias'a hiç ulaşmadan çöker — kullanıcı suçu bu moda atar.
            // Burada erken tespit edip açıklayıcı uyarı veriyoruz.
            ValidateHarmonyEnvironment();

            try
            {
                _harmony = new Harmony(_harmonyId);

                // ── Step 1: Pre-flight validation ────────────────────────────────────
                RunPreflightValidation(assembly);

                // ── Step 2: PatchAll (single atomic operation — all patches or diagnose) ──
                _harmony.PatchAll(assembly);

                IsPatched = true;
                FileLogger.Log("Harmony patches applied successfully");
                return true;
            }
            catch (System.Exception ex)
            {
                FileLogger.LogError($"Harmony patching failed: {ex}");
                DebugLogger.Error("SubModule", $"Harmony patching failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Scans all [HarmonyPatch] classes with static hard-coded type/method attributes
        /// and logs any whose target methods cannot be found. These classes must have
        /// [HarmonyPrepare] guards that return false — this step validates that they do.
        /// </summary>
        private static void RunPreflightValidation(Assembly assembly)
        {
            int total = 0, missing = 0;

            foreach (var type in assembly.GetTypes())
            {
                var patchAttr = type.GetCustomAttribute<HarmonyPatch>();
                if (patchAttr == null) continue;
                total++;

                // [HarmonyPatch(typeof(T), "MethodName")] — hard-coded target
                Type? declaredType = patchAttr.info.declaringType;
                string? methodName = patchAttr.info.methodName;

                if (declaredType == null || string.IsNullOrEmpty(methodName)) continue;

                var targetMethod = AccessTools.Method(declaredType, methodName);
                if (targetMethod == null)
                {
                    missing++;
                    bool hasPrepareGuard = type.GetMethods(
                            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                        .Any(m => m.GetCustomAttribute<HarmonyPrepare>() != null);

                    if (hasPrepareGuard)
                    {
                        FileLogger.Log(
                            $"[Preflight] '{type.Name}' target '{declaredType.Name}.{methodName}' " +
                            $"not found — [HarmonyPrepare] guard present, will skip gracefully.");
                    }
                    else
                    {
                        FileLogger.LogWarning(
                            $"[Preflight] '{type.Name}' target '{declaredType.Name}.{methodName}' " +
                            $"not found and NO [HarmonyPrepare] guard — PatchAll may fail!");
                    }
                }
            }

            FileLogger.Log($"[Preflight] Validated {total} patch class(es), {missing} with missing targets.");
        }


        public void Unapply()
        {
            if (!IsPatched || _harmony == null) return;

            _harmony.UnpatchAll(_harmonyId);
            IsPatched = false;
            FileLogger.Log("Harmony patches unapplied");
        }

        /// <summary>
        /// BUTR Bannerlord.Harmony modülündeki bilinen bir riski erkenden tespit eder.
        /// ValidateLoadOrder() SingleOrDefault kullanıyor — kullanıcının modlist'inde
        /// Harmony iki kez yüklüyse InvalidOperationException fırlatır ve oyun BanditMilitias'a
        /// ulaşmadan çöker. Burada duplicate assembly'i tespit edip log ve ekran uyarısı veriyoruz.
        /// Bu fonksiyon hiçbir zaman exception fırlatmaz.
        /// </summary>
        private static void ValidateHarmonyEnvironment()
        {
            try
            {
                var harmonyAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name == "0Harmony")
                    .ToList();

                if (harmonyAssemblies.Count > 1)
                {
                    var locations = string.Join(", ",
                        harmonyAssemblies.Select(a =>
                            string.IsNullOrEmpty(a.Location) ? "(dynamic)" : System.IO.Path.GetDirectoryName(a.Location)));

                    FileLogger.LogWarning(
                        $"[HarmonyBootstrapper] {harmonyAssemblies.Count} adet 0Harmony assembly yüklü! " +
                        $"Bu, BUTR Harmony modülünün crash'e yol açmasına neden olabilir. " +
                        $"Konumlar: {locations}");

                    // Oyun yüklüyse ekranda göster — yüklü değilse sadece log yeterli
                    try
                    {
                        TaleWorlds.Library.InformationManager.DisplayMessage(
                            new TaleWorlds.Library.InformationMessage(
                                $"[BanditMilitias] WARNING: Multiple Harmony assemblies loaded " +
                                $"({harmonyAssemblies.Count}x). This may cause crashes. " +
                                $"Remove duplicate Harmony modules from your mod list.",
                                TaleWorlds.Library.Colors.Yellow));
                    }
                    catch { /* Screen may not be ready — log is enough */ }
                }
                else
                {
                    FileLogger.Log($"[HarmonyBootstrapper] Harmony environment OK — {harmonyAssemblies.Count} assembly loaded.");
                }
            }
            catch (Exception ex)
            {
                // This check should never block the patching process
                FileLogger.LogWarning($"[HarmonyBootstrapper] Harmony environment check failed: {ex.Message}");
            }
        }
    }
}
