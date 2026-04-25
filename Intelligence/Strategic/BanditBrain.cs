using BanditMilitias.Components;
using BanditMilitias.Core.Events;
using BanditMilitias.Debug;
using BanditMilitias.Infrastructure;
using BanditMilitias.Intelligence.AI.Components;
using BanditMilitias.Intelligence.Logging;
using BanditMilitias.Intelligence.Neural;
using BanditMilitias.Systems.Bounty;
using BanditMilitias.Systems.Fear;
using BanditMilitias.Systems.Progression;
using BanditMilitias.Core.Neural;
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
        private readonly Dictionary<string, CommandType> _recentDecisions = new();

        private readonly Dictionary<PersonalityType, IPersonalityStrategy> _strategies = new();

        private readonly BanditMilitias.Infrastructure.PIDController _responseController = new(
            kp: 0.5f, ki: 0.1f, kd: 0.2f, setpoint: 0.7f);


        private Dictionary<CommandType, RunningAverage> _actionSuccessRates = new();

        private readonly LRUCache<string, float> _computationCache = new(100);

        private int _totalDecisionsMade = 0;
        private int _successfulCoordinations = 0;
        private int _failedCoordinations = 0;
        private float _averageDecisionTime = 0f;

        private Dictionary<CommandType, RunningAverage>? _backupActionSuccessRates;
        private Dictionary<string, CommandFeedback>? _backupCommandFeedback;
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
                    0f,
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
            bool madeConservative = false;

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
            // ── BACKSTORY DAVRANIŞI: Kişisel geçmişe göre komut önceliği ayarı ──
            // Güç Filtresi: Sadece belirli bir güç seviyesinin üzerindeyse risk al
            float warlordPower = CalculateWarlordPower(warlord);
            int troopCount = warlord.CommandedMilitias.Sum(p => p.MemberRoster.TotalManCount);
            bool isPowerfulEnough = warlordPower > 500f || troopCount > 40;

            try
            {
                switch (warlord.Backstory)
                {
                    case BackstoryType.BetrayedNoble:
                        // Lord partilerini avla — Sadece güçlüyse intikam alabilir
                        if (isPowerfulEnough && (command.Type == CommandType.Ambush || command.Type == CommandType.Patrol))
                        {
                            command.Priority *= 1.4f;
                            command.Reason += " [Backstory: İhanet edilmiş soylu — lordları avla]";
                        }
                        else if (!isPowerfulEnough && command.Type == CommandType.Hunt)
                        {
                            command.Type = CommandType.Defend; // Zayıfsa savunmaya çekil
                            command.Reason += " [Backstory: İhanet edilmiş soylu — henüz çok zayıf]";
                        }
                        break;

                    case BackstoryType.VengefulSurvivor:
                        // İntikamcı — Saldırı odaklı ama intihar değil
                        if (isPowerfulEnough && (command.Type == CommandType.Hunt || command.Type == CommandType.Ambush))
                        {
                            command.Priority *= 1.25f;
                            command.Reason += " [Backstory: İntikamcı hayatta kalan]";
                        }
                        break;

                    case BackstoryType.FailedMercenary:
                        // Ekonomik hedefler önce — Altın hırsı riskleri artırır
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
                        // Sürekli büyüme — Güce bakmaksızın aktif kalmaya çalışır
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

            if (confidence < 0.5f)
            {
                bool isHighLevel = level >= LegitimacyLevel.Warlord;
                // 4. Rapor Revize: Düşük seviyeli warlordlar için "Aşırı Muhafazakar" kilidini gevşet
                // Onların büyümek için risk alması gerekiyor. Sadece Ölümcül tehditte (0.95) kaçarlar.
                if ((isHighLevel && threat.OverallThreat > 0.85f) || threat.OverallThreat > 0.95f)
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
                madeConservative ? $"Low success rate ({confidence:F2}) -> Conservative" : null);

            var assessment = CreateStrategicAssessment(warlord);
            var assessmentEvt = EventBus.Instance.Get<StrategicAssessmentEvent>();
            assessmentEvt.TargetWarlord = warlord;
            assessmentEvt.Assessment = assessment;
            NeuralEventRouter.Instance.Publish(assessmentEvt);
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
                    $"[Fleet+] {evt.Party.Name} doğdu. Aktif milis: {ModuleManager.Instance.GetMilitiaCount()}");
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
                    $"[Fleet-] {evt.Party.Name} dağıtıldı. {(evt.IsNuclearCleanup ? "(zorla)" : "")}");
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

        private void UpdateEntanglements(IReadOnlyList<Warlord> warlords)
        {
            // ML-based entanglement removed.
        }

        private void ResolveCommandTarget(StrategicCommand command, Warlord warlord)
        {
            if (command.Type == CommandType.Raid || command.Type == CommandType.Hunt || 
                command.Type == CommandType.Ambush || command.Type == CommandType.CommandRaidVillage ||
                command.Type == CommandType.Harass || command.Type == CommandType.Engage)
            {
                // ── KATMANLI ZEKA: 1. KATMAN - Gözlemci Verisi (Shared Intel) ──
                // Eğer sığınak ağında bir gözlemci varsa ve veri paylaşmışsa, önce ona bak.
                if (warlord.SharedIntel.Count > 0 && MBRandom.RandomFloat < 0.7f)
                {
                    var intelTarget = warlord.SharedIntel[MBRandom.RandomInt(warlord.SharedIntel.Count)];
                    if (intelTarget != null && intelTarget.IsActive)
                    {
                        command.TargetParty = intelTarget;
                        command.TargetLocation = CompatibilityLayer.GetPartyPosition(intelTarget);
                        command.Reason += " [Shared Intel]";
                        return;
                    }
                }

                // ── KATMANLI ZEKA: 2. KATMAN - Yerel Sensörler (Kendi Görüşü) ──
                var currentPos = warlord.AssignedHideout != null 
                    ? CompatibilityLayer.GetSettlementPosition(warlord.AssignedHideout) 
                    : Vec2.Zero;

                float maxDistance = 150f;
                
                // PERFORMANS OPTİMİZASYONU: Listeleri birleştirmek (Concat) yerine 
                // doğrudan BedrockLayer ve SpatialGrid üzerinden sorgu yap.
                
                // 1. Yerleşim Yerleri (Static Grid)
                var nearbySettlements = Core.Memory.WorldMemory.Bedrock.GetNearest(currentPos, 10, maxDistance);
                
                // 2. Partiler (Dynamic SpatialGrid)
                var nearbyParties = new List<MobileParty>();
                if (currentPos != Vec2.Zero)
                {
                    Systems.Grid.SpatialGridSystem.Instance.QueryNearby(currentPos, maxDistance, nearbyParties);
                }

                // Rastgele hedef seçimi
                if (nearbyParties.Count > 0 && MBRandom.RandomFloat < 0.5f)
                {
                    var target = nearbyParties[MBRandom.RandomInt(nearbyParties.Count)];
                    if (target != null && !warlord.CommandedMilitias.Contains(target))
                    {
                        command.TargetParty = target;
                        command.TargetLocation = CompatibilityLayer.GetPartyPosition(target);
                        return;
                    }
                }

                var targetSettlement = nearbySettlements.FirstOrDefault(s => s != warlord.AssignedHideout);
                if (targetSettlement != null)
                {
                    command.TargetLocation = CompatibilityLayer.GetSettlementPosition(targetSettlement);
                }
            }
        }

        private void EvaluateStrategicOpportunities()
        {
            // SharedPercept zaten bu tick'te güncellenmiş listeyi tutar —
            // GetAllWarlords().ToList() kopyası yerine doğrudan kullan
            var warlords = BanditMilitias.Core.Neural.SharedPercept.Current.AllWarlords;
            _recentDecisions.Clear();

            if (warlords.Count > 0)
            {
                UpdateEntanglements(warlords);

                foreach (var warlord in warlords)
                {
                    if (!warlord.IsAlive) continue;

                    // ── KOLEKTİF İSTİHBARAT: Gözlemci Verilerini Güncelle ──
                    UpdateWarlordIntelligence(warlord);

                    var bestAction = SelectOptimalAction(warlord);

                    _recentDecisions[warlord.StringId] = bestAction;

                    AIDecisionLogger.LogDecision(
                        warlord.StringId,
                        warlord.Personality.ToString(),
                        warlord.Gold,
                        bestAction.ToString(),
                        _baseUtilityScores.TryGetValue(bestAction, out float s) ? s : 0f,
                        _baseUtilityScores.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value));

                    var command = CreateCommand(bestAction, warlord);
                    ResolveCommandTarget(command, warlord);
                    IssueCommandToWarlord(warlord, command);
                }
            }

            EvaluateAutonomousMilitiaCommands();
        }

        /// <summary>
        /// Warlord ağındaki Gözlemci (Watcher) biriminin karmaşık tarama verilerini işler.
        /// </summary>
        private void UpdateWarlordIntelligence(Warlord warlord)
        {
            try
            {
                // 1. Halefiyet kontrolü (Gözlemci öldüyse yenisini ata)
                warlord.EnsureWatcherSuccession();

                if (string.IsNullOrEmpty(warlord.WatcherPartyId)) return;

                // 2. Gözlemciyi bul
                var watcher = warlord.CommandedMilitias.FirstOrDefault(p => p.StringId == warlord.WatcherPartyId && p.IsActive);
                if (watcher == null) return;

                // 3. Karmaşık Gözlem (Complex Observation)
                // Gözlemci normal birimlerden 3 kat daha geniş alanı (450f) tarar.
                float scanRadius = 450f;
                var detected = new List<MobileParty>();
                Systems.Grid.SpatialGridSystem.Instance.QueryNearby(CompatibilityLayer.GetPartyPosition(watcher), scanRadius, detected);

                // 4. Veriyi sığınak ağına (Shared Intel) aktar
                warlord.UpdateSharedIntelligence(detected);
            }
            catch (Exception ex)
            {
                DebugLogger.Error("BanditBrain", $"Intelligence update failed for {warlord.Name}: {ex.Message}");
            }
        }

        private CommandType SelectOptimalAction(Warlord warlord)
        {

            var scores = new Dictionary<CommandType, float>();

            var ctx = BuildStrategicContext(warlord);

            // ── Neural Danışman: Tier 3+ warlord'lar için tavsiye al ──
            NeuralAdvice neuralAdvice = default;
            CareerTier warlordCareerTier = CareerTier.Eskiya;
            try
            {
                var careerSystem = WarlordCareerSystem.Instance;
                warlordCareerTier = careerSystem?.GetTier(warlord.StringId) ?? CareerTier.Eskiya;

                var advisor = NeuralAdvisor.Instance;
                if (advisor != null && advisor.IsEnabled && (int)warlordCareerTier >= (int)CareerTier.Warlord)
                {
                    float[] features = NeuralAdvisor.ExtractFeatures(ctx, warlord);
                    neuralAdvice = advisor.GetStrategicAdvice(features, warlordCareerTier);

                    // Neural prediction logla
                    if (neuralAdvice.IsValid && Settings.Instance?.DevMode == true)
                    {
                        NeuralDataExporter.AppendPredictionLog(
                            warlord.StringId,
                            NeuralActionMap.IndexToCommand[neuralAdvice.RecommendedAction].ToString(),
                            neuralAdvice.Confidence,
                            neuralAdvice.ActionProbabilities);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("BanditBrain", $"Neural inference error: {ex.Message}");
            }

            foreach (var action in _baseUtilityScores.Keys)
            {
                float score = CalculateUtilityScore(action, warlord, ctx);

                // ── Neural Blending: heuristic + neural karıştır ──
                if (neuralAdvice.IsValid)
                {
                    int actionIdx = NeuralActionMap.CommandToIndex(action);
                    score = NeuralAdvisor.BlendWithHeuristic(score, neuralAdvice, actionIdx);
                }

                scores[action] = score;
            }

            if (Settings.Instance?.TestingMode == true)
            {
                var sortedScores = scores.OrderByDescending(kvp => kvp.Value).ToList();
                var sb = new System.Text.StringBuilder();
                _ = sb.AppendLine($"[AI Brain] Warlord {warlord.StringId} | Personality: {warlord.Personality} | Gold: {warlord.Gold}");
                string neuralTag = neuralAdvice.IsValid
                    ? $" | Neural: {NeuralActionMap.IndexToCommand[neuralAdvice.RecommendedAction]} (c={neuralAdvice.Confidence:F2})"
                    : " | Neural: OFF";
                _ = sb.AppendLine($"  Tier: {warlordCareerTier}{neuralTag}");
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
            // SharedPercept bu tick için fear indeksini zaten derledi — FearSystem.GetAverageFear çağrısından kaçın
            float averageRegionFear = 0f;
            var percept = BanditMilitias.Core.Neural.SharedPercept.Current;
            if (warlord.StringId != null &&
                percept.WarlordFearIndex.TryGetValue(warlord.StringId, out float cachedFear))
            {
                averageRegionFear = cachedFear;
            }
            else if (ModuleAccess.TryGetEnabled<BanditMilitias.Systems.Fear.FearSystem>(out var fearSystem))
            {
                averageRegionFear = fearSystem.GetAverageFearForWarlord(warlord.StringId!);
            }

            LegitimacyLevel warlordLevel = LegitimacyLevel.Outlaw;
            if (ModuleAccess.TryGetEnabled<WarlordLegitimacySystem>(out var legitimacySystem))
            {
                warlordLevel = legitimacySystem.GetLevel(warlord.StringId!);
            }

            int warlordBounty = 0;
            bool hasActiveHunter = false;
            if (ModuleAccess.TryGetEnabled<BountySystem>(out var bountySystem))
            {
                warlordBounty = bountySystem.GetBounty(warlord.StringId!);
                hasActiveHunter = bountySystem.HasHunterParty(warlord.StringId!);
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

            float normalizedGold = Math.Min(1.0f, warlord.Gold / 5000f);
            float scarcity = 1.0f - normalizedGold;
            float resourceMul = action switch
            {
                CommandType.CommandRaidVillage or CommandType.CommandExtort or CommandType.Scavenge or CommandType.Retrieve
                    => 1.05f + scarcity * 0.55f,
                CommandType.Hunt or CommandType.Engage or CommandType.Ambush
                    => 0.75f + normalizedGold * 0.45f,
                _ => 0.85f + normalizedGold * 0.30f
            };
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
            // CircularBuffer'ı her tick .ToList() ile kopyalamak yerine
            // sadece son tick'ten bu yana eklenen kayıt sayısına bak.
            int total = _combatHistory.Count;
            if (total <= _lastProcessedCombatIndex)
            {
                // Yeni kayıt yok
                return;
            }

            // Yeni kayıtlar: _lastProcessedCombatIndex'ten total'a kadar
            // CircularBuffer head-based erişim için direkt enumeration kullan
            int i = 0;
            foreach (var outcome in _combatHistory)
            {
                if (i++ < _lastProcessedCombatIndex) continue; // zaten işlendi

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

                RecordBattleExperience(outcome);
            }

            _lastProcessedCombatIndex = total;

            // Buffer dolup eski kayıtlar üzerine yazıldığında index'i sıfırla
            if (_lastProcessedCombatIndex >= _combatHistory.Capacity)
                _lastProcessedCombatIndex = 0;
        }

        /// <summary>
        /// Savaş sonucunu NeuralAdvisor'ın deneyim buffer'ına kaydet.
        /// </summary>
        private void RecordBattleExperience(CombatOutcome outcome)
        {
            try
            {
                var advisor = NeuralAdvisor.Instance;
                if (advisor == null || !advisor.IsEnabled) return;

                // Savaş anının tam bağlamını SharedPercept'ten al —
                // features[3..11] artık 0f değil, gerçek değerler taşıyor
                var percept = BanditMilitias.Core.Neural.SharedPercept.Current;

                float[] stateFeatures = new float[NeuralAdvisor.STRATEGIC_INPUT_SIZE];
                stateFeatures[0] = Normalize(outcome.MilitiaStrength, 0f, 5000f);
                stateFeatures[1] = Normalize(outcome.PlayerStrength, 0f, 5000f);
                stateFeatures[2] = outcome.PlayerStrength > 0
                    ? Math.Min(3f, outcome.MilitiaStrength / Math.Max(1f, outcome.PlayerStrength))
                    : 1f;
                stateFeatures[3] = Math.Min(1f, percept.ThreatLevel);           // Tehdit seviyesi
                stateFeatures[4] = 0f;                                           // avgFear — percept'te warlord bazlı yok
                stateFeatures[5] = 0.5f;                                         // gold — bilinmiyor, nötr
                stateFeatures[6] = Normalize(outcome.MilitiaStrength / 10f, 0f, 200f); // asker tahmini
                stateFeatures[7] = 0.5f;                                         // avgTier — nötr
                stateFeatures[8] = 0.5f;                                         // legitimacy — nötr
                stateFeatures[9] = 0f;                                           // hasHunter
                stateFeatures[10] = 0.2f;                                        // bounty — nötr
                stateFeatures[11] = percept.ThreatLevel > 0.5f ? 0.3f : 0.7f;  // playerDist — yüksek tehdit → oyuncu yakın

                int actionIdx = outcome.PlayerWon
                    ? NeuralActionMap.CommandToIndex(CommandType.Defend)
                    : NeuralActionMap.CommandToIndex(CommandType.Ambush);

                float casualtyRatio = 0.3f;
                float reward = RewardFunction.CalculateBattleReward(
                    !outcome.PlayerWon,
                    outcome.MilitiaStrength,
                    outcome.PlayerStrength,
                    casualtyRatio);

                advisor.RecordExperience(stateFeatures, actionIdx, reward, stateFeatures, "battle");
            }
            catch (Exception ex)
            {
                DebugLogger.Warning("BanditBrain", $"RecordBattleExperience failed: {ex.Message}");
            }
        }

        private static float Normalize(float value, float min, float max)
        {
            if (max <= min) return 0f;
            return Math.Max(0f, Math.Min(1f, (value - min) / (max - min)));
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

            NeuralEventRouter.Instance.Publish(evt);
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

            // GetAllWarlords() IsAlive filtreli — IsAlive filtresi gereksiz
            var warlords = WarlordSystem.Instance.GetAllWarlords()
                .Where(w => w.AssignedHideout != null &&
                            w.AssignedHideout.GatePosition.DistanceSquared(hideout.GatePosition) < 10000f)
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

        private void EvaluateAutonomousMilitiaCommands()
        {
            var militias = ModuleManager.Instance.ActiveMilitias;
            if (militias == null || militias.Count == 0)
                return;

            // FIX: Sabit 8 limit yerine, aktif milis sayısına göre dinamik bütçe.
            // Küçük gruplar (≤20 milis) → hepsi değerlendirilir.
            // Büyük gruplar → her saat bütçenin ~%40'ı işlenir (staggered kapsama).
            // Warlord'suz milisler için bu fonksiyon tek stratejik karar kaynağıdır.
            int autonomousCount = 0;
            foreach (var m in militias)
            {
                if (m?.IsActive == true && m.PartyComponent is MilitiaPartyComponent mc
                    && mc.AssignedWarlord == null && string.IsNullOrWhiteSpace(mc.WarlordId))
                    autonomousCount++;
            }

            int maxIssued = autonomousCount <= 20
                ? autonomousCount                                    // ≤20: hepsi
                : Math.Max(8, (int)(autonomousCount * 0.40f));      // >20: %40 bütçe

            int issued = 0;
            int currentHour = (int)CampaignTime.Now.ToHours;

            foreach (var militia in militias)
            {
                if (issued >= maxIssued)
                    break;
                if (militia == null || !militia.IsActive || militia.MapEvent != null)
                    continue;
                if (Math.Abs(militia.StringId.GetHashCode()) % 2 != currentHour % 2)
                    continue;
                if (militia.PartyComponent is not MilitiaPartyComponent comp)
                    continue;
                if (comp.AssignedWarlord != null || !string.IsNullOrWhiteSpace(comp.WarlordId))
                    continue;
                if (HasFreshAutonomousOrder(comp))
                    continue;

                StrategicCommand? command = CreateAutonomousCommand(militia, comp);
                if (command == null)
                    continue;

                PublishStrategicCommand(command, militia);
                _recentDecisions[militia.StringId] = command.Type;
                issued++;
            }
        }

        private static bool HasFreshAutonomousOrder(MilitiaPartyComponent comp)
        {
            if (comp.CurrentOrder == null)
                return false;

            double ageHours = (CampaignTime.Now - comp.OrderTimestamp).ToHours;
            if (ageHours >= 8.0)
                return false;

            return comp.CurrentOrder.Type != CommandType.Patrol || ageHours < 3.0;
        }

        private StrategicCommand? CreateAutonomousCommand(MobileParty militia, MilitiaPartyComponent comp)
        {
            Settlement? home = comp.GetHomeSettlement();
            if (home == null)
                return null;

            float myStrength = CompatibilityLayer.GetTotalStrength(militia);
            int troopCount = militia.MemberRoster?.TotalManCount ?? 0;
            int availableGold = Math.Max(0, comp.Gold);
            Vec2 myPos = CompatibilityLayer.GetPartyPosition(militia);

            MobileParty? closestThreat = null;
            float closestThreatStrength = 0f;
            Settlement? closestVillage = null;
            float closestVillageDistSq = float.MaxValue;

            var nearby = new List<MobileParty>();
            MilitiaSmartCache.Instance.GetNearbyParties(myPos, 26f, nearby);
            foreach (var party in nearby)
            {
                if (party == null || !party.IsActive || party == militia)
                    continue;
                if (party.MapFaction == null || militia.MapFaction == null || !party.MapFaction.IsAtWarWith(militia.MapFaction))
                    continue;

                float strength = CompatibilityLayer.GetTotalStrength(party);
                if (strength > closestThreatStrength)
                {
                    closestThreat = party;
                    closestThreatStrength = strength;
                }
            }

            foreach (var village in StaticDataCache.Instance.AllVillages)
            {
                if (village == null)
                    continue;

                float distSq = CompatibilityLayer.GetSettlementPosition(village).DistanceSquared(myPos);
                if (distSq < closestVillageDistSq)
                {
                    closestVillage = village;
                    closestVillageDistSq = distSq;
                }
            }

            if (closestThreat != null && myStrength >= closestThreatStrength * 1.15f && troopCount >= 18)
            {
                return new StrategicCommand
                {
                    Type = CommandType.Hunt,
                    TargetParty = closestThreat,
                    TargetLocation = CompatibilityLayer.GetPartyPosition(closestThreat),
                    Priority = 0.85f,
                    Reason = "Autonomous captain: local advantage"
                };
            }

            if (availableGold < 1000 && troopCount < 20 && closestVillage != null)
            {
                return new StrategicCommand
                {
                    Type = CommandType.CommandRaidVillage,
                    TargetLocation = CompatibilityLayer.GetSettlementPosition(closestVillage),
                    Priority = 0.9f,
                    Reason = "Autonomous survival raid"
                };
            }

            if (closestThreat != null && closestThreatStrength > myStrength * 1.35f)
            {
                return new StrategicCommand
                {
                    Type = CommandType.Defend,
                    TargetLocation = CompatibilityLayer.GetSettlementPosition(home),
                    Priority = 0.7f,
                    Reason = "Autonomous fallback to hideout"
                };
            }

            if (closestVillage != null && (comp.Role == MilitiaPartyComponent.MilitiaRole.Raider || availableGold < 2000))
            {
                return new StrategicCommand
                {
                    Type = CommandType.CommandRaidVillage,
                    TargetLocation = CompatibilityLayer.GetSettlementPosition(closestVillage),
                    Priority = 0.72f,
                    Reason = "Autonomous economic pressure"
                };
            }

            return new StrategicCommand
            {
                Type = CommandType.Patrol,
                TargetLocation = CompatibilityLayer.GetSettlementPosition(home),
                Priority = 0.45f,
                Reason = "Autonomous territorial patrol"
            };
        }

        private int _lastThreatUpdateIndex = 0;

        private void UpdateThreatAssessments()
        {
            // StaticDataCache.AllHideouts zaten önbellek — .Where().ToList() kopyası gereksiz.
            // Doğrudan index ile round-robin geziyoruz; IsActive kontrolü içeride yapılır.
            var allHideouts = StaticDataCache.Instance.AllHideouts;
            int total = allHideouts.Count;
            if (total == 0) return;

            int maxUpdatesPerTick = 5;
            int updated = 0;
            int scanned = 0;

            while (updated < maxUpdatesPerTick && scanned < total)
            {
                if (_lastThreatUpdateIndex >= total)
                    _lastThreatUpdateIndex = 0;

                var hideout = allHideouts[_lastThreatUpdateIndex];
                _lastThreatUpdateIndex++;
                scanned++;

                if (hideout == null || !hideout.IsActive) continue;

                _threatMap[hideout] = AssessThreatFuzzy(hideout);
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

            // 4. Rapor Revize: Fakir warlordlar haraç kesmeye daha meyillidir
            if (ctx.WarlordGold < 2000) score += 0.35f;

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

            // 4. Rapor Revize: Scavenge (Yağma/Leşçilik) düşük seviyede ana geçim kaynağıdır
            float goldNeed = MathF.Clamp(1f - (ctx.WarlordGold / 5000f), 0f, 1f);
            score += goldNeed * 0.60f; // Önceliği artırıldı

            float troopNeed = MathF.Clamp(1f - (ctx.OwnCombatPower / 150f), 0f, 1f);
            score += troopNeed * 0.40f;

            if (ctx.ThreatLevel < 0.5f)
            {
                score += (0.5f - ctx.ThreatLevel);
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
