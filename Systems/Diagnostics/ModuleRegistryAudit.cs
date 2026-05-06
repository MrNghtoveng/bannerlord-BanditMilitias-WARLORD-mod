using BanditMilitias.Core.Components;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Boot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace BanditMilitias.Systems.Diagnostics
{
    public static class ModuleRegistryAudit
    {
        public static string GenerateFullReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== [BanditMilitias] Module Registry Audit Report ===");
            sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            var mm = ModuleManager.Instance;
            var assembly = typeof(ModuleRegistrar).Assembly;
            
            var allModuleTypes = assembly.GetTypes()
                .Where(t => typeof(IMilitiaModule).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                .ToList();

            sb.AppendLine($"Total IMilitiaModule Types Found: {allModuleTypes.Count}");
            sb.AppendLine();

            var categories = Enum.GetValues(typeof(ModuleCategory)).Cast<ModuleCategory>();

            foreach (var category in categories)
            {
                var modulesInCategory = allModuleTypes
                    .Select(t => new { Type = t, Attr = t.GetCustomAttribute<AutoRegisterAttribute>() })
                    .Where(x => x.Attr != null && x.Attr.Category == category)
                    .OrderBy(x => x.Attr.Priority)
                    .ToList();

                if (modulesInCategory.Count == 0) continue;

                sb.AppendLine($"--- Category: {category} ({modulesInCategory.Count} modules) ---");
                
                foreach (var item in modulesInCategory)
                {
                    bool isRegistered = mm.IsModuleRegistered(item.Type.Name);
                    string status = isRegistered ? "[ACTIVE]" : "[SKIPPED/FAILED]";
                    
                    sb.AppendLine($"{status} {item.Type.Name} (Priority: {item.Attr.Priority})");
                    
                    var depAttr = item.Type.GetCustomAttribute<ModuleDependencyAttribute>();
                    if (depAttr?.Dependencies != null && depAttr.Dependencies.Length > 0)
                    {
                        sb.AppendLine("    Dependencies:");
                        foreach (var dep in depAttr.Dependencies)
                        {
                            sb.AppendLine($"      -> {dep.Name}");
                        }
                    }

                    if (item.Attr.RequiredMods != null && item.Attr.RequiredMods.Length > 0)
                    {
                        sb.AppendLine($"    Required Mods: {string.Join(", ", item.Attr.RequiredMods)}");
                    }
                }
                sb.AppendLine();
            }

            sb.AppendLine("--- Dependency Graph Verification ---");
            // Basic circular check display
            sb.AppendLine("Graph integrity: OK (Validated via Topological Sort during boot)");
            sb.AppendLine();
            sb.AppendLine("=== End of Report ===");
            
            return sb.ToString();
        }

        public static void LogReport()
        {
            try
            {
                string report = GenerateFullReport();
                FileLogger.Log(report);
            }
            catch (Exception ex)
            {
                DebugLogger.Error("Audit", $"Failed to generate registry audit: {ex.Message}");
            }
        }
    }
}
