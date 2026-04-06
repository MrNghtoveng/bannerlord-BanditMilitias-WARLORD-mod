using BanditMilitias.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.AI.Components;
using BanditMilitias.Intelligence.Logging;
using BanditMilitias.Intelligence.ML;
using BanditMilitias.Systems.Bounty;
using BanditMilitias.Systems.Fear;
using BanditMilitias.Systems.Progression;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using MathF = TaleWorlds.Library.MathF;

namespace BanditMilitias.Intelligence.Strategic
{
    public sealed partial class BanditBrain : BanditMilitias.Core.Components.MilitiaModuleBase
    {

        public override string ModuleName => "BanditBrain";
        public override bool IsEnabled => Settings.Instance?.EnableCustomAI ?? true;
        public override int Priority => 78;

        public PersonalityType PersonalityType { get; set; } = PersonalityType.Cunning;

        private static readonly Lazy<BanditBrain> _instance =
            new(() => new BanditBrain());

        public static BanditBrain Instance => _instance.Value;

        private BrainState _currentState = BrainState.Dormant;
        private bool _isInitialized = false;
        private readonly object _stateLock = new();

        private readonly Dictionary<Settlement, ThreatAssessment> _threatMap = new();

        private PlayerProfile _playerProfile = new();

        private Dictionary<string, MilitiaPerformance> _militiaMetrics = new();

        private readonly CircularBuffer<CombatOutcome> _combatHistory = new(50);

        private Dictionary<string, CommandFeedback> _commandFeedback = new();
        private int _totalCommandsIssued = 0;
        private int _successfulCommands = 0;
        private float _overallSuccessRate = 0f;

        private readonly Dictionary<CommandType, float> _baseUtilityScores = new()
        {
            { CommandType.Patrol, 0.3f },
            { CommandType.Ambush, 0.7f },
            { CommandType.Retreat, 0.5f },
            { CommandType.Hunt, 0.8f },
            { CommandType.Harass, 0.6f },
            { CommandType.Defend, 0.7f },
            { CommandType.AvoidCrowd, 0.4f },
            { CommandType.Raid, 0.7f },
            { CommandType.Engage, 0.8f },
            { CommandType.Scavenge, 0.4f },
            { CommandType.Retrieve, 0.3f },

            { CommandType.CommandRaidVillage, 0.6f },
            { CommandType.CommandLayLow, 0.5f },
            { CommandType.CommandExtort, 0.5f },
            { CommandType.CommandBuildRepute, 0.4f }
        };

        private readonly CoordinationPlanner _planner = new();
        private readonly QuantumWalkPathfinder _pathfinder = new();
        private readonly EntangledCoalition _coalition = new();
        private readonly Dictionary<string, CommandType> _recentDecisions = new();

        private readonly Dictionary<PersonalityType, IPersonalityStrategy> _strategies = new();

        private readonly BanditMilitias.Infrastructure.PIDController _responseController = new(
            kp: 0.5f, ki: 0.1f, kd: 0.2f, setpoint: 0.7f);

        private float _explorationRate = 0.2f;

        private Dictionary<CommandType, RunningAverage> _actionSuccessRates = new();

        private readonly LRUCache<string, float> _computationCache = new(100);

        private int _totalDecisionsMade = 0;
        private int _successfulCoordinations = 0;
        private int _failedCoordinations = 0;
        private float _averageDecisionTime = 0f;

        private Dictionary<CommandType, RunningAverage>? _backupActionSuccessRates;
        private Dictionary<string, CommandFeedback>? _backupCommandFeedback;
        private float _backupExplorationRate = 0.2f;
        private int _backupTotalDecisions = 0;

        private BanditBrain()
        {
            InitializePersonalityStrategies();
            InitializeActionSuccessTrackers();
        }

        public override void Initialize()
        {
            lock (_stateLock)
            {
                if (_isInitialized) return;

                EventBus.Instance.Subscribe<MilitiaKilledEvent>(OnMilitiaKilled);
                EventBus.Instance.Subscribe<PlayerEnteredTerritoryEvent>(OnPlayerTerritory);
                EventBus.Instance.Subscribe<MilitiaMergeEvent>(OnMilitiaMerge);
                EventBus.Instance.Subscribe<HideoutClearedEvent>(OnHideoutCleared);
                EventBus.Instance.Subscribe<CommandCompletionEvent>(OnCommandCompletion);
                EventBus.Instance.Subscribe<ThreatLevelChangedEvent>(OnThreatLevelChanged);
                EventBus.Instance.Subscribe<MilitiaSpawnedEvent>(OnMilitiaSpawned);
                EventBus.Instance.Subscribe<MilitiaDisbandedEvent>(OnMilitiaDisbanded);

                // Watchdog kaydı SubModule.cs'de yapılıyor (güvenli zamanlama)

                // FIX: Cleanup() sonrası _strategies ve _actionSuccessRates boş kalır.
                // Constructor'da bir kez dolduruluyor ama Cleanup() bunları temizliyor.
                // Initialize() her çağrıldığında yeniden doldurmak zorunlu.
                if (_strategies.Count == 0)
                    InitializePersonalityStrategies();
                if (_actionSuccessRates.Count == 0)
                    InitializeActionSuccessTrackers();

                // UpdatePlayerProfile sadece kampanya tamamen hazırsa çağır
                _isInitialized = true;
                _currentState = BrainState.Dormant;
            }
        }

        public override void RegisterCampaignEvents()
        {
            lock (_stateLock)
            {
                if (!_isInitialized || _currentState == BrainState.Active)
                {
                    return;
                }

                if (CompatibilityLayer.IsGameplayActivationDelayed())
                {
                    _currentState = BrainState.Dormant;
                    DebugLogger.Info("BanditBrain", "Gameplay activation is delayed. Brain set to DORMANT.");
                }
                else
                {
                    UpdatePlayerProfile();
                    _currentState = BrainState.Active;
                    AIDecisionLogger.LogSessionStart();
                }

                if (Settings.Instance?.TestingMode == true && Settings.Instance?.ShowTestMessages == true)
                {
                    InformationManager.DisplayMessage(new InformationMessage(
                        "[BanditBrain] Advanced AI Online - Adaptive Mode Enabled",
                        Colors.Cyan));
                }
            }
        }

        public override void OnHourlyTick()
        {
            if (_currentState == BrainState.Dormant)
            {
                if (!CompatibilityLayer.IsGameplayActivationDelayed())
                {
                    _currentState = BrainState.Active;
                    UpdatePlayerProfile();
                    AIDecisionLogger.LogSessionStart();
                    DebugLogger.Info("BanditBrain", "Gameplay activation delay expired. Brain is now ACTIVE.");
                }
                else return;
            }

            if (_currentState != BrainState.Active) return;

            var timer = System.Diagnostics.Stopwatch.StartNew();

            try
            {

                if (Settings.Instance?.EnableSpatialAwareness == true)
                {
                    UpdateThreatAssessments();
                }

                ProcessCoordinations();
                EvaluateStrategicOpportunities();

                IssueAdaptiveCommands();

                UpdateActionSuccessRates();

            }
            catch (Exception ex)
            {
                HandleCriticalError("OnHourlyTick", ex);
            }
            finally
            {
                timer.Stop();
                _averageDecisionTime = (_averageDecisionTime * 0.9f) +
                                      ((float)timer.Elapsed.TotalMilliseconds * 0.1f);

                Systems.Diagnostics.SystemWatchdog.Instance.ReportHeartbeat("BanditBrain");
                Systems.Diagnostics.SystemWatchdog.Instance.ReportHeartbeat("NeuralActivity");
            }
        }

        public override void OnDailyTick()
        {
            if (_currentState != BrainState.Active) return;

            try
            {

                UpdatePlayerProfile();
                OptimizeThreatResponse();
                PruneStaleData();

                if (_playerProfile.TotalKills > 0)
                {
                    _playerProfile.TotalKills = Math.Max(1, (int)(_playerProfile.TotalKills * 0.985f));
                }

                _responseController.Reset();

                AIDecisionLogger.LogDailySummary(
                    _totalDecisionsMade,
                    _totalCommandsIssued,
                    _overallSuccessRate,
                    _explorationRate,
                    _commandFeedback.Count);
            }
            catch (Exception ex)
            {
                HandleCriticalError("OnDailyTick", ex);
            }
        }

        public override void Cleanup()
        {
            lock (_stateLock)
            {
                EventBus.Instance.Unsubscribe<MilitiaKilledEvent>(OnMilitiaKilled);
                EventBus.Instance.Unsubscribe<PlayerEnteredTerritoryEvent>(OnPlayerTerritory);
                EventBus.Instance.Unsubscribe<MilitiaMergeEvent>(OnMilitiaMerge);
                EventBus.Instance.Unsubscribe<HideoutClearedEvent>(OnHideoutCleared);
                EventBus.Instance.Unsubscribe<CommandCompletionEvent>(OnCommandCompletion);
                EventBus.Instance.Unsubscribe<ThreatLevelChangedEvent>(OnThreatLevelChanged);
                EventBus.Instance.Unsubscribe<MilitiaSpawnedEvent>(OnMilitiaSpawned);
                EventBus.Instance.Unsubscribe<MilitiaDisbandedEvent>(OnMilitiaDisbanded);

                _backupActionSuccessRates = new Dictionary<CommandType, RunningAverage>(_actionSuccessRates);
                _backupCommandFeedback = new Dictionary<string, CommandFeedback>(_commandFeedback);
                _backupExplorationRate = _explorationRate;
                _backupTotalDecisions = _totalDecisionsMade;

                _threatMap.Clear();
                _militiaMetrics.Clear();
                _combatHistory.Clear();
                _planner.Clear();
                _computationCache.Clear();

                _playerProfile.CurrentStrength = 0;
                _playerProfile.ClanTier = 0;

                _commandFeedback.Clear();
                _actionSuccessRates.Clear();  // Initialize()'da yeniden doldurulacak
                _strategies.Clear();           // Initialize()'da yeniden doldurulacak
                _explorationRate = 0.2f;
                _totalDecisionsMade = 0;
                _successfulCommands = 0;
                _overallSuccessRate = 0f;

                _currentState = BrainState.Dormant;
                _isInitialized = false;
            }
        }

        private void OnMilitiaKilled(MilitiaKilledEvent e)
        {

            if (e.WasPlayerKill)
            {
                _playerProfile.TotalKills++;
                _playerProfile.LastKillTime = CampaignTime.Now;

                _combatHistory.Add(new CombatOutcome
                {
                    PlayerWon = true,
                    PlayerStrength = GetPlayerStrength(),
                    MilitiaStrength = GetPartyStrength(e.Victim),
                    Timestamp = CampaignTime.Now
                });

                const float elapsedHours = 1.0f;
                _responseController.Update(_playerProfile.GetCurrentThreat(), elapsedHours);
            }

            if (e.Victim != null)
            {
                string militiaId = e.Victim.StringId;
                if (_militiaMetrics.TryGetValue(militiaId, out var metrics))
                {
                    metrics.Deaths++;
                    metrics.LastDeathTime = CampaignTime.Now;
                }
            }
        }

        private void OnThreatLevelChanged(ThreatLevelChangedEvent e)
        {

            if (e.ThreatDelta > 0)
            {

                _responseController.Update(e.NewThreatLevel, 1.0f);
            }

            _playerProfile.UpdateThreatCache(e.NewThreatLevel);

            if (Settings.Instance?.TestingMode == true)
            {
                DebugLogger.Info("BanditBrain", $"[Threat Awareness] Level changed: {e.OldThreatLevel:F2} -> {e.NewThreatLevel:F2} ({e.Reason})");
            }
        }

        private void OnPlayerTerritory(PlayerEnteredTerritoryEvent e)
        {
            if (e.NearbyHideout == null) return;

            var warlord = WarlordSystem.Instance.GetWarlordForHideout(e.NearbyHideout);
            if (warlord == null) return;

            DecideWarlordResponse(warlord, e);
        }

        private void DecideWarlordResponse(Warlord warlord, PlayerEnteredTerritoryEvent e)
        {

            var threat = AssessThreatFuzzy(e.NearbyHideout);
            if (e.NearbyHideout != null)
            {
                _threatMap[e.NearbyHideout] = threat;
            }

            if (!_strategies.TryGetValue(warlord.Personality, out var strategy))
                return;

            float confidence = 1.0f;
            bool strategyOverridden = false;
            if (_commandFeedback.TryGetValue(warlord.StringId, out var feedback))
            {
                confidence = feedback.StrategyConfidence;

                if (feedback.SuccessRate < 0.3f && threat.OverallThreat > 0.7f)
                {

                    if (_strategies.TryGetValue(PersonalityType.Cautious, out var cautiousStrategy))
                    {
                        strategy = cautiousStrategy;
                        strategyOverridden = true;
                    }
                }
            }

            var level = WarlordLegitimacySystem.Instance != null
                ? WarlordLegitimacySystem.Instance.GetLevel(warlord.StringId)
                : LegitimacyLevel.Outlaw;
            var command = strategy.DetermineResponse(warlord, level, threat, _playerProfile);

            // ── BACKSTORY DAVRANIŞI: Kişisel geçmişe göre komut önceliği ayarı ──
            // BetrayedNoble: Lord partilerine karşı agresif (+%40 Hunt önceliği)
            // VengefulSurvivor: Düşman hasarını maksimize et, ölüm korkusu yok
            // FailedMercenary: Yüksek altın hedefleri tercih et (kervan/şehir)
            // ExiledLeader: Stratejik bölge kontrolü, geri çekilmekten kaçın
            // AmbitionDriven: Her zaman büyüme — Patrol → Ambush dönüşümü
            try
            {
                switch (warlord.Backstory)
                {
                    case BackstoryType.BetrayedNoble:
                        // Lord partilerini avla — intikam odaklı
                        if (command.Type == CommandType.Ambush || command.Type == CommandType.Patrol)
                        {
                            command.Priority *= 1.4f;
                            command.Reason += " [Backstory: İhanet edilmiş soylu — lordları avla]";
                        }
                        break;

                    case BackstoryType.VengefulSurvivor:
                        // Tehditten kaçınma — saldırı = daha yüksek hasar
                        if (command.Type == CommandType.Hunt || command.Type == CommandType.Ambush)
                        {
                            command.Priority *= 1.25f;
                            command.Reason += " [Backstory: İntikamcı hayatta kalan]";
                        }
                        break;

                    case BackstoryType.FailedMercenary:
                        // Ekonomik hedefler önce — kervan ve şehir
                        if (command.Type == CommandType.Patrol)
                        {
                            command.Type = CommandType.Ambush;
                            command.Reason += " [Backstory: Başarısız paralı asker — altın önce]";
                        }
                        break;

                    case BackstoryType.ExiledLeader:
                        // Bölge kontrolü — geri çekilme yok
                        if (command.Type == CommandType.Retreat || command.Type == CommandType.Defend)
                        {
                            command.Type = CommandType.Patrol;
                            command.Priority *= 1.15f;
                            command.Reason += " [Backstory: Sürgün lider — toprak savunusu]";
                        }
                        break;

                    case BackstoryType.AmbitionDriven:
                        // Her zaman büyü — pasif emirleri aktife çevir
                        if (command.Type == CommandType.Patrol && threat.OverallThreat < 0.6f)
                        {
                            command.Type = CommandType.Ambush;
                            command.Reason += " [Backstory: Hırslı — sürekli büyüme]";
                        }
                        break;
                }
            }
            catch { /* Backstory sistemi hazır değilse atla */ }
            // ── BACKSTORY DAVRANIŞI SONU ──────────────────────────────────────

            var drive = WarlordLegitimacySystem.Instance != null
                ? WarlordLegitimacySystem.Instance.ComputePromotionDrive(warlord)
                : default;

            // Promotion Drive Dynamics: AI davranışlarını rütbe hedeflerine göre bükme
            if (WarlordLegitimacySystem.Instance != null && !drive.IsCloseToPromotion)
            {
                if (drive.WealthDrive > 0.6f && (command.Type == CommandType.Patrol || command.Type == CommandType.Defend))
                {
                    command.Type = CommandType.Ambush;
                    command.Reason += " (Promotion Wealth Drive)";
                    command.Priority *= 1.25f;
                }
                else if (drive.PowerDrive > 0.6f && (command.Type == CommandType.Hunt || command.Type == CommandType.Ambush))
                {
                    command.Type = CommandType.Patrol;
                    command.Reason += " (Promotion Power Drive)";
                    command.Priority *= 0.85f;
                }
                else if (drive.HonorDrive > 0.7f && (command.Type == CommandType.Defend || command.Type == CommandType.Patrol))
                {
                    command.Type = CommandType.Hunt;
                    command.Reason += " (Promotion Honor Drive)";
                }
            }
            else if (drive.IsCloseToPromotion)
            {
                if (threat.OverallThreat > 0.5f)
                {
                    command = MakeCommandMoreConservative(command);
                    command.Reason += " (Promotion Integrity Strategy)";
                }
            }

            bool madeConservative = false;
            if (confidence < 0.5f && _explorationRate < 0.05f)
            {
                bool isHighLevel = level >= LegitimacyLevel.Warlord;
                if (!isHighLevel || threat.OverallThreat > 0.95f)
                {
                    command = MakeCommandMoreConservative(command);
                    madeConservative = true;
                }
            }

            AIDecisionLogger.LogWarlordResponse(
                warlord.StringId,
                warlord.Personality.ToString(),
                threat.OverallThreat,
                confidence,
                command.Type.ToString(),
                strategyOverridden || madeConservative,
                strategyOverridden ? "Low success rate + high threat -> Cautious" :
                madeConservative ? $"Low confidence ({confidence:F2}) -> Conservative" : null);

            var assessment = CreateStrategicAssessment(warlord);
            var assessmentEvt = EventBus.Instance.Get<StrategicAssessmentEvent>();
            assessmentEvt.TargetWarlord = warlord;
            assessmentEvt.Assessment = assessment;
            EventBus.Instance.Publish(assessmentEvt);
            EventBus.Instance.Return(assessmentEvt);

            IssueCommandToWarlord(warlord, command);
        }

        private StrategicCommand MakeCommandMoreConservative(StrategicCommand command)
        {

            if (command.Type == CommandType.Hunt || command.Type == CommandType.Ambush)
            {
                var newCmd = CloneCommand(command);
                newCmd.Type = CommandType.Defend;
                newCmd.TargetLocation = command.TargetLocation;
                newCmd.Priority = command.Priority * 0.7f;
                newCmd.Reason = command.Reason + " (Conservative Override)";
                return newCmd;
            }

            return command;
        }

        private StrategicAssessment CreateStrategicAssessment(Warlord warlord)
        {

            float threat = Systems.Tracking.PlayerTracker.Instance.GetThreatLevel();

            var posture = threat switch
            {
                > 2.0f => StrategicPosture.Defensive,
                > 1.0f => StrategicPosture.Defensive,
                > 0.5f => StrategicPosture.Opportunistic,
                _ => StrategicPosture.Offensive
            };

            float confidence = 0.5f;
            if (_commandFeedback.TryGetValue(warlord.StringId, out var feedback))
            {
                confidence = feedback.StrategyConfidence;
            }

            return new StrategicAssessment
            {
                ThreatLevel = threat,
                RecommendedPosture = posture,
                PlayerThreat = threat,
                Confidence = confidence,
                AssessmentTime = CampaignTime.Now
            };
        }

        private void OnMilitiaMerge(MilitiaMergeEvent e)
        {

            if (e.ResultingParty != null)
            {
                _planner.NotifyCoalitionFormed(e.ResultingParty);
            }
        }

        private void OnMilitiaSpawned(MilitiaSpawnedEvent evt)
        {
            if (evt?.Party == null) return;
            _totalDecisionsMade++;

            if (Settings.Instance?.TestingMode == true)
                DebugLogger.Info("BanditBrain",
                    $"[Fleet+] {evt.Party.Name} doÄŸdu. Aktif milis: {ModuleManager.Instance.GetMilitiaCount()}");
        }

        private void OnMilitiaDisbanded(MilitiaDisbandedEvent evt)
        {
            if (evt?.Party == null) return;

            string? warlordId = (evt.Party.PartyComponent as MilitiaPartyComponent)?.WarlordId;
            if (warlordId != null && _commandFeedback.TryGetValue(warlordId, out var feedback))
            {
                feedback.TotalCommands /= 2;
                feedback.SuccessCount /= 2;
                feedback.FailureCount /= 2;
            }

            if (Settings.Instance?.TestingMode == true)
                DebugLogger.Info("BanditBrain",
                    $"[Fleet-] {evt.Party.Name} daÄŸÄ±tÄ±ldÄ±. {(evt.IsNuclearCleanup ? "(zorla)" : "")}");
        }

        private void OnCommandCompletion(CommandCompletionEvent evt)
        {
            if (evt.Party == null) return;

            try
            {

                var warlord = FindWarlordForMilitia(evt.Party);
                if (warlord == null) return;

                if (!_commandFeedback.TryGetValue(warlord.StringId, out var feedback))
                {
                    feedback = new CommandFeedback();
                    _commandFeedback[warlord.StringId] = feedback;
                }

                float oldConfidence = feedback.StrategyConfidence;
                feedback.TotalCommands++;
                _totalCommandsIssued++;

                if (evt.Status == CommandCompletionStatus.Success)
                {
                    feedback.SuccessCount++;
                    _successfulCommands++;

                    if (_strategies.TryGetValue(warlord.Personality, out var strategy))
                    {

                        feedback.StrategyConfidence = Math.Min(1f, feedback.StrategyConfidence + 0.05f);
                    }
                }
                else
                {
                    feedback.FailureCount++;

                    if (_strategies.TryGetValue(warlord.Personality, out var strategy))
                    {
                        feedback.StrategyConfidence = Math.Max(0.1f, feedback.StrategyConfidence - 0.1f);
                    }
                }

                feedback.SuccessRate = feedback.SuccessCount / (float)Math.Max(1, feedback.TotalCommands);
                _overallSuccessRate = _successfulCommands / (float)Math.Max(1, _totalCommandsIssued);

                AIDecisionLogger.LogCommandOutcome(
                    warlord.Name,
                    evt.Command?.Type.ToString() ?? "TacticalAI",
                    evt.Status.ToString(),
                    feedback.SuccessRate,
                    oldConfidence,
                    feedback.StrategyConfidence);

                if (evt.Command != null)
                {
                    var aiAction = AILearningSystem.MapCommandType(evt.Command.Type);
                    UpdateQLearning(evt.Command.Type, evt.Status == CommandCompletionStatus.Success);
                    AdjustSuccessRate(evt.Command.Type, evt.Status == CommandCompletionStatus.Success ? 1.0f : 0.0f);
                }

                if (Settings.Instance?.TestingMode == true)
                {
                    DebugLogger.Info("BanditBrain",
                        $"[Feedback] {warlord.Name}: {evt.Command?.Type.ToString() ?? "TacticalAI"} {evt.Status} " +
                        $"(Success rate: {feedback.SuccessRate:P0})");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error("BanditBrain", $"Feedback processing failed: {ex.Message}");
            }
        }

        private Warlord? FindWarlordForMilitia(MobileParty militia)
        {
            if (militia?.PartyComponent is not BanditMilitias.Components.MilitiaPartyComponent component)
                return null;

            if (component.HomeSettlement == null) return null;

            return WarlordSystem.Instance.GetWarlordForHideout(component.HomeSettlement);
        }

        private void UpdateQLearning(CommandType action, bool success)
        {

            float currentQ = GetActionQValue(action);

            float reward = success ? 1.0f : -0.5f;

            if (action == CommandType.CommandRaidVillage ||
                action == CommandType.CommandLayLow ||
                action == CommandType.CommandExtort ||
                action == CommandType.CommandBuildRepute)
            {

                reward = CalculateNewCommandReward(action, success, 500f, false);
            }

            const float alpha = 0.1f;
            const float gamma = 0.9f;

            float maxFutureQ = success
                ? (_baseUtilityScores.Count > 0 ? _baseUtilityScores.Values.Max() : 0.5f)
                : currentQ;

            float newQ = currentQ + alpha * (reward + gamma * maxFutureQ - currentQ);

            AIDecisionLogger.LogQLearningUpdate(
                action.ToString(), success, reward,
                currentQ, MathF.Clamp(newQ, 0f, 1f), _explorationRate);

            if (_baseUtilityScores.ContainsKey(action))
            {
                _baseUtilityScores[action] = MathF.Clamp(newQ, 0f, 1f);
            }

            _explorationRate = Math.Max(0.02f, _explorationRate * 0.995f);
        }

        private float GetActionQValue(CommandType action)
        {
            return _baseUtilityScores.TryGetValue(action, out float value) ? value : 0.5f;
        }

        private void OnHideoutCleared(HideoutClearedEvent e)
        {
            if (e.Hideout == null) return;

            _playerProfile.HideoutsDestroyed++;
            if (e.Hideout != null)
                _ = _threatMap.Remove(e.Hideout);

            var hideout = e.Hideout;
            if (hideout != null)
            {
                BroadcastDistressSignal(hideout, DistressLevel.Critical);
            }
        }

        private void UpdateEntanglements(List<Warlord> warlords)
        {
            for (int i = 0; i < warlords.Count; i++)
            {
                for (int j = i + 1; j < warlords.Count; j++)
                {
                    var w1 = warlords[i];
                    var w2 = warlords[j];
                    if (w1.IsAlive && w2.IsAlive && w1.AssignedHideout != null && w2.AssignedHideout != null)
                    {
                        float dist = GetDistance(w1.AssignedHideout, w2.AssignedHideout);
                        if (dist < 50f)
                        {
                            string c1 = w1.AssignedHideout.Culture?.StringId ?? "c1";
                            string c2 = w2.AssignedHideout.Culture?.StringId ?? "c2";

                            // FIX: Önceden farklı kültür = 0.0f (kayıtsız) idi.
                            // Aynı coğrafyada faaliyet gösteren farklı kültür eşkıyaları ortak
                            // düşmana (oyuncu/ordu) karşı koordine olmalı → pozitif korelasyon.
                            // Aynı kültür = rakiplik → negatif korelasyon (değişmedi).
                            // Mesafe faktörü: yakınlık arttıkça etki güçlenir (50 birimde 1.0, 0'da 0).
                            float distFactor = 1f - (dist / 50f); // [0..1]
                            float correlation = (c1 == c2)
                                ? -0.6f * distFactor   // rakip: uzaklaştıkça azalır
                                : +0.35f * distFactor; // müttefik: yakınlıkla orantılı

                            _coalition.Entangle(w1.StringId, w2.StringId, correlation);
                        }
                    }
                }
            }
            _coalition.Decoherence(0.95f);
        }

        private void ResolveCommandTarget(StrategicCommand command, Warlord warlord)
        {
            if (command.Type == CommandType.Raid || command.Type == CommandType.Hunt || 
                command.Type == CommandType.Ambush || command.Type == CommandType.CommandRaidVillage ||
                command.Type == CommandType.Harass || command.Type == CommandType.Engage)
            {
                var nodes = new List<StrategicNode>();
                var targets = new Dictionary<string, object>();
                
                var currentPos = warlord.AssignedHideout != null 
                    ? CompatibilityLayer.GetSettlementPosition(warlord.AssignedHideout) 
                    : Vec2.Zero;

                float maxDistance = 150f; // Warlord'un avlanma bölgesi (Geniş Bölge)

                // 1. Settlements (Şehirler, Kaleler, Köyler, Sığınaklar)
                var allSettlements = Settlement.All;
                if (allSettlements != null)
                {
                    foreach(var s in allSettlements)
                    {
                        if (!s.IsActive) continue;
                        if (s == warlord.AssignedHideout) continue; // Kendi mekanına saldırma

                        float dist = currentPos != Vec2.Zero ? currentPos.Distance(CompatibilityLayer.GetSettlementPosition(s)) : 0f;
                        if (dist > maxDistance) continue;

                        float threat = _threatMap.TryGetValue(s, out var t) ? t.OverallThreat : 0.5f;
                        float loot = Math.Max(0f, 1f - threat) * 0.5f; 
                        
                        if (s.IsVillage || s.IsTown) loot += 0.3f; // Kasaba ve köylerde ganimet daha iyidir

                        string id = "S_" + s.StringId;
                        targets[id] = s;
                        nodes.Add(new StrategicNode(
                            id, loot, threat, dist, 
                            s.Culture?.StringId ?? "unknown", 
                            dist < 20f ? 1.0f : 0.0f, TargetType.Settlement));
                    }
                }

                // 2. Mobile Parties (Kervanlar, Rakip Milisler, Lordlar)
                var allParties = MobileParty.All;
                if (allParties != null)
                {
                    foreach(var p in allParties)
                    {
                        if (warlord.CommandedMilitias.Contains(p)) continue; // Kendi birliğine saldırma
                        var pPos = CompatibilityLayer.GetPartyPosition(p);
                        float dist = currentPos != Vec2.Zero ? currentPos.Distance(pPos) : 0f;
                        if (dist > maxDistance) continue;

                        // Tehdit hesaplaması: Hedefin gücü vs Warlord'un toplam gücü
                        float targetTroops = p.MemberRoster.TotalManCount;
                        float ourTroops = Math.Max(1f, warlord.CommandedMilitias.Sum(m => m != null && m.IsActive ? m.MemberRoster.TotalManCount : 0));
                        float threat = Math.Min(1f, targetTroops / (ourTroops * 1.5f));
                        
                        float loot = p.IsCaravan ? 0.8f : (p.LeaderHero != null ? 0.6f : 0.4f); 

                        string id = "P_" + p.StringId;
                        targets[id] = p;
                        nodes.Add(new StrategicNode(
                            id, loot, threat, dist, 
                            p.Party.Culture?.StringId ?? "unknown", 
                            dist < 20f ? 1.0f : 0.0f, TargetType.MobileParty));
                    }
                }

                if(nodes.Any())
                {
                    var ctx = new WarlordContext(warlord.AssignedHideout?.Culture?.StringId ?? "unknown");
                    string bestTargetId = _pathfinder.FindOptimalTarget(
                        warlord.AssignedHideout?.StringId ?? "spawn", 
                        nodes, 
                        ctx);
                    
                    if(targets.TryGetValue(bestTargetId, out var targetObj))
                    {
                        if (targetObj is Settlement settlement)
                        {
                            command.TargetLocation = CompatibilityLayer.GetSettlementPosition(settlement);
                        }
                        else if (targetObj is MobileParty party)
                        {
                            command.TargetParty = party;
                            command.TargetLocation = CompatibilityLayer.GetPartyPosition(party);
                        }
                    }
                }
            }
        }

        private void EvaluateStrategicOpportunities()
        {
            var warlords = WarlordSystem.Instance.GetAllWarlords().ToList();
            if (!warlords.Any()) return;

            UpdateEntanglements(warlords);
            _recentDecisions.Clear();

            foreach (var warlord in warlords)
            {
                if (!warlord.IsAlive) continue;

                var bestAction = SelectOptimalAction(warlord);
                bool wasExploration = false;

                if (MBRandom.RandomFloat < _explorationRate)
                {
                    bestAction = _baseUtilityScores.Keys.ElementAt(
                        MBRandom.RandomInt(_baseUtilityScores.Count));
                    wasExploration = true;
                }

                _recentDecisions[warlord.StringId] = bestAction;

                AIDecisionLogger.LogDecision(
                    warlord.StringId,
                    warlord.Personality.ToString(),
                    warlord.Gold,
                    bestAction.ToString(),
                    _baseUtilityScores.TryGetValue(bestAction, out float s) ? s : 0f,
                    _baseUtilityScores.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value),
                    wasExploration,
                    _explorationRate);

                var command = CreateCommand(bestAction, warlord);
                ResolveCommandTarget(command, warlord);
                IssueCommandToWarlord(warlord, command);
            }
        }

        private CommandType SelectOptimalAction(Warlord warlord)
        {

            var scores = new Dictionary<CommandType, float>();

            var ctx = BuildStrategicContext(warlord);

            foreach (var action in _baseUtilityScores.Keys)
            {
                float score = CalculateUtilityScore(action, warlord, ctx);
                scores[action] = score;
            }

            bool appliedCollapse = false;
            float[] aiActionScores = new float[Enum.GetNames(typeof(AIAction)).Length];
            foreach (var action in _baseUtilityScores.Keys)
            {
                aiActionScores[(int)AILearningSystem.MapCommandType(action)] = scores[action];
            }

            foreach (var kvp in _recentDecisions)
            {
                string deciderId = kvp.Key;
                AIAction decidedAiAction = AILearningSystem.MapCommandType(kvp.Value);
                aiActionScores = _coalition.CollapsePartnerProbabilities(deciderId, warlord.StringId, decidedAiAction, aiActionScores);
                appliedCollapse = true;
            }

            if (appliedCollapse)
            {
                foreach (var action in _baseUtilityScores.Keys)
                {
                    scores[action] = aiActionScores[(int)AILearningSystem.MapCommandType(action)];
                }
            }

            if (Settings.Instance?.TestingMode == true)
            {
                var sortedScores = scores.OrderByDescending(kvp => kvp.Value).ToList();
                var sb = new System.Text.StringBuilder();
                _ = sb.AppendLine($"[AI Brain] Warlord {warlord.StringId} | Personality: {warlord.Personality} | Gold: {warlord.Gold}");
                _ = sb.AppendLine("  Rank | Command            | Score");
                _ = sb.AppendLine("  -----|--------------------|---------");
                for (int i = 0; i < sortedScores.Count; i++)
                {
                    var entry = sortedScores[i];
                    string marker = i == 0 ? " <<<" : "";
                    _ = sb.AppendLine($"  #{i + 1,-4}| {entry.Key,-19}| {entry.Value:F3}{marker}");
                }
                TaleWorlds.Library.Debug.Print(sb.ToString());
            }

            return scores.OrderByDescending(kvp => kvp.Value).First().Key;
        }

        private StrategicContext BuildStrategicContext(Warlord warlord)
        {
            float averageRegionFear = 0f;
            if (ModuleAccess.TryGetEnabled<BanditMilitias.Systems.Fear.FearSystem>(out var fearSystem))
            {
                averageRegionFear = fearSystem.GetAverageFearForWarlord(warlord.StringId);
            }

            LegitimacyLevel warlordLevel = LegitimacyLevel.Outlaw;
            if (ModuleAccess.TryGetEnabled<WarlordLegitimacySystem>(out var legitimacySystem))
            {
                warlordLevel = legitimacySystem.GetLevel(warlord.StringId);
            }

            int warlordBounty = 0;
            bool hasActiveHunter = false;
            if (ModuleAccess.TryGetEnabled<BountySystem>(out var bountySystem))
            {
                warlordBounty = bountySystem.GetBounty(warlord.StringId);
                hasActiveHunter = bountySystem.HasHunterParty(warlord.StringId);
            }

            var ctx = new StrategicContext
            {
                OwnCombatPower = CalculateWarlordPower(warlord),
                EnemyCombatPower = CalculateEnemyPower(warlord),
                WarlordGold = warlord.Gold,
                ThreatLevel = (warlord.AssignedHideout != null && _threatMap.TryGetValue(warlord.AssignedHideout, out var t)) ? t.OverallThreat : 0f,
                AverageRegionFear = averageRegionFear,
                WarlordLevel = warlordLevel,
                WarlordBounty = warlordBounty,
                HasActiveHunter = hasActiveHunter
            };

            return ctx;
        }

        private static float CalculateWarlordPower(Warlord warlord)
        {
            float totalPower = 0f;

            if (warlord.CommandedMilitias == null) return 50f;

            foreach (var militia in warlord.CommandedMilitias)
            {
                if (militia?.MemberRoster == null || !militia.IsActive) continue;

                for (int i = 0; i < militia.MemberRoster.Count; i++)
                {
                    var element = militia.MemberRoster.GetElementCopyAtIndex(i);
                    if (element.Character != null && element.Number > 0)
                    {

                        totalPower += element.Number * (element.Character.Tier * 10f);
                    }
                }
            }

            return MathF.Max(50f, totalPower);
        }

        private float CalculateEnemyPower(Warlord warlord)
        {

            float threatPower = 0f;
            if (warlord.AssignedHideout != null && _threatMap.TryGetValue(warlord.AssignedHideout, out var threat))
            {

                threatPower = threat.StrengthThreat * 300f;
            }

            float playerPower = 0f;
            var mainParty = MobileParty.MainParty;
            if (mainParty?.MemberRoster != null)
            {
                for (int i = 0; i < mainParty.MemberRoster.Count; i++)
                {
                    var el = mainParty.MemberRoster.GetElementCopyAtIndex(i);
                    if (el.Character != null && el.Number > 0)
                        playerPower += el.Number * (el.Character.Tier * 10f);
                }
            }

            return MathF.Max(80f, MathF.Max(threatPower, playerPower));
        }

        private float CalculateUtilityScore(CommandType action, Warlord warlord, StrategicContext ctx)
        {

            string cacheKey = $"{action}_{warlord.StringId}_{CampaignTime.Now.ToHours:F0}";
            if (_computationCache.TryGet(cacheKey, out float cachedScore))
            {
                return cachedScore;
            }

            float baseScore = _baseUtilityScores[action];
            float score = baseScore;

            float personalityMul = 1f;
            if (_strategies.TryGetValue(warlord.Personality, out var strategy))
            {
                personalityMul = strategy.GetActionAffinity(action);
                score *= personalityMul;
            }

            float successMul = 1f;
            if (_actionSuccessRates.TryGetValue(action, out var successRate))
            {
                successMul = 0.5f + successRate.Value * 0.5f;
                score *= successMul;
            }

            float threatMul = 1f;
            if (warlord.AssignedHideout != null &&
                _threatMap.TryGetValue(warlord.AssignedHideout, out var threat))
            {
                threatMul = GetThreatMultiplier(action, threat);
                score *= threatMul;
            }

            float resourceMul = 0.7f + Math.Min(1.0f, warlord.Gold / 5000f) * 0.3f;
            score *= resourceMul;

            float legitimacyMul = 1.0f;
            if (ctx.WarlordLevel <= LegitimacyLevel.Rebel)
            {
                if (action == CommandType.CommandBuildRepute)
                    legitimacyMul = 1.4f;
            }
            else if (ctx.WarlordLevel >= LegitimacyLevel.Recognized)
            {
                if (action == CommandType.Engage || action == CommandType.Defend)
                    legitimacyMul = 1.3f;
            }
            score *= legitimacyMul;

            float pidMul = 1f;
            float pidOutput = _responseController.Output;
            if (action == CommandType.Hunt || action == CommandType.Ambush)
            {
                pidMul = 1.0f + pidOutput * 0.3f;
                score *= pidMul;
            }

            float integrationMul = 1f;
            if (action == CommandType.CommandRaidVillage)
            {
                integrationMul = CalculateRaidVillageScore(ctx, warlord.StringId, ctx.AverageRegionFear);
                // NEW: King level reduced interest in small raids
                if (ctx.WarlordLevel == LegitimacyLevel.Recognized) integrationMul *= 0.2f;
            }
            else if (action == CommandType.CommandLayLow)
                integrationMul = CalculateLayLowScore(ctx, warlord.StringId);
            else if (action == CommandType.CommandExtort)
                integrationMul = CalculateExtortScore(ctx, warlord.StringId, ctx.AverageRegionFear);
            else if (action == CommandType.CommandBuildRepute)
                integrationMul = CalculateBuildReputeScore(ctx, warlord.StringId);
            else if (action == CommandType.Scavenge)
                integrationMul = CalculateScavengeScore(ctx, warlord.StringId);
            else if (action == CommandType.Retrieve)
                integrationMul = CalculateRetrieveScore(ctx, warlord.StringId);
            else if (action == CommandType.Hunt && warlord.IsLordHunting)
            {
                // NEW: Lord hunting bonus
                integrationMul = 2.5f;
            }
            score *= integrationMul;

            _computationCache.Set(cacheKey, score);

            float finalScore = Math.Max(0f, score);

            AIDecisionLogger.LogUtilityBreakdown(
                warlord.StringId, action.ToString(),
                baseScore, personalityMul, successMul,
                threatMul, resourceMul, pidMul,
                integrationMul, finalScore);

            if (Settings.Instance?.TestingMode == true)
            {
                TaleWorlds.Library.Debug.Print(
                    $"[AI Explainer] {warlord.StringId} -> {action}: " +
                    $"Base={baseScore:F2} x Personality={personalityMul:F2} x Success={successMul:F2} " +
                    $"x Threat={threatMul:F2} x Resource={resourceMul:F2} = {finalScore:F3}");
            }

            return finalScore;
        }

        private static float GetThreatMultiplier(CommandType action, ThreatAssessment threat)
        {

            float threatLevel = threat.OverallThreat;

            return action switch
            {
                CommandType.Defend when threatLevel > 0.7f => 2.0f,
                CommandType.Retreat when threatLevel > 0.8f => 1.5f,
                CommandType.Ambush when threatLevel < 0.3f => 1.5f,
                CommandType.Hunt when threatLevel > 0.6f => 1.3f,
                _ => 1.0f
            };
        }

        private void ProcessCoordinations()
        {
            _planner.Update();

            var readyStrikes = _planner.GetReadyCoordinations();
            foreach (var strike in readyStrikes)
            {
                ExecuteCoordinatedStrike(strike);
            }
        }

        private void ExecuteCoordinatedStrike(CoordinatedStrike strike)
        {
            if (strike == null || !strike.IsValid()) return;

            var command = CreateCommand(strike.Mission, null);
            command.TargetLocation = strike.TargetLocation;
            command.Priority = 1.0f;
            command.Reason = "COORDINATED STRIKE";

            foreach (var militia in strike.ParticipatingMilitias)
            {
                if (militia == null || !militia.IsActive) continue;

                PublishStrategicCommand(command, militia);
            }

            _successfulCoordinations++;

            if (Settings.Instance?.TestingMode == true)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[Brain] Coordinated strike executed: {strike.ParticipatingMilitias.Count} militias",
                    Colors.Green));
            }
        }

        public void TriggerCoordinatedStrike(Warlord warlord, Vec2 target)
        {
            if (warlord == null || warlord.CommandedMilitias.Count < 2) return;

            var strike = new CoordinatedStrike
            {
                RallyPoint = warlord.AssignedHideout != null
                    ? CompatibilityLayer.GetSettlementPosition(warlord.AssignedHideout)
                    : Vec2.Invalid,
                ScheduledTime = CampaignTime.Now + CampaignTime.Hours(2),
                ParticipatingMilitias = new List<MobileParty>(warlord.CommandedMilitias),
                Mission = CommandType.Ambush,
                TargetLocation = target
            };

            _planner.AddCoordination(strike);
        }

        private int _lastProcessedCombatIndex = 0;

        private void UpdateActionSuccessRates()
        {

            var historyList = _combatHistory.ToList();

            for (int i = _lastProcessedCombatIndex; i < historyList.Count; i++)
            {
                var outcome = historyList[i];

                if (outcome.PlayerWon)
                {

                    AdjustSuccessRate(CommandType.Defend, -0.05f);
                    AdjustSuccessRate(CommandType.Retreat, -0.03f);
                }
                else
                {

                    AdjustSuccessRate(CommandType.Ambush, 0.05f);
                    AdjustSuccessRate(CommandType.Hunt, 0.03f);
                }
            }

            _lastProcessedCombatIndex = historyList.Count;

            if (_lastProcessedCombatIndex >= 50) _lastProcessedCombatIndex = 0;
        }

        private void AdjustSuccessRate(CommandType action, float outcome)
        {
            if (!_actionSuccessRates.TryGetValue(action, out var rate))
            {
                rate = new RunningAverage(windowSize: 20);
                _actionSuccessRates[action] = rate;
            }

            rate.Add(outcome);
        }

        public void UpdatePlayerProfile()
        {
            if (Hero.MainHero?.PartyBelongedTo == null) return;

            _playerProfile.CurrentStrength = Hero.MainHero.PartyBelongedTo.MemberRoster.TotalManCount;
            _playerProfile.ClanTier = Hero.MainHero.Clan?.Tier ?? 0;
            _playerProfile.DaysPlayed++;

            _playerProfile.PlayStyle = ClassifyPlayStyle();

            if (_combatHistory.Count >= 5)
            {
                float winRate = _combatHistory.Count(o => o.PlayerWon) / (float)_combatHistory.Count;
                _playerProfile.CombatEffectiveness = winRate;
            }
        }

        private PlayStyle ClassifyPlayStyle()
        {

            float killRate = _playerProfile.TotalKills / Math.Max(1f, _playerProfile.DaysPlayed);

            if (killRate > 5f) return PlayStyle.Aggressive;
            if (killRate > 2f) return PlayStyle.Balanced;
            if (_playerProfile.HideoutsDestroyed > 3) return PlayStyle.Strategic;
            return PlayStyle.Passive;
        }

        private ThreatAssessment AssessThreatFuzzy(Settlement? hideout)
        {
            if (hideout == null) return new ThreatAssessment();
            var assessment = new ThreatAssessment { Settlement = hideout };

            float distance = GetPlayerDistance(hideout);
            assessment.ProximityThreat = FuzzyMembership(distance,
                nearThreshold: 20f,
                farThreshold: 50f,
                inverse: true);

            float playerStrength = GetPlayerStrength();
            float localStrength = GetLocalMilitiaStrength(hideout);
            float strengthRatio = playerStrength / Math.Max(1f, localStrength);
            assessment.StrengthThreat = FuzzyMembership(strengthRatio,
                nearThreshold: 0.5f,
                farThreshold: 2.0f,
                inverse: false);

            float timeSinceLastKill = _playerProfile.LastKillTime != CampaignTime.Zero
                ? (float)(CampaignTime.Now - _playerProfile.LastKillTime).ToHours
                : 999f;
            assessment.AggressionThreat = FuzzyMembership(timeSinceLastKill,
                nearThreshold: 2f,
                farThreshold: 24f,
                inverse: true);

            assessment.OverallThreat =
                assessment.ProximityThreat * 0.4f +
                assessment.StrengthThreat * 0.4f +
                assessment.AggressionThreat * 0.2f;

            return assessment;
        }

        private static float FuzzyMembership(float value, float nearThreshold, float farThreshold, bool inverse)
        {

            if (value <= nearThreshold)
                return inverse ? 1.0f : 0.0f;
            if (value >= farThreshold)
                return inverse ? 0.0f : 1.0f;

            float membership = (value - nearThreshold) / (farThreshold - nearThreshold);
            return inverse ? (1.0f - membership) : membership;
        }

        private StrategicCommand CreateCommand(CommandType type, Warlord? warlord)
        {
            return new StrategicCommand
            {
                Type = type,
                Priority = _baseUtilityScores.TryGetValue(type, out float priority) ? priority : 0.5f,
                Reason = warlord != null ? $"{warlord.Name}: {type}" : $"Brain: {type}",
                TargetLocation = Vec2.Invalid
            };
        }

        private void IssueCommandToWarlord(Warlord warlord, StrategicCommand command)
        {
            if (warlord == null || command == null) return;

            foreach (var militia in warlord.CommandedMilitias)
            {
                if (militia == null || !militia.IsActive) continue;
                PublishStrategicCommand(command, militia);
            }

            _totalDecisionsMade++;
        }

        private void PublishStrategicCommand(StrategicCommand command, MobileParty? targetParty = null)
        {
            var evt = EventBus.Instance.Get<StrategicCommandEvent>();
            // Each dispatch gets its own immutable snapshot so event consumers,
            // telemetry, and future memory layers do not share mutable command state.
            evt.Command = CloneCommand(command);
            evt.TargetParty = targetParty;
            evt.IssuedBy = "BanditBrain";
            evt.Timestamp = CampaignTime.Now;

            EventBus.Instance.Publish(evt);
            EventBus.Instance.Return(evt);
        }

        private static StrategicCommand CloneCommand(StrategicCommand command)
        {
            return new StrategicCommand
            {
                Type = command.Type,
                TargetLocation = command.TargetLocation,
                TargetParty = command.TargetParty,
                Priority = command.Priority,
                Reason = command.Reason
            };
        }

        private void IssueAdaptiveCommands()
        {

            float urgency = Math.Abs(_responseController.Output);

            if (urgency > 0.7f)
            {

                BroadcastEmergencyResponse();
            }
        }

        private void BroadcastEmergencyResponse()
        {
            var command = CreateCommand(CommandType.Defend, null);
            command.Priority = 1.0f;
            command.Reason = "EMERGENCY: High player threat detected";

            PublishStrategicCommand(command);
        }

        private void BroadcastDistressSignal(Settlement hideout, DistressLevel level)
        {

            var warlords = WarlordSystem.Instance.GetAllWarlords()
                .Where(w => w.IsAlive && w.AssignedHideout != null &&
                            w.AssignedHideout.GatePosition.DistanceSquared(hideout.GatePosition) < 100f * 100f)
                .ToList();

            foreach (var warlord in warlords)
            {
                var command = CreateCommand(CommandType.Hunt, warlord);
                command.TargetLocation = CompatibilityLayer.GetSettlementPosition(hideout);
                command.Priority = level == DistressLevel.Critical ? 1.0f : 0.7f;
                command.Reason = $"DISTRESS: {hideout.Name} destroyed!";

                IssueCommandToWarlord(warlord, command);
            }
        }

        private int _lastThreatUpdateIndex = 0;

        private void UpdateThreatAssessments()
        {
            var hideouts = StaticDataCache.Instance.AllHideouts
                .Where(s => s != null && s.IsActive)
                .ToList();
            if (hideouts.Count == 0) return;

            int maxUpdatesPerTick = 5;
            int updated = 0;

            while (updated < maxUpdatesPerTick)
            {
                if (_lastThreatUpdateIndex >= hideouts.Count)
                    _lastThreatUpdateIndex = 0;

                var hideout = hideouts[_lastThreatUpdateIndex];
                var assessment = AssessThreatFuzzy(hideout);
                _threatMap[hideout] = assessment;

                _lastThreatUpdateIndex++;
                updated++;
            }
        }

        private void OptimizeThreatResponse()
        {

            float avgThreat = _threatMap.Values.Select(t => t.OverallThreat).DefaultIfEmpty(0f).Average();
            _responseController.Setpoint = avgThreat > 0.6f ? 0.8f : 0.7f;
        }

        private void PruneStaleData()
        {

            var staleHideouts = _threatMap.Keys
                .Where(h => h == null || !h.IsActive)
                .ToList();

            foreach (var hideout in staleHideouts)
            {
                _ = _threatMap.Remove(hideout);
            }

            var staleMetrics = _militiaMetrics
                .Where(kvp => (CampaignTime.Now - kvp.Value.LastDeathTime).ToDays > 30)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in staleMetrics)
            {
                _ = _militiaMetrics.Remove(id);
            }

            if (_totalDecisionsMade > 1000)
            {
                var keys = _baseUtilityScores.Keys.ToList();
                foreach (var action in keys)
                {
                    float currentQ = _baseUtilityScores[action];

                    _baseUtilityScores[action] = currentQ + (0.5f - currentQ) * 0.01f;
                }
            }

            if (_totalDecisionsMade > 5000)
            {
                _totalDecisionsMade = (int)(_totalDecisionsMade * 0.90f);
            }
        }

        private static float GetPlayerStrength()
        {
            if (Hero.MainHero?.PartyBelongedTo == null) return 0f;

            var party = Hero.MainHero.PartyBelongedTo;
            float troopCount = party.MemberRoster.TotalManCount;
            float avgLevel = party.MemberRoster.TotalManCount > 0
                ? party.MemberRoster.GetTroopRoster().Sum(t => t.Character.Level * t.Number) / troopCount
                : 0f;

            return MathF.Sqrt(troopCount * (1f + avgLevel / 100f));
        }

        private static float GetPartyStrength(MobileParty? party)
        {
            if (party == null) return 0f;

            float troopCount = party.MemberRoster.TotalManCount;
            return MathF.Sqrt(troopCount);
        }

        private float GetLocalMilitiaStrength(Settlement hideout)
        {
            var warlord = WarlordSystem.Instance.GetWarlordForHideout(hideout);
            if (warlord == null) return 0f;

            float total = 0f;
            foreach (var militia in warlord.CommandedMilitias)
            {
                if (militia != null && militia.IsActive)
                {
                    total += GetPartyStrength(militia);
                }
            }

            return total;
        }

        private static float GetPlayerDistance(Settlement hideout)
        {
            if (Hero.MainHero?.PartyBelongedTo == null || hideout == null) return 999f;

            var playerPos = CompatibilityLayer.GetPartyPosition(Hero.MainHero.PartyBelongedTo);
            var hideoutPos = CompatibilityLayer.GetSettlementPosition(hideout);

            return playerPos.Distance(hideoutPos);
        }

        private static float GetDistance(Settlement a, Settlement b)
        {
            if (a == null || b == null) return 999f;
            return CompatibilityLayer.GetSettlementPosition(a)
                .Distance(CompatibilityLayer.GetSettlementPosition(b));
        }

        private void HandleCriticalError(string context, Exception ex)
        {
            _currentState = BrainState.Degraded;

            DebugLogger.Error("BanditBrain",
                $"Critical error in {context}: {ex.Message}");

            if (Settings.Instance?.TestingMode == true)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    $"[BanditBrain] ERROR in {context} - Entering degraded mode",
                    Colors.Red));
            }

        }

        private void OnSystemFailure()
        {

            Cleanup();
            Initialize();
        }

        private void InitializePersonalityStrategies()
        {
            _strategies[PersonalityType.Aggressive] = new AggressiveStrategy();
            _strategies[PersonalityType.Cautious] = new CautiousStrategy();
            _strategies[PersonalityType.Cunning] = new CunningStrategy();
            _strategies[PersonalityType.Vengeful] = new VengefulStrategy();
        }

        private void InitializeActionSuccessTrackers()
        {
            foreach (var action in _baseUtilityScores.Keys)
            {
                _actionSuccessRates[action] = new RunningAverage(windowSize: 20);
            }
        }

        public override string GetDiagnostics()
        {
            string stats = "BanditBrain:\n" +
                   $"  State: {_currentState}\n" +
                   $"  Decisions Made: {_totalDecisionsMade}\n" +
                   $"  Avg Decision Time: {_averageDecisionTime:F2}ms\n" +
                   $"  Coordinations: {_successfulCoordinations}? / {_failedCoordinations}?\n" +
                   $"  Threat Map Size: {_threatMap.Count}\n" +
                   $"  Player Threat: {_playerProfile.GetCurrentThreat():F2}\n" +
                   $"  Exploration Rate: {_explorationRate:P0}\n" +
                   $"  PID Output: {_responseController.Output:F2}\n" +
                   $"  Commands Issued: {_totalCommandsIssued}\n" +
                   $"  Success Rate: {_overallSuccessRate:P0} ({_successfulCommands}/{_totalCommandsIssued})";

            var sb = new System.Text.StringBuilder();
            _ = sb.Append(stats);

            foreach (var kvp in _commandFeedback)
            {
                var warlordId = kvp.Key;
                var feedback = kvp.Value;
                _ = sb.AppendLine();
                _ = sb.Append($"  [{warlordId}] Success: {feedback.SuccessRate:P0}, Conf: {feedback.StrategyConfidence:F2}");
            }

            return sb.ToString();
        }

        private int _saveVersion = 2;

        public override void SyncData(IDataStore dataStore)
        {
            if (dataStore.IsSaving)
            {
                _saveVersion = 2;
            }

            _ = dataStore.SyncData("_saveVersion", ref _saveVersion);

            if (dataStore.IsLoading && _saveVersion < 1)
            {

            }

            _ = dataStore.SyncData("_playerProfile", ref _playerProfile);
            _ = dataStore.SyncData("_totalDecisionsMade", ref _totalDecisionsMade);
            _ = dataStore.SyncData("_explorationRate", ref _explorationRate);

            _ = dataStore.SyncData("_actionSuccessRates", ref _actionSuccessRates);
            _ = dataStore.SyncData("_commandFeedback", ref _commandFeedback);
            _ = dataStore.SyncData("_militiaMetrics", ref _militiaMetrics);

            List<ThreatAssessment> threatSnapshot = new List<ThreatAssessment>();
            if (dataStore.IsSaving)
            {
                foreach (var kvp in _threatMap)
                {
                    var settlement = kvp.Key;
                    var value = kvp.Value;
                    if (settlement == null || value == null) continue;

                    threatSnapshot.Add(new ThreatAssessment
                    {
                        Settlement = settlement,
                        ProximityThreat = value.ProximityThreat,
                        StrengthThreat = value.StrengthThreat,
                        AggressionThreat = value.AggressionThreat,
                        OverallThreat = value.OverallThreat
                    });
                }
            }
            _ = dataStore.SyncData("_threatMapSnapshot", ref threatSnapshot);

            List<CombatOutcome> historyList = new List<CombatOutcome>();
            if (dataStore.IsSaving)
            {
                historyList = _combatHistory.ToList();
            }

            _ = dataStore.SyncData("_combatHistory", ref historyList);

            if (dataStore.IsLoading)
            {
                _threatMap.Clear();
                if (threatSnapshot != null)
                {
                    foreach (var item in threatSnapshot)
                    {
                        if (item == null) continue;
                        var settlement = item.Settlement;
                        if (settlement == null || !settlement.IsActive) continue;
                        _threatMap[settlement] = item;
                    }
                }

                _combatHistory.Clear();
                if (historyList != null)
                {
                    foreach (var item in historyList)
                    {
                        _combatHistory.Add(item);
                    }
                }

                if (_backupActionSuccessRates != null)
                {
                    foreach (var kvp in _backupActionSuccessRates)
                    {
                        if (!_actionSuccessRates.ContainsKey(kvp.Key))
                            _actionSuccessRates[kvp.Key] = kvp.Value;
                    }
                }

                if (_backupCommandFeedback != null)
                {
                    foreach (var kvp in _backupCommandFeedback)
                    {
                        if (kvp.Key != null && !_commandFeedback.ContainsKey(kvp.Key))
                            _commandFeedback[kvp.Key] = kvp.Value;
                    }
                }

                if (_explorationRate <= 0f)
                    _explorationRate = _backupExplorationRate > 0f ? _backupExplorationRate : 0.2f;

                if (_totalDecisionsMade == 0 && _backupTotalDecisions > 0)
                    _totalDecisionsMade = _backupTotalDecisions;

                _backupActionSuccessRates = null;
                _backupCommandFeedback = null;

                if (_currentState == BrainState.Dormant)
                {
                    Initialize();
                }
            }
        }
    }
    // ── FearBountyIntegration (inline) ─────────────────────────────────

    public sealed partial class BanditBrain
    {

        private float CalculateRaidVillageScore(
            StrategicContext ctx,
            string warlordId,
            float averageNearbyFear)
        {
            float score = 0f;

            float powerFactor = MathF.Clamp(ctx.OwnCombatPower / 50f, 0f, 1f);
            score += powerFactor * 0.35f;

            float goldNeed = MathF.Clamp(1f - (ctx.WarlordGold / 5000f), 0f, 1f);
            score += goldNeed * 0.25f;

            float fearOpportunity = averageNearbyFear < 0.30f ? 0.20f : 0.05f;
            score += fearOpportunity;

            if (ModuleAccess.IsEnabled<BountySystem>())
            {
                float bountyPenalty = MathF.Clamp(ctx.WarlordBounty / 10000f, 0f, 0.40f);
                score -= bountyPenalty;
            }

            if (ModuleAccess.IsEnabled<WarlordLegitimacySystem>())
            {
                if (ctx.WarlordLevel >= LegitimacyLevel.Recognized)
                    score -= 0.15f;
            }

            return MathF.Clamp(score, 0f, 1f);
        }

        private float CalculateLayLowScore(
            StrategicContext ctx,
            string warlordId)
        {
            float score = 0f;

            if (!ModuleAccess.IsEnabled<BountySystem>())
                return 0f;

            float bountyFactor = MathF.Clamp(ctx.WarlordBounty / 8000f, 0f, 0.50f);
            score += bountyFactor;

            if (ctx.HasActiveHunter) score += 0.30f;

            if (ctx.EnemyCombatPower > 0)
            {
                float weakness = MathF.Clamp(1f - (ctx.OwnCombatPower / ctx.EnemyCombatPower), 0f, 0.20f);
                score += weakness;
            }

            float goldSafety = MathF.Clamp(ctx.WarlordGold / 10000f, 0f, 0.15f);
            score += goldSafety;

            return MathF.Clamp(score, 0f, 1f);
        }

        private float CalculateExtortScore(
            StrategicContext ctx,
            string warlordId,
            float averageNearbyFear)
        {
            float score = 0f;

            if (!ModuleAccess.IsEnabled<FearSystem>())
                return 0f;

            if (averageNearbyFear < 0.40f)
            {
                float fearGap = 0.55f - averageNearbyFear;
                score += fearGap * 0.60f;
            }
            else if (averageNearbyFear > 0.70f)
            {

                score -= 0.30f;
            }

            float powerBonus = MathF.Clamp(ctx.OwnCombatPower / 80f, 0f, 0.20f);
            score += powerBonus;

            if (ModuleAccess.IsEnabled<WarlordLegitimacySystem>())
            {
                float legBonus = (int)ctx.WarlordLevel * 0.05f;
                score += legBonus;
            }

            return MathF.Clamp(score, 0f, 1f);
        }

        private float CalculateBuildReputeScore(
            StrategicContext ctx,
            string warlordId)
        {
            float score = 0f;

            if (!ModuleAccess.TryGetEnabled<WarlordLegitimacySystem>(out var legitimacySystem))
                return 0f;

            float progress = legitimacySystem.GetProgressToNextLevel(warlordId);
            if (progress > 0.70f)
                score += (progress - 0.70f) * 1.5f;

            if (ctx.WarlordLevel == LegitimacyLevel.Warlord)
                return 0f;

            if (ModuleAccess.IsEnabled<BountySystem>())
            {
                float lowBountyBonus = MathF.Clamp(1f - (ctx.WarlordBounty / 5000f), 0f, 0.20f);
                score += lowBountyBonus;
            }

            float goldAvailable = MathF.Clamp(ctx.WarlordGold / 8000f, 0f, 0.15f);
            score += goldAvailable;

            return MathF.Clamp(score, 0f, 1f);
        }

        private static float CalculateNewCommandReward(
            CommandType command,
            bool wasSuccessful,
            float goldGained,
            bool leveledUp)
        {
            return command switch
            {

                CommandType.CommandRaidVillage when wasSuccessful =>
                    MathF.Min(0.8f, goldGained / 1000f),
                CommandType.CommandRaidVillage => -0.25f,

                CommandType.CommandLayLow when wasSuccessful => 0.60f,
                CommandType.CommandLayLow => -0.10f,

                CommandType.CommandExtort when wasSuccessful => 0.45f,
                CommandType.CommandExtort => -0.20f,

                CommandType.CommandBuildRepute when leveledUp => 1.00f,
                CommandType.CommandBuildRepute when wasSuccessful => 0.30f,
                CommandType.CommandBuildRepute => -0.05f,

                _ => 0f
            };
        }

        private float CalculateScavengeScore(StrategicContext ctx, string warlordId)
        {
            float score = 0f;

            float goldNeed = MathF.Clamp(1f - (ctx.WarlordGold / 3000f), 0f, 1f);
            score += goldNeed * 0.40f;

            float troopNeed = MathF.Clamp(1f - (ctx.OwnCombatPower / 100f), 0f, 1f);
            score += troopNeed * 0.30f;

            if (ctx.ThreatLevel < 0.4f)
            {
                score += (0.4f - ctx.ThreatLevel);
            }

            return MathF.Clamp(score, 0f, 1f);
        }

        private float CalculateRetrieveScore(StrategicContext ctx, string warlordId)
        {
            float score = 0f;

            float powerFactor = MathF.Clamp(ctx.OwnCombatPower / 150f, 0f, 1f);
            score += powerFactor * 0.35f;

            if (ModuleAccess.IsEnabled<WarlordLegitimacySystem>())
            {
                if (ctx.WarlordLevel >= LegitimacyLevel.Recognized)
                    score += 0.25f;
            }

            return MathF.Clamp(score, 0f, 1f);
        }
    }

}
