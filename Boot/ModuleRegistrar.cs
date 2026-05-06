using BanditMilitias.Core.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BanditMilitias.Boot
{
    public sealed class ModuleRegistrar
    {
        private sealed class ModuleNode
        {
            public Type Type { get; }
            public AutoRegisterAttribute Attr { get; }
            public List<Type> Dependencies { get; } = new();
            public bool IsRegistered { get; set; }

            public ModuleNode(Type type, AutoRegisterAttribute attr)
            {
                Type = type;
                Attr = attr;
                var depAttr = type.GetCustomAttribute<ModuleDependencyAttribute>();
                if (depAttr?.Dependencies != null)
                {
                    Dependencies.AddRange(depAttr.Dependencies);
                }
            }
        }

        public void RegisterAll()
        {
            var mm = ModuleManager.Instance;
            var assembly = typeof(ModuleRegistrar).Assembly;
            var criticalFailures = new List<string>();

            bool isTestingMode = false;
            try { isTestingMode = Settings.Instance?.TestingMode == true; }
            catch { }

            bool isDevMode = false;
            try { isDevMode = Settings.Instance?.DevMode == true; }
            catch { }

            FileLogger.Log("=== [ModuleRegistrar] Starting Modernized Discovery ===");

            // 1. Discovery & Basic Filtering
            var allNodes = assembly.GetTypes()
                .Where(t => typeof(IMilitiaModule).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                .Select(t => new { Type = t, Attr = t.GetCustomAttribute<AutoRegisterAttribute>() })
                .Where(x => x.Attr != null)
                .Select(x => new ModuleNode(x.Type, x.Attr))
                .ToList();

            var activeNodes = new List<ModuleNode>();
            foreach (var node in allNodes)
            {
                if (node.Attr.DevOnly && !isDevMode && !isTestingMode)
                {
                    FileLogger.Log($"[SubModule] Skipping {node.Type.Name}: DevOnly");
                    continue;
                }

                if (node.Attr.RequiredMods.Length > 0)
                {
                    bool allModsFound = true;
                    foreach (var modId in node.Attr.RequiredMods)
                    {
                        if (!CompatibilityLayer.IsModActive(modId))
                        {
                            FileLogger.Log($"[SubModule] Skipping {node.Type.Name}: Missing Mod {modId}");
                            allModsFound = false;
                            break;
                        }
                    }
                    if (!allModsFound) continue;
                }

                activeNodes.Add(node);
            }

            // 2. Topological Sort (Kahn's Algorithm)
            var sortedList = new List<ModuleNode>();
            var visited = new HashSet<Type>();
            var stack = new HashSet<Type>(); // For circular dependency detection

            void SortInternal(ModuleNode node)
            {
                if (visited.Contains(node.Type)) return;
                if (stack.Contains(node.Type))
                {
                    DebugLogger.Error("SubModule", $"Circular dependency detected at {node.Type.Name}");
                    return;
                }

                stack.Add(node.Type);

                foreach (var depType in node.Dependencies)
                {
                    var depNode = activeNodes.FirstOrDefault(n => depType.IsAssignableFrom(n.Type));
                    if (depNode != null)
                    {
                        SortInternal(depNode);
                    }
                }

                stack.Remove(node.Type);
                visited.Add(node.Type);
                sortedList.Add(node);
            }

            // Primary sort by priority before topological sort ensures stable order for independent modules
            foreach (var node in activeNodes.OrderByDescending(n => n.Attr.Priority))
            {
                SortInternal(node);
            }

            FileLogger.Log($"[SubModule] Discovery complete. Found {activeNodes.Count} active modules. Execution order determined.");

            // 3. Registration
            foreach (var node in sortedList)
            {
                RegisterSafe(node.Type, node.Attr.IsCritical, criticalFailures);
            }

            if (isTestingMode)
            {
                ModuleValidator.ValidateRegistrations();
                BanditMilitias.Systems.Diagnostics.ModuleRegistryAudit.LogReport();
            }

            if (criticalFailures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Critical modules failed to register: {string.Join(", ", criticalFailures.Distinct())}");
            }

            FileLogger.Log($"=== [ModuleRegistrar] Modernized Registration Finished. Total: {sortedList.Count} ===");
            DebugLogger.Info("SubModule", $"Registered {sortedList.Count} modules successfully (Dependency Aware)");
        }

        private void RegisterSafe(Type type, bool isCritical, List<string> criticalFailures)
        {
            var mm = ModuleManager.Instance;
            string label = type.Name;
            IMilitiaModule? module = null;

            try
            {
                var attr = type.GetCustomAttribute<AutoRegisterAttribute>();
                
                if (attr?.IsSingleton == true)
                {
                    var instanceProp = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (instanceProp != null && typeof(IMilitiaModule).IsAssignableFrom(instanceProp.PropertyType))
                    {
                        module = instanceProp.GetValue(null) as IMilitiaModule;
                    }
                }

                if (module == null)
                {
                    module = Activator.CreateInstance(type) as IMilitiaModule;
                }

                if (module == null)
                {
                    FileLogger.LogWarning($"[SubModule] Module resolution returned NULL for {label}");
                    if (isCritical) criticalFailures.Add(label);
                    return;
                }

                mm.RegisterModule(module);
                FileLogger.Log($"[SubModule] [{attr?.Category ?? ModuleCategory.System}] Registered {label}. Status: {(module.IsEnabled ? "Enabled" : "Disabled")}");
            }
            catch (Exception ex)
            {
                DebugLogger.Error("SubModule", $"Module registration failed for {label}: {ex}");
                FileLogger.LogWarning($"[SubModule] CRITICAL: Exception for {label}: {ex.Message}");
                if (isCritical) criticalFailures.Add(label);
            }
        }
    }
}
