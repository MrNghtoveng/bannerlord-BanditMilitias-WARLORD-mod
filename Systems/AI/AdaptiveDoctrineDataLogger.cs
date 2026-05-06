using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.Strategic;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BanditMilitias.Systems.AI
{
    public static class AdaptiveDoctrineDataLogger
    {
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord",
            "Warlord_Logs",
            "BanditMilitias",
            "AI");

        private static readonly string ProfileUpdatesPath = Path.Combine(LogDirectory, "adaptive_doctrine_profile_updates.csv");
        private static readonly string BattleUpdatesPath = Path.Combine(LogDirectory, "adaptive_doctrine_battle_updates.csv");

        public static string SnapshotPath => Path.Combine(LogDirectory, "adaptive_doctrine_snapshot.csv");

        private static bool _initialized;
        private static int _profileLogs;
        private static int _battleLogs;
        private static readonly object _sync = new();

        public static void LogProfileUpdate(
            string warlordId,
            bool isGlobalProfile,
            PlayerCombatDoctrine observed,
            CounterDoctrine oldDoctrine,
            CounterDoctrine candidateDoctrine,
            CounterDoctrine activeDoctrine,
            bool switched,
            float confidence,
            float aggressionBias,
            float threatLevel,
            PlayStyle style,
            PersonalityType personality,
            int sampleIndex)
        {
            EnsureInitialized();
            AppendLine(ProfileUpdatesPath,
                SafeTelemetry.CsvRow(
                    Now(),
                    warlordId,
                    isGlobalProfile,
                    observed,
                    oldDoctrine,
                    candidateDoctrine,
                    activeDoctrine,
                    switched,
                    confidence.ToString("F3"),
                    aggressionBias.ToString("F3"),
                    threatLevel.ToString("F3"),
                    style,
                    personality,
                    sampleIndex));
            _profileLogs++;
        }

        public static void LogBattleUpdate(
            string warlordId,
            string partyId,
            bool won,
            float confidenceBefore,
            float confidenceAfter,
            CounterDoctrine doctrine,
            int successfulEngagements,
            int failedEngagements,
            int sampleIndex)
        {
            EnsureInitialized();
            AppendLine(BattleUpdatesPath,
                SafeTelemetry.CsvRow(
                    Now(),
                    warlordId,
                    partyId,
                    won,
                    confidenceBefore.ToString("F3"),
                    confidenceAfter.ToString("F3"),
                    doctrine,
                    successfulEngagements,
                    failedEngagements,
                    sampleIndex));
            _battleLogs++;
        }

        public static void ExportProfilesSnapshot(List<AdaptiveDoctrineProfile> snapshot)
        {
            EnsureInitialized();

            var rows = new List<string>
            {
                "Timestamp,WarlordId,ObservedPlayerDoctrine,ActiveCounterDoctrine,Confidence,AggressionBias,SuccessfulEngagements,FailedEngagements"
            };

            rows.AddRange(snapshot.Select(profile =>
                SafeTelemetry.CsvRow(
                    Now(),
                    profile.WarlordId,
                    profile.ObservedPlayerDoctrine,
                    profile.ActiveCounterDoctrine,
                    profile.Confidence.ToString("F3"),
                    profile.AggressionBias.ToString("F3"),
                    profile.SuccessfulEngagements,
                    profile.FailedEngagements)));

            lock (_sync)
            {
                File.WriteAllLines(SnapshotPath, rows);
            }
        }

        public static string GetDiagnostics()
            => $"AdaptiveDoctrineDataLogger: ProfileLogs={_profileLogs} BattleLogs={_battleLogs} Snapshot={SnapshotPath}";

        private static void EnsureInitialized()
        {
            if (_initialized) return;

            lock (_sync)
            {
                if (_initialized) return;

                _ = Directory.CreateDirectory(LogDirectory);

                if (!File.Exists(ProfileUpdatesPath))
                {
                    File.WriteAllText(ProfileUpdatesPath,
                        "Timestamp,WarlordId,IsGlobalProfile,ObservedDoctrine,OldDoctrine,CandidateDoctrine,ActiveDoctrine,Switched,Confidence,AggressionBias,ThreatLevel,PlayStyle,Personality,SampleIndex" + Environment.NewLine);
                }

                if (!File.Exists(BattleUpdatesPath))
                {
                    File.WriteAllText(BattleUpdatesPath,
                        "Timestamp,WarlordId,PartyId,Won,ConfidenceBefore,ConfidenceAfter,Doctrine,SuccessfulEngagements,FailedEngagements,SampleIndex" + Environment.NewLine);
                }

                _initialized = true;
            }
        }

        private static void AppendLine(string path, string line)
        {
            lock (_sync)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }

        private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }
}
