using BanditMilitias.Debug;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaleWorlds.Library;

namespace BanditMilitias.Infrastructure
{

    public static class ExceptionMonitor
    {
        private sealed class Record
        {
            public int Count;
            public DateTime FirstSeen = DateTime.Now;
            public DateTime LastSeen = DateTime.Now;
            public string LastType = "Exception";
            public string LastMessage = "n/a";
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Record> _records =
            new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> _lastNotifyByContext =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public static void Capture(
            string context,
            Exception ex,
            bool userVisible = false,
            int notifyCooldownMinutes = 30)
        {
            if (string.IsNullOrWhiteSpace(context))
            {
                context = "Unknown";
            }

            string type = ex?.GetType().Name ?? "Exception";
            string message = Compact(ex?.Message);
            int count;

            lock (_lock)
            {
                if (!_records.TryGetValue(context, out Record? record))
                {
                    record = new Record();
                    _records[context] = record;
                }

                record.Count++;
                record.LastSeen = DateTime.Now;
                record.LastType = type;
                record.LastMessage = message;
                count = record.Count;
            }

            if (Settings.Instance?.EnableFileLogging == true)
            {
                try
                {
                    FileLogger.Log($"[SOFT-ERROR] {context} #{count} {type}: {message}");
                }
                catch
                {

                }
            }

            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Warning("ExceptionMonitor", $"{context} #{count} {type}: {message}");
            }

            if (!userVisible)
            {
                return;
            }

            bool shouldNotify;
            lock (_lock)
            {
                shouldNotify = !_lastNotifyByContext.TryGetValue(context, out DateTime lastNotify) ||
                               (DateTime.Now - lastNotify).TotalMinutes >= Math.Max(1, notifyCooldownMinutes);
                if (shouldNotify)
                {
                    _lastNotifyByContext[context] = DateTime.Now;
                }
            }

            if (shouldNotify)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[BanditMilitias] Recovered error in {context}. Cmd: militia.suppressed_exceptions",
                    Colors.Yellow));
            }
        }

        public static string GetReport(int maxEntries = 25)
        {
            lock (_lock)
            {
                if (_records.Count == 0)
                {
                    return "No suppressed/recovered exceptions recorded.";
                }

                StringBuilder sb = new StringBuilder();
                _ = sb.AppendLine("=== SUPPRESSED EXCEPTIONS (BanditMilitias) ===");

                foreach (var kv in _records
                    .OrderByDescending(kv => kv.Value.LastSeen)
                    .Take(Math.Max(1, maxEntries)))
                {
                    string context = kv.Key;
                    Record r = kv.Value;
                    _ = sb.AppendLine(
                        $"- {context} | Count={r.Count} | LastAt={r.LastSeen:yyyy-MM-dd HH:mm:ss} | {r.LastType}: {r.LastMessage}");
                }

                _ = sb.AppendLine("==============================================");
                return sb.ToString();
            }
        }

        private static string Compact(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "n/a";
            }

            string compact = (text ?? string.Empty).Replace(Environment.NewLine, " ").Trim();
            return compact.Length <= 220 ? compact : compact.Substring(0, 220) + "...";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("suppressed_exceptions", "militia")]
        public static string DebugSuppressedExceptions(List<string> args)
        {
            int maxEntries = 25;
            if (args != null && args.Count > 0 && int.TryParse(args[0], out int parsed))
            {
                maxEntries = Math.Max(1, parsed);
            }

            return GetReport(maxEntries);
        }
    }
}