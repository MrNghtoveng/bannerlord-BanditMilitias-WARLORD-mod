Directory structure:
└── mrnghtoveng-bannerlord-banditmilitias-warlord-mod/
    ├── README.md
    └── src/
        └── BanditMilitias/
            ├── README.md
            ├── BanditMilitias.csproj
            ├── BanditMilitias.sln
            ├── KURULUM.md
            ├── Module.xsd
            ├── SubModule.xml
            ├── .editorconfig
            ├── BanditMilitias.Tests/
            │   ├── AdaptiveDoctrineRulesTests.cs
            │   ├── AIWiringTests.cs
            │   ├── ArchitectureTests.cs
            │   ├── BanditMilitias.Tests.csproj
            │   ├── BanditMilitias.Tests.sln
            │   ├── BanditPoliticsRulesTests.cs
            │   ├── BanditTestHubIntegrationTests.cs
            │   ├── DecisionRulesTests.cs
            │   ├── ModuleManagerRetryTests.cs
            │   ├── ModuleRegistryHealthAnalyzerTests.cs
            │   ├── ModuleRegistryTests.cs
            │   ├── PIDControllerTests.cs
            │   ├── RegistryAuditTests.cs
            │   ├── RegressionFixTests.cs
            │   ├── SpawnCleanupIntegrationWiringTests.cs
            │   ├── SpawnDecisionRulesTests.cs
            │   ├── SpawnPipelineIntegrationTests.cs
            │   ├── TelemetryRegressionTests.cs
            │   ├── TestSourceHelper.cs
            │   ├── WarlordProgressionRulesTests.cs
            │   └── XmlValidationTests.cs
            ├── Behaviors/
            │   ├── MilitiaBehavior.cs
            │   ├── MilitiaDiplomacyCampaignBehavior.cs
            │   └── WarlordCampaignBehavior.cs
            ├── Components/
            │   └── MilitiaPartyComponent.cs
            ├── Core/
            │   ├── MathUtils.cs
            │   ├── Components/
            │   │   ├── MilitiaModuleBase.cs
            │   │   └── ModuleSpecializations.cs
            │   ├── Config/
            │   │   ├── Constants.cs
            │   │   └── DynamicDifficulty.cs
            │   ├── Events/
            │   │   ├── EventBus.cs
            │   │   └── Events.cs
            │   └── Registry/
            │       └── ModuleRegistry.cs
            ├── Debug/
            │   ├── Debug.cs
            │   └── DebugLogger.cs
            ├── Documentation/
            │   ├── AI_Assisted_Development.md
            │   ├── AI_Assisted_Development.txt
            │   ├── AIArchitecture.md
            │   ├── AIArchitecture.txt
            │   ├── InGameTestingGuide.md
            │   ├── InGameTestingGuide.txt
            │   ├── ModuleHierarchy.md
            │   ├── ModuleHierarchy.txt
            │   ├── ProjectHierarchy.md
            │   ├── ProjectHierarchy.txt
            │   └── SystemFlowTree.md
            ├── GUI/
            │   ├── GauntletUI/
            │   │   └── MilitiaIntelLayer.cs
            │   └── ViewModels/
            │       └── LackeyVM.cs
            ├── Infrastructure/
            │   ├── BanditMilitiasSaveDefiner.cs
            │   ├── ClanCache.cs
            │   ├── CompatibilityLayer.cs
            │   ├── ExceptionMonitor.cs
            │   ├── FileLogger.cs
            │   ├── HealthCheck.cs
            │   ├── ModuleInfra.cs
            │   ├── ModuleManager.cs
            │   ├── SafeTelemetry.cs
            │   ├── Utilities.cs
            │   └── Mcm/
            │       └── McmAbstractionsCompat.cs
            ├── Intelligence/
            │   ├── AI/
            │   │   ├── CustomMilitiaAI.cs
            │   │   ├── PatrolDetection.cs
            │   │   ├── ScoringFunctions.cs
            │   │   └── Components/
            │   │       ├── DataCache.cs
            │   │       ├── MilitiaActionExecutor.cs
            │   │       ├── MilitiaAISensors.cs
            │   │       └── MilitiaDecider.cs
            │   ├── Logging/
            │   │   └── AIDecisionLogger.cs
            │   ├── Narrative/
            │   │   └── WarlordNarrative.cs
            │   ├── Strategic/
            │   │   ├── HTNEngine.cs
            │   │   └── StrategyEngine.cs
            │   ├── Swarm/
            │   │   └── SwarmCoordinator.cs
            │   └── Tactical/
            │       ├── AmbushTactics.cs
            │       ├── HTNCore.cs
            │       ├── TacticalDoctrines.cs
            │       ├── TacticalTasks.cs
            │       └── WarlordTacticalMissionBehavior.cs
            ├── Models/
            │   └── GameModels.cs
            ├── ModuleData/
            │   ├── check_xml_sync.py
            │   ├── check_xmls.py
            │   ├── check_xmls_portable.py
            │   ├── fix_xmls.py
            │   ├── lords.xml
            │   ├── GUI/
            │   │   └── Prefabs/
            │   │       └── LackeyPanel.xml
            │   └── Languages/
            │       ├── EN/
            │       │   └── std_BanditMilitias_xml_en.xml
            │       └── TR/
            │           └── std_BanditMilitias_xml_tr.xml
            ├── Patches/
            │   ├── AiPatrollingBehaviorPatch.cs
            │   ├── BanditAiPatch.cs
            │   ├── MilitiaSpeedPatch.cs
            │   └── SurrenderCrashPatch.cs
            ├── System.IndexRange/
            │   ├── Index.cs
            │   └── Range.cs
            ├── Systems/
            │   ├── AI/
            │   │   ├── AdaptiveAIDoctrineSystem.cs
            │   │   ├── AdaptiveDoctrineDataLogger.cs
            │   │   ├── AdaptiveDoctrineTypes.cs
            │   │   └── MilitiaEquipmentManager.cs
            │   ├── Behavior/
            │   │   └── WarlordBehaviorSystem.cs
            │   ├── Bounty/
            │   │   └── BountySystem.cs
            │   ├── Cleanup/
            │   │   ├── MilitiaConsolidationSystem.cs
            │   │   └── PartyCleanupSystem.cs
            │   ├── Combat/
            │   │   ├── Combat.cs
            │   │   └── MilitiaMoraleSystem.cs
            │   ├── Crisis/
            │   │   └── CrisisEventSystem.cs
            │   ├── Dev/
            │   │   └── DevDataCollector.cs
            │   ├── Diagnostics/
            │   │   ├── BanditTestHub.cs
            │   │   ├── DiagnosticsSystem.cs
            │   │   └── ModuleRegistryHealthAnalyzer.cs
            │   ├── Diplomacy/
            │   │   ├── BanditPoliticsSystem.cs
            │   │   ├── DuelSystem.cs
            │   │   ├── ExtortionSystem.cs
            │   │   └── PropagandaSystem.cs
            │   ├── Economy/
            │   │   ├── BlackMarketSystem.cs
            │   │   ├── CaravanTaxSystem.cs
            │   │   └── WarlordEconomySystem.cs
            │   ├── Enhancement/
            │   │   └── EnhancementSystem.cs
            │   ├── Events/
            │   │   └── JailbreakMissionSystem.cs
            │   ├── Fear/
            │   │   └── FearSystem.cs
            │   ├── Grid/
            │   │   ├── CampaignGridSystem.cs
            │   │   └── SpatialGridSystem.cs
            │   ├── Legacy/
            │   │   └── WarlordLegacySystem.cs
            │   ├── Logistics/
            │   │   └── WarlordLogisticsSystem.cs
            │   ├── Progression/
            │   │   ├── AscensionEvaluator.cs
            │   │   ├── WarlordProgression.cs
            │   │   └── WarlordSuccessionSystem.cs
            │   ├── Raiding/
            │   │   └── MilitiaRaidSystem.cs
            │   ├── Scheduling/
            │   │   └── AISchedulerSystem.cs
            │   ├── Seasonal/
            │   │   └── SeasonalEffectsSystem.cs
            │   ├── Spawning/
            │   │   ├── DynamicHideoutSystem.cs
            │   │   ├── MilitiaNameGenerator.cs
            │   │   └── MilitiaSpawningSystem.cs
            │   ├── Territory/
            │   │   └── TerritorySystem.cs
            │   ├── Tracking/
            │   │   ├── ActivityTracker.cs
            │   │   └── PlayerTracker.cs
            │   └── Workshop/
            │       └── WarlordWorkshopSystem.cs
            └── tools/
                └── Validate-ModuleDataXml.ps1