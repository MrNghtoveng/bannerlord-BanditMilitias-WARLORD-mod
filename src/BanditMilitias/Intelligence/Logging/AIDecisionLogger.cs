using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BanditMilitias.Intelligence.Logging
{

    public static class AIDecisionLogger
    {
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord",
            "Logs");

        private static readonly string LogPath = Path.Combine(LogDirectory, "BanditMilitias_AIDecisions.log");
        private static readonly string ArchivePath = Path.Combine(LogDirectory, "BanditMilitias_AIDecisions_prev.log");

        private static readonly object _lock = new object();
        private const long MaxFileSizeBytes = 5 * 1024 * 1024;

        private static bool IsEnabled =>
            Settings.Instance?.EnableAIDecisionLogging == true;

        private static void Write(string message)
        {
            if (!IsEnabled) return;

            try
            {
                if (!Directory.Exists(LogDirectory))
                    _ = Directory.CreateDirectory(LogDirectory);

                lock (_lock)
                {

                    if (File.Exists(LogPath))
                    {
                        var info = new FileInfo(LogPath);
                        if (info.Length > MaxFileSizeBytes)
                        {
                            if (File.Exists(ArchivePath))
                                File.Delete(ArchivePath);
                            File.Move(LogPath, ArchivePath);
                        }
                    }

                    string timestamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n";
                    File.AppendAllText(LogPath, timestamped);
                }
            }
            catch
            {

            }
        }

        public static void LogDecision(
            string warlordId,
            string personality,
            float gold,
            string chosenAction,
            float chosenScore,
            Dictionary<string, float> allScores,
            bool wasExploration,
            float explorationRate)
        {
            if (!IsEnabled) return;

            var sb = new StringBuilder();
            _ = sb.Append($"[DECISION] Warlord={warlordId} | Action={chosenAction} | Score={chosenScore:F3}");
            if (wasExploration) _ = sb.Append(" | EXPLORATION");
            _ = sb.AppendLine();
            _ = sb.Append($"  Personality={personality} | Gold={gold:F0} | ExplorationRate={explorationRate:P1}");
            _ = sb.AppendLine();

            var sorted = allScores.OrderByDescending(kvp => kvp.Value).ToList();
            _ = sb.Append("  Alternatives: ");
            _ = sb.Append(string.Join(", ", sorted.Select(kvp => $"{kvp.Key}={kvp.Value:F3}")));

            Write(sb.ToString());
        }

        public static void LogUtilityBreakdown(
            string warlordId,
            string action,
            float baseScore,
            float personalityMul,
            float successMul,
            float threatMul,
            float resourceMul,
            float pidMul,
            float integrationMul,
            float finalScore)
        {
            if (!IsEnabled) return;

            Write($"[UTILITY] {warlordId} -> {action}: " +
                  $"Base={baseScore:F2} x Personality={personalityMul:F2} x Success={successMul:F2} " +
                  $"x Threat={threatMul:F2} x Resource={resourceMul:F2} x PID={pidMul:F2} " +
                  $"x Integration={integrationMul:F2} = {finalScore:F3}");
        }

        public static void LogQLearningUpdate(
            string action,
            bool success,
            float reward,
            float oldQValue,
            float newQValue,
            float explorationRate)
        {
            if (!IsEnabled) return;

            string status = success ? "SUCCESS" : "FAILED";
            Write($"[Q-LEARN] Action={action} | {status} | Reward={reward:F2} | " +
                  $"Q-Value: {oldQValue:F3} -> {newQValue:F3} | ExplorationRate={explorationRate:P1}");
        }

        public static void LogWarlordResponse(
            string warlordId,
            string personality,
            float overallThreat,
            float confidence,
            string chosenCommand,
            bool strategyOverridden,
            string? overrideReason = null)
        {
            if (!IsEnabled) return;

            var sb = new StringBuilder();
            _ = sb.Append($"[RESPONSE] Warlord={warlordId} | Threat={overallThreat:F2} | " +
                      $"Confidence={confidence:F2} | Command={chosenCommand}");
            if (strategyOverridden)
                _ = sb.Append($" | OVERRIDE: {overrideReason ?? "Conservative fallback"}");

            Write(sb.ToString());
        }

        public static void LogCommandOutcome(
            string warlordName,
            string commandType,
            string status,
            float successRate,
            float oldConfidence,
            float newConfidence)
        {
            if (!IsEnabled) return;

            Write($"[OUTCOME] Warlord={warlordName} | Command={commandType} | Status={status} | " +
                  $"SuccessRate={successRate:P0} | Confidence: {oldConfidence:F2} -> {newConfidence:F2}");
        }

        public static void LogTacticalDecision(
            string partyId,
            string decisionType,
            string source,
            string? targetId = null,
            float? score = null)
        {
            if (!IsEnabled) return;

            var sb = new StringBuilder();
            _ = sb.Append($"[TACTICAL] Party={partyId} | Decision={decisionType} | Source={source}");
            if (targetId != null) _ = sb.Append($" | Target={targetId}");
            if (score.HasValue) _ = sb.Append($" | Score={score.Value:F1}");

            Write(sb.ToString());
        }

        public static void LogSessionStart()
        {
            if (!IsEnabled) return;

            Write("========== NEW SESSION ==========");
        }

        public static void LogDailySummary(
            int totalDecisions,
            int totalCommands,
            float overallSuccessRate,
            float explorationRate,
            int warlordCount)
        {
            if (!IsEnabled) return;

            Write($"[DAILY] Decisions={totalDecisions} | Commands={totalCommands} | " +
                  $"SuccessRate={overallSuccessRate:P0} | Exploration={explorationRate:P1} | " +
                  $"Warlords={warlordCount}");
        }

        public static string GetLogPath() => LogPath;
    }
}