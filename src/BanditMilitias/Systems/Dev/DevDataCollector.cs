using BanditMilitias.Components;
using BanditMilitias.Core.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.AI.Components;
using BanditMilitias.Intelligence.ML;
using BanditMilitias.Intelligence.Strategic;
using BanditMilitias.Systems.Grid;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Systems.Scheduling;
using BanditMilitias.Systems.Economy;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BanditMilitias.Systems.Dev
{
    // ─────────────────────────────────────────────────────────────────────────
    //  DevDataCollector
    //  Geliştirici test modülü — Settings.DevMode = false olduğunda hiçbir şey
    //  yapmaz, hiçbir event'e bağlanmaz, sıfır overhead.
    //
    //  Toplanan veri kategorileri:
    //    A) Spawn olayları   — her milisya doğması/ölmesi
    //    B) AI kararları     — her taktik karar + uyku süresi
    //    C) Savaş sonuçları  — kazanç/kayıp, düşman güç oranı
    //    D) Scheduler tanı   — saat başı kuyruk durumu
    //    E) Sistem anlık     — her N dakikada bir tüm modül tanısı
    //    F) Uyku modu        — _nextThinkTime doluluk analizi
    //    G) Spatial grid     — hücre doluluk dağılımı
    //    H) Warlord kariyer  — tier değişimleri, legitimacy
    //    I) Economy          — parti bazında altın/günlük gelir
    //    J) Player etkisi    — oyuncunun tehdit etkisi
    //
    //  Çıktı:
    //    Documents/Mount and Blade II Bannerlord/BanditMilitias_Dev/
    //      spawn_log.csv
    //      ai_decisions.csv
    //      battle_outcomes.csv
    //      scheduler_hourly.csv
    //      system_snapshot.csv
    //      sleep_analysis.csv
    //      warlord_career.csv
    //      economy_log.csv
    //      session_summary.txt
    // ─────────────────────────────────────────────────────────────────────────

    [AutoRegister]
    public class DevDataCollector : MilitiaModuleBase
    {
        private static DevDataCollector? _instance;
        public static DevDataCollector Instance =>
            _instance ??= ModuleManager.Instance.GetModule<DevDataCollector>()
                          ?? new DevDataCollector();

        public override string ModuleName => "DevDataCollector";

        // Üretim sürümünde tamamen deaktif — sıfır overhead
        public override bool IsEnabled => Settings.Instance?.DevMode == true;
        public override int Priority => 5;

        // ── Çıktı klasörü ─────────────────────────────────────────
        private static readonly string _baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord",
            "BanditMilitias_Dev");

        private string _sessionDir = _baseDir;

        // ── Dosya yolları (session başında belirlenir) ─────────────
        private string _spawnLog = "";
        private string _aiLog = "";
        private string _battleLog = "";
        private string _schedulerLog = "";
        private string _snapshotLog = "";
        private string _sleepLog = "";
        private string _warlordLog = "";
        private string _economyLog = "";
        private string _timingLog = "";
        private string _pathLog = "";
        private string _fullSimDir = "";
        private string _fullSimRunLog = "";
        private string _summaryFile = "";

        // ── Session istatistikleri ─────────────────────────────────
        private int _sessionSpawns;
        private int _sessionDeaths;
        private int _sessionBattles;
        private int _sessionWins;
        private int _sessionDecisions;
        private int _sessionPathfinds;
        private int _hourlyTicks;
        private float _sessionStartDay;
        private bool _isPathInitialized = false;
        private bool _fullSimEnabled = false;
        private int _fullSimIntervalHours = 6;
        private double _nextFullSimCaptureHour = double.NaN;
        private int _fullSimRunCount = 0;

        private readonly object _writeLock = new();
        private readonly Dictionary<string, BattleSnapshot> _battleSnapshots = new();

        private readonly struct BattleSnapshot
        {
            public BattleSnapshot(bool hadEnemy, int militiaTroops, int enemyTroops)
            {
                HadEnemy = hadEnemy;
                MilitiaTroops = militiaTroops;
                EnemyTroops = enemyTroops;
            }

            public bool HadEnemy { get; }
            public int MilitiaTroops { get; }
            public int EnemyTroops { get; }
        }

        // ── Başlatma ──────────────────────────────────────────────

        public override void Initialize()
        {
            if (!IsEnabled) return;

            _instance = this;
            _sessionStartDay = 0f; // SAFE-FIX: Defer to RegisterCampaignEvents

            // Zaman damgalı session klasörü
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            _sessionDir = Path.Combine(_baseDir, $"session_{stamp}");
            _ = Directory.CreateDirectory(_sessionDir);

            _spawnLog = Path.Combine(_sessionDir, "spawn_log.csv");
            _aiLog = Path.Combine(_sessionDir, "ai_decisions.csv");
            _battleLog = Path.Combine(_sessionDir, "battle_outcomes.csv");
            _schedulerLog = Path.Combine(_sessionDir, "scheduler_hourly.csv");
            _snapshotLog = Path.Combine(_sessionDir, "system_snapshot.csv");
            _sleepLog = Path.Combine(_sessionDir, "sleep_analysis.csv");
            _warlordLog = Path.Combine(_sessionDir, "warlord_career.csv");
            _economyLog = Path.Combine(_sessionDir, "economy_log.csv");
            _timingLog = Path.Combine(_sessionDir, "timing_log.csv");
            _pathLog = Path.Combine(_sessionDir, "pathfinding_stress.csv");
            _fullSimDir = Path.Combine(_sessionDir, "full_sim_test");
            _fullSimRunLog = Path.Combine(_fullSimDir, "full_sim_runs.csv");
            _summaryFile = Path.Combine(_sessionDir, "session_summary.txt");

            // Başlık satırlarını yaz
            WriteHeader(_spawnLog,
                "Timestamp,CampaignDay,Event,PartyId,PartyName,HideoutId,HideoutName," +
                "TroopCount,Gold,WarlordId,Role,Personality");

            WriteHeader(_aiLog,
                "Timestamp,CampaignDay,PartyId,PartyName,Decision,Source,Score," +
                "SleepHours,NextThinkHour,TargetId,MLConfidence,AIState");

            WriteHeader(_battleLog,
                "Timestamp,CampaignDay,MilitiaId,MilitiaName,EnemyId,EnemyName," +
                "MilitiaWon,MilitiaTroops,EnemyTroops,StrengthRatio,Reward,WarlordTier");

            WriteHeader(_schedulerLog,
                "Timestamp,CampaignHour,UrgentQueue,NormalQueue,ProcessedThisTick," +
                "TotalProcessed,ActiveMilitias,SleepingMilitias,AwakePct");

            WriteHeader(_snapshotLog,
                "Timestamp,CampaignDay,Module,Diagnostics");

            WriteHeader(_sleepLog,
                "Timestamp,CampaignHour,PartyId,PartyName,NextThinkHour," +
                "SleepRemainingHours,Role,CurrentState,HasOrder");

            WriteHeader(_warlordLog,
                "Timestamp,CampaignDay,WarlordId,WarlordName,Event," +
                "OldTier,NewTier,Legitimacy,BattlesWon,TotalBounty,MilitiaCount");

            WriteHeader(_economyLog,
                "Timestamp,CampaignDay,PartyId,PartyName,WarlordId,Gold," +
                "TroopCount,Role,CurrentState,ProsperityAvg");

            WriteHeader(_timingLog,
                "Timestamp,CampaignDay,Module,DurationMs,Context");

            WriteHeader(_pathLog,
                "Timestamp,CampaignDay,TotalRequests,ActiveMilitias,RequestPerMilitia");
            WriteHeader(_fullSimRunLog,
                "Timestamp,CampaignDay,Trigger,RunIndex,IntervalHours,ActiveMilitias,SessionBattles,SessionWins," +
                "SessionDir,BattleDataPath,QTablePath");

            UnsubscribeEvents(); // Temizlik garantisi
            SubscribeEvents();
            BattleDataLogger.EnsureArtifactsExist();

            _isPathInitialized = true;
            if (!(Settings.Instance?.TestingMode == true && Settings.Instance?.ShowTestMessages == true))
                return;
            InformationManager.DisplayMessage(new InformationMessage(
                $"[DevMode] Veri toplanıyor → {_sessionDir}", Colors.Cyan));
        }

        public override void RegisterCampaignEvents()
        {
            if (!IsEnabled) return;
            // SAFE-FIX: Now is safe here.
            _sessionStartDay = (float)CampaignTime.Now.ToDays;
        }

        // ── Event abonelikleri ────────────────────────────────────

        private void SubscribeEvents()
        {
            // Spawn/ölüm
            EventBus.Instance.Subscribe<MilitiaSpawnedEvent>(OnMilitiaSpawned);
            EventBus.Instance.Subscribe<MilitiaDisbandedEvent>(OnMilitiaDisbanded);
            EventBus.Instance.Subscribe<MilitiaKilledEvent>(OnMilitiaKilled);

            // Savaş
            CampaignEvents.MapEventStarted.AddNonSerializedListener(this, OnMapEventStarted);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);

            // Warlord kariyer
            EventBus.Instance.Subscribe<CareerTierChangedEvent>(OnTierChanged);
            EventBus.Instance.Subscribe<CareerFatihPromotionEvent>(OnFatihPromotion);
            EventBus.Instance.Subscribe<WarlordFallenEvent>(OnWarlordFallen);
        }

        private void UnsubscribeEvents()
        {
            EventBus.Instance.Unsubscribe<MilitiaSpawnedEvent>(OnMilitiaSpawned);
            EventBus.Instance.Unsubscribe<MilitiaDisbandedEvent>(OnMilitiaDisbanded);
            EventBus.Instance.Unsubscribe<MilitiaKilledEvent>(OnMilitiaKilled);
            EventBus.Instance.Unsubscribe<CareerTierChangedEvent>(OnTierChanged);
            EventBus.Instance.Unsubscribe<CareerFatihPromotionEvent>(OnFatihPromotion);
            EventBus.Instance.Unsubscribe<WarlordFallenEvent>(OnWarlordFallen);
            CampaignEvents.MapEventStarted.ClearListeners(this);
            CampaignEvents.MapEventEnded.ClearListeners(this);
            _battleSnapshots.Clear();
        }

        // ── Saatlik tick ──────────────────────────────────────────

        public override void OnHourlyTick()
        {
            if (!IsEnabled || !_isPathInitialized || Campaign.Current == null) return;
            _hourlyTicks++;

            CollectSchedulerData();
            CollectSleepAnalysis();
            CollectPathfindingSnapshot();

            // Her 6 saatte bir ekonomi + sistem anlık görüntüsü
            if (_hourlyTicks % 6 == 0)
            {
                CollectEconomyData();
                CollectSystemSnapshot();
            }

            ProcessFullSimAutomation();
        }

        // ── Günlük tick ───────────────────────────────────────────

        public override void OnDailyTick()
        {
            if (!IsEnabled || !_isPathInitialized) return;
            UpdateSummary();
        }

        // ── A) Spawn olayları ─────────────────────────────────────

        private void OnMilitiaSpawned(MilitiaSpawnedEvent evt)
        {
            if (!IsEnabled || !_isPathInitialized || evt?.Party == null) return;
            _sessionSpawns++;

            var comp = evt.Party.PartyComponent as MilitiaPartyComponent;
            var home = comp?.GetHomeSettlement();

            AppendRow(_spawnLog, new[]
            {
                Now(), Day(), "Spawned",
                Csv(evt.Party.StringId),
                Csv(evt.Party.Name?.ToString()),
                Csv(home?.StringId),
                Csv(home?.Name?.ToString()),
                (evt.Party.MemberRoster?.TotalManCount ?? 0).ToString(),
                evt.Party.PartyTradeGold.ToString(),
                Csv(comp?.WarlordId),
                comp?.Role.ToString() ?? "",
                GetPersonality(comp)
            });
        }

        private void OnMilitiaDisbanded(MilitiaDisbandedEvent evt)
        {
            if (!IsEnabled || !_isPathInitialized || evt?.Party == null) return;
            _sessionDeaths++;

            var comp = evt.Party.PartyComponent as MilitiaPartyComponent;
            AppendRow(_spawnLog, new[]
            {
                Now(), Day(), "Disbanded",
                Csv(evt.Party.StringId),
                Csv(evt.Party.Name?.ToString()),
                Csv(comp?.GetHomeSettlement()?.StringId),
                Csv(comp?.GetHomeSettlement()?.Name?.ToString()),
                (evt.Party.MemberRoster?.TotalManCount ?? 0).ToString(),
                evt.Party.PartyTradeGold.ToString(),
                Csv(comp?.WarlordId),
                comp?.Role.ToString() ?? "",
                GetPersonality(comp)
            });
        }

        private void OnMilitiaKilled(MilitiaKilledEvent evt)
        {
            if (!IsEnabled || !_isPathInitialized || evt?.Party == null) return;
            _sessionDeaths++;

            var comp = evt.Party.PartyComponent as MilitiaPartyComponent;
            AppendRow(_spawnLog, new[]
            {
                Now(), Day(), "Killed",
                Csv(evt.Party.StringId),
                Csv(evt.Party.Name?.ToString()),
                Csv(comp?.GetHomeSettlement()?.StringId),
                Csv(comp?.GetHomeSettlement()?.Name?.ToString()),
                "0",
                "0",
                Csv(comp?.WarlordId),
                comp?.Role.ToString() ?? "",
                GetPersonality(comp)
            });
        }

        // ── B) Savaş sonuçları ────────────────────────────────────

        private void OnMapEventStarted(TaleWorlds.CampaignSystem.MapEvents.MapEvent mapEvent, PartyBase attackerParty, PartyBase defenderParty)
        {
            if (!IsEnabled || !_isPathInitialized || mapEvent == null) return;

            foreach (var partyRef in mapEvent.InvolvedParties)
            {
                var militia = partyRef?.MobileParty;
                if (militia?.PartyComponent is not MilitiaPartyComponent) continue;

                var enemy = FindEnemy(mapEvent, militia);
                _battleSnapshots[militia.StringId] = new BattleSnapshot(
                    enemy != null,
                    militia.MemberRoster?.TotalManCount ?? 0,
                    enemy?.MemberRoster?.TotalManCount ?? 0);
            }
        }

        private void OnMapEventEnded(TaleWorlds.CampaignSystem.MapEvents.MapEvent mapEvent)
        {
            if (!IsEnabled || !_isPathInitialized || mapEvent == null) return;

            foreach (var partyRef in mapEvent.InvolvedParties)
            {
                var militia = partyRef?.MobileParty;
                if (militia?.PartyComponent is not MilitiaPartyComponent comp) continue;

                bool isAttacker = mapEvent.AttackerSide.Parties
                    .Any(p => p.Party.MobileParty == militia);
                bool won = isAttacker
                    ? mapEvent.WinningSide == BattleSideEnum.Attacker
                    : mapEvent.WinningSide == BattleSideEnum.Defender;

                var oppSide = isAttacker ? mapEvent.DefenderSide : mapEvent.AttackerSide;
                var enemy = oppSide.Parties
                    .Select(p => p.Party.MobileParty)
                    .FirstOrDefault(p => p != null && p != militia);

                if (!_battleSnapshots.TryGetValue(militia.StringId, out var snapshot))
                {
                    snapshot = new BattleSnapshot(
                        enemy != null,
                        militia.MemberRoster?.TotalManCount ?? 0,
                        enemy?.MemberRoster?.TotalManCount ?? 0);
                }

                int mTroops = snapshot.MilitiaTroops > 0
                    ? snapshot.MilitiaTroops
                    : militia.MemberRoster?.TotalManCount ?? 0;
                int eTroops = snapshot.EnemyTroops > 0
                    ? snapshot.EnemyTroops
                    : enemy?.MemberRoster?.TotalManCount ?? 0;
                float ratio = eTroops > 0 ? (float)eTroops / System.Math.Max(1, mTroops) : 0f;

                // Warlord tier
                int warlordTier = 0;
                try
                {
                    string? wlId = comp.WarlordId;
                    if (!string.IsNullOrEmpty(wlId))
                    {
                        var lev = WarlordLegitimacySystem.Instance?.GetLevel(wlId!);
                        warlordTier = lev.HasValue ? (int)lev.Value : 0;
                    }
                }
                catch { }
                float reward = AILearningSystem.CalculateTelemetryReward(
                    militia,
                    snapshot.HadEnemy || enemy != null,
                    won,
                    mTroops,
                    eTroops);

                _sessionBattles++;
                if (won) _sessionWins++;

                AppendRow(_battleLog, new[]
                {
                    Now(), Day(),
                    Csv(militia.StringId), Csv(militia.Name?.ToString()),
                    Csv(enemy?.StringId),  Csv(enemy?.Name?.ToString()),
                    won.ToString(),
                    mTroops.ToString(), eTroops.ToString(),
                    ratio.ToString("F2"),
                    reward.ToString("F1"),
                    warlordTier.ToString()
                });

                _battleSnapshots.Remove(militia.StringId);
            }
        }

        // ── C) Scheduler saatlik ──────────────────────────────────

        private void CollectSchedulerData()
        {
            try
            {
                var sched = ModuleManager.Instance.GetModule<AISchedulerSystem>();
                if (sched == null) return;

                var allMilitias = Campaign.Current?.MobileParties
                    .Where(p => p?.PartyComponent is MilitiaPartyComponent && p.IsActive)
                    .ToList();

                int total = allMilitias?.Count ?? 0;
                int sleeping = allMilitias?.Count(p =>
                {
                    var c = p.PartyComponent as MilitiaPartyComponent;
                    return c != null && CampaignTime.Now < c.NextThinkTime;
                }) ?? 0;

                float awakePct = total > 0 ? (float)(total - sleeping) / total * 100f : 0f;

                // Scheduler diagnostics string'ini parse et
                string diag = sched.GetDiagnostics();
                int urgent = ExtractInt(diag, "Urgent=", ' ');
                int normal = ExtractInt(diag, "Normal=", ' ');
                int procTick = ExtractInt(diag, "LastTick=", 'm') < 0
                    ? 0 : ExtractInt(diag, "Processed=", ' ');

                AppendRow(_schedulerLog, new[]
                {
                    Now(),
                    ((int)CampaignTime.Now.ToHours).ToString(),
                    urgent.ToString(), normal.ToString(),
                    procTick.ToString(),
                    ExtractInt(diag, "Processed=", ' ').ToString(),
                    total.ToString(), sleeping.ToString(),
                    awakePct.ToString("F1")
                });
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DevCollector] Scheduler veri hatası: {ex.Message}");
            }
        }

        // ── D) Uyku modu analizi ──────────────────────────────────

        private void CollectSleepAnalysis()
        {
            if (Campaign.Current == null) return;
            double nowHours = CampaignTime.Now.ToHours;

            foreach (var party in Campaign.Current.MobileParties)
            {
                if (party?.PartyComponent is not MilitiaPartyComponent comp) continue;
                if (!party.IsActive) continue;

                double nextHour = comp.NextThinkTime.ToHours;
                float remaining = (float)(nextHour - nowHours);

                AppendRow(_sleepLog, new[]
                {
                    Now(),
                    ((int)nowHours).ToString(),
                    Csv(party.StringId),
                    Csv(party.Name?.ToString()),
                    nextHour.ToString("F1"),
                    remaining.ToString("F1"),
                    comp.Role.ToString(),
                    comp.CurrentState.ToString(),
                    (comp.CurrentOrder != null).ToString()
                });
            }
        }

        // ── E) Sistem anlık görüntüsü ─────────────────────────────

        private void CollectSystemSnapshot()
        {
            if (Campaign.Current == null) return;

            var modules = new (string name, Func<string> diag)[]
            {
                ("StaticDataCache",    () => StaticDataCache.Instance.GetDiagnostics()),
                ("AIScheduler",        () => ModuleManager.Instance.GetModule<AISchedulerSystem>()?.GetDiagnostics() ?? "N/A"),
                ("SpatialGrid",        () => SpatialGridSystem.Instance.GetDiagnostics()),
                ("MLSystem",           () => ModuleManager.Instance.GetModule<AILearningSystem>()?.GetDiagnostics() ?? "N/A"),
                ("DistanceCache",      () => $"CacheSize={SettlementDistanceCache.Instance.CacheSize}"),
                ("ActiveMilitias",     () => $"Count={Campaign.Current.MobileParties.Count(p => p?.PartyComponent is MilitiaPartyComponent && p.IsActive)}"),
                ("ModuleManager",      () => ModuleManager.Instance.GetDiagnostics()),
            };

            foreach (var (name, diagFn) in modules)
            {
                try
                {
                    AppendRow(_snapshotLog, new[]
                    {
                        Now(), Day(), name, Csv(diagFn())
                    });
                }
                catch { }
            }
        }

        // ── F) Ekonomi verisi ─────────────────────────────────────

        private void CollectEconomyData()
        {
            if (Campaign.Current == null) return;

            float globalProsperity = WarlordEconomySystem.Instance.CalculateGlobalProsperityAvg();

            foreach (var party in Campaign.Current.MobileParties)
            {
                if (party?.PartyComponent is not MilitiaPartyComponent comp) continue;
                if (!party.IsActive) continue;

                AppendRow(_economyLog, new[]
                {
                    Now(), Day(),
                    Csv(party.StringId),
                    Csv(party.Name?.ToString()),
                    Csv(comp.WarlordId),
                    party.PartyTradeGold.ToString(),
                    (party.MemberRoster?.TotalManCount ?? 0).ToString(),
                    comp.Role.ToString(),
                    comp.CurrentState.ToString(),
                    globalProsperity.ToString("F1")
                });
            }
        }

        // ── G) Warlord kariyer olayları ───────────────────────────

        private void OnTierChanged(CareerTierChangedEvent evt)
        {
            if (!IsEnabled || evt?.Warlord == null) return;

            float legitimacy = 0f;
            try
            {
                legitimacy = (float)(WarlordLegitimacySystem.Instance?.GetLevel(evt.Warlord.StringId) ?? 0);
            }
            catch { }

            AppendRow(_warlordLog, new[]
            {
                Now(), Day(),
                Csv(evt.Warlord.StringId),
                Csv(evt.Warlord.Name),
                "TierChanged",
                evt.PreviousTier.ToString(),
                evt.NewTier.ToString(),
                legitimacy.ToString("F0"),
                GetWarlordBattleCount(evt.Warlord).ToString(),
                evt.Warlord.TotalBounty.ToString("F0"),
                GetMilitiaCount(evt.Warlord).ToString()
            });
        }

        private void OnFatihPromotion(CareerFatihPromotionEvent evt)
        {
            if (!IsEnabled || evt?.Warlord == null) return;

            AppendRow(_warlordLog, new[]
            {
                Now(), Day(),
                Csv(evt.Warlord.StringId),
                Csv(evt.Warlord.Name),
                "FatihPromotion",
                "4", "5",
                "MAX",
                GetWarlordBattleCount(evt.Warlord).ToString(),
                evt.Warlord.TotalBounty.ToString("F0"),
                GetMilitiaCount(evt.Warlord).ToString()
            });
        }

        private void OnWarlordFallen(WarlordFallenEvent evt)
        {
            if (!IsEnabled || evt?.Warlord == null) return;

            AppendRow(_warlordLog, new[]
            {
                Now(), Day(),
                Csv(evt.Warlord.StringId),
                Csv(evt.Warlord.Name),
                "Fallen",
                "", "",
                "",
                GetWarlordBattleCount(evt.Warlord).ToString(),
                evt.Warlord.TotalBounty.ToString("F0"),
                "0"
            });
        }

        // ── H) AI karar kaydı (dışarıdan çağrılır) ───────────────

        /// <summary>
        /// CustomMilitiaAI veya MilitiaDecider'dan çağrılır.
        /// Üretimde IsEnabled=false → bu metot anında döner.
        /// </summary>
        private static int GetWarlordBattleCount(Warlord warlord)
        {
            if (warlord == null) return 0;

            return warlord.CommandedMilitias
                .Where(p => p?.PartyComponent is MilitiaPartyComponent)
                .Sum(p => ((MilitiaPartyComponent)p.PartyComponent).BattlesWon);
        }

        private static int GetMilitiaCount(Warlord warlord)
        {
            if (warlord == null) return 0;

            return warlord.CommandedMilitias.Count(p => p != null && p.IsActive);
        }

        public void RecordAIDecision(
            MobileParty party,
            string decision,
            string source,
            float score,
            float sleepHours,
            string? targetId = null,
            float mlConfidence = 0f,
            string aiState = "")
        {
            if (!IsEnabled || party == null) return;
            _sessionDecisions++;

            var comp = party.PartyComponent as MilitiaPartyComponent;
            double nextHour = comp?.NextThinkTime.ToHours ?? 0.0;

            AppendRow(_aiLog, new[]
            {
                Now(), Day(),
                Csv(party.StringId),
                Csv(party.Name?.ToString()),
                decision, source,
                score.ToString("F1"),
                sleepHours.ToString("F1"),
                nextHour.ToString("F1"),
                Csv(targetId),
                mlConfidence.ToString("F2"),
                aiState
            });
        }

        // ── Session özeti ─────────────────────────────────────────

        private void UpdateSummary()
        {
            if (Campaign.Current == null) return;
            float currentDay = (float)CampaignTime.Now.ToDays;
            float elapsedDays = currentDay - _sessionStartDay;

            int activeMilitias = Campaign.Current.MobileParties
                .Count(p => p?.PartyComponent is MilitiaPartyComponent && p.IsActive);

            int sleeping = Campaign.Current.MobileParties
                .Where(p => p?.PartyComponent is MilitiaPartyComponent && p.IsActive)
                .Count(p =>
                {
                    var c = p.PartyComponent as MilitiaPartyComponent;
                    return c != null && CampaignTime.Now < c.NextThinkTime;
                });

            float winRate = _sessionBattles > 0
                ? (float)_sessionWins / _sessionBattles * 100f : 0f;

            float spawnRate = elapsedDays > 0
                ? _sessionSpawns / elapsedDays : 0f;

            var sb = new StringBuilder();
            _ = sb.AppendLine("=== BanditMilitias Dev Session Summary ===");
            _ = sb.AppendLine($"Session dir : {_sessionDir}");
            _ = sb.AppendLine($"Campaign day: {currentDay:F1} (elapsed: {elapsedDays:F1} days)");
            _ = sb.AppendLine($"Updated     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _ = sb.AppendLine();
            _ = sb.AppendLine("--- Spawn ---");
            _ = sb.AppendLine($"Total spawns  : {_sessionSpawns}");
            _ = sb.AppendLine($"Total deaths  : {_sessionDeaths}");
            _ = sb.AppendLine($"Active now    : {activeMilitias}");
            _ = sb.AppendLine($"Spawn/day     : {spawnRate:F1}");
            _ = sb.AppendLine();
            _ = sb.AppendLine("--- Combat ---");
            _ = sb.AppendLine($"Battles fought: {_sessionBattles}");
            _ = sb.AppendLine($"Battles won   : {_sessionWins}");
            _ = sb.AppendLine($"Win rate      : {winRate:F1}%");
            _ = sb.AppendLine();
            _ = sb.AppendLine("--- AI ---");
            _ = sb.AppendLine($"Decisions made: {_sessionDecisions}");
            _ = sb.AppendLine($"Sleeping now  : {sleeping}/{activeMilitias}");
            _ = sb.AppendLine($"Sleep pct     : {(activeMilitias > 0 ? (float)sleeping / activeMilitias * 100f : 0f):F1}%");
            _ = sb.AppendLine($"Hourly ticks  : {_hourlyTicks}");
            _ = sb.AppendLine();
            _ = sb.AppendLine("--- System ---");

            try
            {
                _ = sb.AppendLine($"DistanceCache : {SettlementDistanceCache.Instance.CacheSize} entries");
                _ = sb.AppendLine($"Grid          : {SpatialGridSystem.Instance.GetDiagnostics()}");
                _ = sb.AppendLine($"ML System     : {ModuleManager.Instance.GetModule<AILearningSystem>()?.GetDiagnostics() ?? "N/A"}");
            }
            catch { }

            File.WriteAllText(_summaryFile, sb.ToString());
        }

        // ── Manuel export komutu ──────────────────────────────────

        [CommandLineFunctionality.CommandLineArgumentFunction("dev_export", "militia")]
        public static string CommandDevExport(List<string> args)
        {
            var inst = ModuleManager.Instance.GetModule<DevDataCollector>();
            if (inst == null || !inst.IsEnabled)
                return "DevMode deaktif. Settings'ten DevMode=true yapın.";
            inst.UpdateSummary();
            inst.CollectSystemSnapshot();
            inst.CollectEconomyData();
            return $"Export tamamlandı → {inst._sessionDir}";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("dev_status", "militia")]
        public static string CommandDevStatus(List<string> args)
        {
            var inst = ModuleManager.Instance.GetModule<DevDataCollector>();
            if (inst == null || !inst.IsEnabled)
                return "DevMode deaktif.";

            return $"DevDataCollector AKTIF\n" +
                   $"  Spawns: {inst._sessionSpawns}\n" +
                   $"  Deaths: {inst._sessionDeaths}\n" +
                   $"  Battles: {inst._sessionBattles} (Win: {inst._sessionWins})\n" +
                   $"  AI Decisions: {inst._sessionDecisions}\n" +
                   $"  FullSim: {(inst._fullSimEnabled ? $"ON ({inst._fullSimIntervalHours}h)" : "OFF")}\n" +
                   $"  Dir: {inst._sessionDir}";
        }

        // ── Temizlik ──────────────────────────────────────────────

        [CommandLineFunctionality.CommandLineArgumentFunction("full_sim_test", "militia")]
        public static string CommandFullSimTest(List<string> args)
        {
            DevDataCollector? inst = EnsureCollectorReady();
            if (inst == null)
                return "DevDataCollector hazirlanamadi. Settings.Instance yuklu degil.";

            if (args != null && args.Count > 0)
            {
                string mode = (args[0] ?? string.Empty).Trim().ToLowerInvariant();
                if (mode is "stop" or "off" or "disable")
                {
                    inst._fullSimEnabled = false;
                    inst._nextFullSimCaptureHour = double.NaN;
                    return $"full_sim_test durduruldu -> {inst._fullSimDir}";
                }

                if (mode is "status")
                    return inst.GetFullSimStatus();

                if (mode is "once" or "run")
                {
                    inst.RunFullSimCapture("manual-once");
                    return $"full_sim_test tek seferlik capture tamamlandi -> {inst._fullSimDir}";
                }

                if (int.TryParse(mode, out int parsedHours))
                {
                    inst.EnableFullSimAutomation(parsedHours);
                    return $"full_sim_test otomasyonu acildi ({inst._fullSimIntervalHours} saat) -> {inst._fullSimDir}";
                }
            }

            inst.EnableFullSimAutomation(inst._fullSimIntervalHours);
            return $"full_sim_test otomasyonu acildi ({inst._fullSimIntervalHours} saat) -> {inst._fullSimDir}";
        }

        public override void Cleanup()
        {
            if (!IsEnabled) return;
            if (_fullSimEnabled)
            {
                RunFullSimCapture("cleanup");
            }
            UpdateSummary();
            UnsubscribeEvents();
            _instance = null;
        }

        // ── Yardımcılar ───────────────────────────────────────────

        private static void WriteHeader(string path, string header)
        {
            try
            {
                _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path))
                    File.WriteAllText(path, header + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private void AppendRow(string path, string[] columns)
        {
            try
            {
                string line = string.Join(",", columns) + Environment.NewLine;
                lock (_writeLock)
                    File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch { }
        }

        private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        private static string Day() => Campaign.Current != null
            ? CampaignTime.Now.ToDays.ToString("F2") : "0";
        private static string Csv(string? s) => $"\"{(s ?? "").Replace("\"", "\"\"")}\"";

        private static MobileParty? FindEnemy(TaleWorlds.CampaignSystem.MapEvents.MapEvent mapEvent, MobileParty militia)
        {
            bool isAttacker = mapEvent.AttackerSide.Parties.Any(p => p.Party.MobileParty == militia);
            var oppositeSide = isAttacker ? mapEvent.DefenderSide : mapEvent.AttackerSide;
            return oppositeSide.Parties
                .Select(p => p.Party.MobileParty)
                .FirstOrDefault(p => p != null && p != militia);
        }

        private static string GetPersonality(MilitiaPartyComponent? comp)
        {
            if (comp == null) return "";
            // PersonalityType BanditBrain üzerinden okunur
            try
            {
                var brain = BanditBrain.Instance;
                return brain != null ? "HasBrain" : "NoBrain";
            }
            catch { return ""; }
        }

        public void RecordModuleTiming(string moduleName, long ms, string context = "")
        {
            if (!IsEnabled || !_isPathInitialized) return;
            AppendRow(_timingLog, new[]
            {
                Now(), Day(), moduleName, ms.ToString(), Csv(context)
            });
        }

        public void RecordPathfindingCall()
        {
            if (!IsEnabled || !_isPathInitialized) return;
            _ = System.Threading.Interlocked.Increment(ref _sessionPathfinds);
        }

        private static DevDataCollector? EnsureCollectorReady()
        {
            if (Settings.Instance == null)
                return null;

            var inst = ModuleManager.Instance.GetModule<DevDataCollector>() ?? Instance;
            if (Settings.Instance.DevMode != true)
                Settings.Instance.DevMode = true;

            if (!inst._isPathInitialized)
            {
                inst.Initialize();
                inst.RegisterCampaignEvents();
            }

            return inst;
        }

        private void EnableFullSimAutomation(int intervalHours)
        {
            if (!_isPathInitialized)
                return;

            _fullSimIntervalHours = System.Math.Max(1, intervalHours);
            _fullSimEnabled = true;
            _nextFullSimCaptureHour = Campaign.Current != null
                ? CampaignTime.Now.ToHours + _fullSimIntervalHours
                : _fullSimIntervalHours;

            RunFullSimCapture("enabled");
        }

        private void ProcessFullSimAutomation()
        {
            if (!_fullSimEnabled || Campaign.Current == null || !_isPathInitialized)
                return;

            double nowHours = CampaignTime.Now.ToHours;
            if (double.IsNaN(_nextFullSimCaptureHour))
            {
                _nextFullSimCaptureHour = nowHours + _fullSimIntervalHours;
                return;
            }

            if (nowHours < _nextFullSimCaptureHour)
                return;

            RunFullSimCapture("scheduled");
            _nextFullSimCaptureHour = nowHours + _fullSimIntervalHours;
        }

        private void RunFullSimCapture(string trigger)
        {
            if (!_isPathInitialized)
                return;

            try
            {
                UpdateSummary();
                CollectSystemSnapshot();
                CollectEconomyData();
                CollectPathfindingSnapshot();
                BattleDataLogger.EnsureArtifactsExist();

                var mlSystem = ModuleManager.Instance.GetModule<AILearningSystem>();
                if (mlSystem != null)
                {
                    BattleDataLogger.ExportQTable(mlSystem.ExportQTable());
                }

                _fullSimRunCount++;

                int activeMilitias = Campaign.Current?.MobileParties
                    .Count(p => p?.PartyComponent is MilitiaPartyComponent && p.IsActive) ?? 0;

                AppendRow(_fullSimRunLog, new[]
                {
                    Now(),
                    Day(),
                    Csv(trigger),
                    _fullSimRunCount.ToString(),
                    _fullSimIntervalHours.ToString(),
                    activeMilitias.ToString(),
                    _sessionBattles.ToString(),
                    _sessionWins.ToString(),
                    Csv(_sessionDir),
                    Csv(BattleDataLogger.BattleLogPath),
                    Csv(BattleDataLogger.QTablePath)
                });

            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DevCollector] full_sim_test capture hatasi: {ex.Message}");
            }
        }

        private string GetFullSimStatus()
        {
            string nextRun = double.IsNaN(_nextFullSimCaptureHour)
                ? "n/a"
                : _nextFullSimCaptureHour.ToString("F1");

            return "full_sim_test\n" +
                   $"  Enabled: {_fullSimEnabled}\n" +
                   $"  IntervalHours: {_fullSimIntervalHours}\n" +
                   $"  Runs: {_fullSimRunCount}\n" +
                   $"  NextCampaignHour: {nextRun}\n" +
                   $"  Dir: {_fullSimDir}";
        }

        private void CollectPathfindingSnapshot()
        {
            if (!IsEnabled || !_isPathInitialized) return;
            int currentRequests = _sessionPathfinds;
            _ = System.Threading.Interlocked.Exchange(ref _sessionPathfinds, 0);

            int activeMilitias = Campaign.Current?.MobileParties
                .Count(p => p?.PartyComponent is MilitiaPartyComponent && p.IsActive) ?? 0;

            float ratio = activeMilitias > 0 ? (float)currentRequests / activeMilitias : 0f;

            AppendRow(_pathLog, new[]
            {
                Now(), Day(), currentRequests.ToString(), activeMilitias.ToString(), ratio.ToString("F2")
            });
        }

        private static int ExtractInt(string text, string after, char until)
        {
            int idx = text.IndexOf(after, StringComparison.Ordinal);
            if (idx < 0) return 0;
            idx += after.Length;
            int end = text.IndexOf(until, idx);
            if (end < 0) end = text.Length;
            return int.TryParse(text.Substring(idx, end - idx).Trim(), out int v) ? v : 0;
        }
    }
}
