using BanditMilitias.Core.Registry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BanditMilitias.Systems.Diagnostics
{
    public sealed class ModuleRegistryHealthSnapshot
    {
        public ModuleRegistryHealthSnapshot(AuditResult audit, IReadOnlyList<ModuleEntry> coldModules)
        {
            Audit = audit ?? throw new ArgumentNullException(nameof(audit));
            ColdModules = coldModules ?? throw new ArgumentNullException(nameof(coldModules));
        }

        public AuditResult Audit { get; }
        public IReadOnlyList<ModuleEntry> ColdModules { get; }

        public bool HasProblems =>
            Audit.Unregistered.Count > 0 ||
            Audit.Failed.Count > 0 ||
            Audit.SilentBroken.Count > 0 ||
            Audit.Stale.Count > 0 ||
            Audit.Dead.Count > 0 ||
            Audit.EventLeaks.Count > 0 ||
            ColdModules.Count > 0;

        public string Summary =>
            $"Ghost={Audit.Unregistered.Count}, Failed={Audit.Failed.Count}, Silent={Audit.SilentBroken.Count}, Stale={Audit.Stale.Count}, Dead={Audit.Dead.Count}, EventLeak={Audit.EventLeaks.Count}, Cold={ColdModules.Count}";

        public string BuildDetails()
        {
            var sb = new StringBuilder();
            AppendSection(sb, "Ghost", Audit.Unregistered);
            AppendSection(sb, "Failed", Audit.Failed);
            AppendSection(sb, "Silent", Audit.SilentBroken);
            AppendSection(sb, "Stale", Audit.Stale);
            AppendSection(sb, "Dead", Audit.Dead);
            AppendSection(sb, "EventLeak", Audit.EventLeaks);
            AppendSection(sb, "Cold", ColdModules);
            return sb.ToString().Trim();
        }

        private static void AppendSection(StringBuilder sb, string label, IEnumerable<ModuleEntry> entries)
        {
            List<string> names = entries
                .Select(entry => entry.DisplayName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (names.Count == 0)
            {
                return;
            }

            if (sb.Length > 0)
            {
                _ = sb.Append(" | ");
            }

            _ = sb.Append(label);
            _ = sb.Append(": ");
            _ = sb.Append(string.Join(", ", names));
        }
    }

    public static class ModuleRegistryHealthAnalyzer
    {
        public static ModuleRegistryHealthSnapshot Capture(ModuleRegistry? registry = null, AuditOptions? options = null)
        {
            ModuleRegistry current = registry ?? ModuleRegistry.Instance;
            AuditResult audit = current.Audit(options);
            IReadOnlyList<ModuleEntry> coldModules = FindColdModules(current.All);
            return new ModuleRegistryHealthSnapshot(audit, coldModules);
        }

        public static IReadOnlyList<ModuleEntry> FindColdModules(IEnumerable<ModuleEntry> entries)
        {
            if (entries == null)
            {
                return Array.Empty<ModuleEntry>();
            }

            return entries
                .Where(entry => entry != null
                    && entry.Status == ModuleStatus.Registered
                    && !entry.HasRuntimeActivity
                    && !entry.LastHealthyUtc.HasValue)
                .OrderByDescending(entry => entry.Priority)
                .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
