# Module ID Bazli Degerlendirme (TR)

Tarih: 2026-04-25
Toplam modul: **36**

## Ozet
- Aktif: 21
- Pasif/Kosullu: 15

## Modul Tablosu
| Module ID | Class | Kategori | Durum | AutoRegister | Risk Bayraklari |
|---|---|---|---|---|---|
| AdaptiveAIDoctrineSystem | AdaptiveAIDoctrineSystem | Systems/AI | Aktif | True | StaticCollection |
| AIScheduler | AISchedulerSystem | Systems/Scheduling | Aktif | True | - |
| AscensionEvaluator | AscensionEvaluator | Systems/Progression | Pasif/Kosullu | False | StaticCollection |
| BanditPoliticsSystem | BanditPoliticsSystem | Systems/Diplomacy | Aktif | True | - |
| BountySystem | BountySystem | Systems/Bounty | Pasif/Kosullu | False | - |
| CaravanTaxSystem | CaravanTaxSystem | Systems/Economy | Aktif | True | - |
| CleanupSystem | PartyCleanupSystem | Systems/Cleanup | Pasif/Kosullu | False | SilentCatch |
| ConsolidationSystem | MilitiaConsolidationSystem | Systems/Cleanup | Aktif | True | LazySingleton_NoCleanup |
| CrisisEvents | CrisisEventSystem | Systems/Crisis | Pasif/Kosullu | False | SilentCatch |
| DevDataCollector | DevDataCollector | Systems/Dev | Aktif | True | StaticCollection, SilentCatch |
| FearSystem | FearSystem | Systems/Fear | Pasif/Kosullu | False | - |
| HardcoreDynamicHideoutSystem | HardcoreDynamicHideoutSystem | Systems/Spawning | Aktif | True | - |
| JailbreakMissionSystem | JailbreakMissionSystem | Systems/Events | Aktif | True | - |
| MilitiaAssertionSystem | MilitiaAssertionSystem | Systems/Diagnostics | Aktif | True | StaticCollection, SilentCatch |
| MilitiaMoraleSystem | MilitiaMoraleSystem | Systems/Combat | Aktif | True | - |
| MilitiaProgressionSystem | MilitiaProgressionSystem | Systems/Progression | Pasif/Kosullu | True | - |
| MilitiaRaidSystem | MilitiaRaidSystem | Systems/Raiding | Aktif | True | - |
| MilitiaUpgradeSystem_LEGACY | MilitiaUpgradeSystem | Systems/Progression | Pasif/Kosullu | True | - |
| NarrativeSystem | WarlordNarrativeSystem | Intelligence/Narrative | Pasif/Kosullu | True | StaticCollection |
| NervousSystem | NervousSystem | Core/Neural | Aktif | True | LazySingleton_NoCleanup |
| SeasonalEffectsSystem | SeasonalEffectsSystem | Systems/Seasonal | Aktif | True | - |
| SpatialGrid | SpatialGridSystem | Systems/Grid | Pasif/Kosullu | False | - |
| SpawningSystem | MilitiaSpawningSystem | Systems/Spawning | Pasif/Kosullu | False | StaticCollection |
| StaticDataCache | StaticDataCache | Intelligence/AI | Aktif | True | - |
| SwarmCoordinator | SwarmCoordinator | Intelligence/Swarm | Pasif/Kosullu | False | StaticCollection |
| TerritoryInfluence | TerritorySystem | Systems/Territory | Pasif/Kosullu | False | - |
| TroopProgressionSystem_LEGACY | TroopProgressionSystem | Systems/Progression | Pasif/Kosullu | True | - |
| WarlordBehaviorSystem | WarlordBehaviorSystem | Systems/Behavior | Aktif | True | - |
| WarlordCareer | WarlordCareerSystem | Systems/Progression | Aktif | True | - |
| WarlordEconomy | WarlordEconomySystem | Systems/Economy | Aktif | True | LazySingleton_NoCleanup |
| WarlordLegacy | WarlordLegacySystem | Systems/Legacy | Pasif/Kosullu | False | SilentCatch |
| WarlordLogisticsSystem | WarlordLogisticsSystem | Systems/Logistics | Pasif/Kosullu | False | - |
| WarlordSuccessionSystem | WarlordSuccessionSystem | Systems/Progression | Aktif | True | - |
| WarlordTacticsSystem | WarlordTacticsSystem | Systems/Enhancement | Aktif | True | StaticCollection |
| WarlordWorkshopSystem | WarlordWorkshopSystem | Systems/Workshop | Aktif | True | - |
| WorldMemory | WorldMemory | Core/Memory | Aktif | True | - |

## Modul Kartlari (kisa)
### [AdaptiveAIDoctrineSystem]
- Sinif: AdaptiveAIDoctrineSystem
- Kategori: Systems/AI
- Kaynak: BanditMilitias\Systems\AI\AdaptiveAIDoctrineSystem.cs
- Bagimliliklar: AdaptiveAIDoctrineSystem, EventBus, MilitiaEquipmentManager, NeuralEventRouter, PlayerTracker, WarlordLegitimacySystem, WarlordSystem, WarlordTacticsSystem
- Durum: Aktif
- Risk: StaticCollection
### [AIScheduler]
- Sinif: AISchedulerSystem
- Kategori: Systems/Scheduling
- Kaynak: BanditMilitias\Systems\Scheduling\AISchedulerSystem.cs
- Bagimliliklar: AISchedulerSystem, DevDataCollector
- Durum: Aktif
- Risk: -
### [AscensionEvaluator]
- Sinif: AscensionEvaluator
- Kategori: Systems/Progression
- Kaynak: BanditMilitias\Systems\Progression\AscensionEvaluator.cs
- Bagimliliklar: BlackMarketSystem, BountySystem, EventBus, FearSystem, WarlordCareerSystem, WarlordEconomySystem, WarlordSystem
- Durum: Pasif/Kosullu
- Risk: StaticCollection
### [BanditPoliticsSystem]
- Sinif: BanditPoliticsSystem
- Kategori: Systems/Diplomacy
- Kaynak: BanditMilitias\Systems\Diplomacy\BanditPoliticsSystem.cs
- Bagimliliklar: BountySystem, EventBus, NeuralEventRouter, WarlordSystem
- Durum: Aktif
- Risk: -
### [BountySystem]
- Sinif: BountySystem
- Kategori: Systems/Bounty
- Kaynak: BanditMilitias\Systems\Bounty\BountySystem.cs
- Bagimliliklar: EventBus, NeuralEventRouter, WarlordSystem
- Durum: Pasif/Kosullu
- Risk: -
### [CaravanTaxSystem]
- Sinif: CaravanTaxSystem
- Kategori: Systems/Economy
- Kaynak: BanditMilitias\Systems\Economy\CaravanTaxSystem.cs
- Bagimliliklar: FearSystem, SpatialGridSystem, WarlordSystem
- Durum: Aktif
- Risk: -
### [CleanupSystem]
- Sinif: PartyCleanupSystem
- Kategori: Systems/Cleanup
- Kaynak: BanditMilitias\Systems\Cleanup\PartyCleanupSystem.cs
- Bagimliliklar: DevDataCollector, EventBus, MilitiaConsolidationSystem, MilitiaSmartCache, NeuralEventRouter, PartyCleanupSystem, WarlordLegitimacySystem, WarlordSystem
- Durum: Pasif/Kosullu
- Risk: SilentCatch
### [ConsolidationSystem]
- Sinif: MilitiaConsolidationSystem
- Kategori: Systems/Cleanup
- Kaynak: BanditMilitias\Systems\Cleanup\MilitiaConsolidationSystem.cs
- Bagimliliklar: FearSystem, WarlordLegitimacySystem, WarlordSystem
- Durum: Aktif
- Risk: LazySingleton_NoCleanup
### [CrisisEvents]
- Sinif: CrisisEventSystem
- Kategori: Systems/Crisis
- Kaynak: BanditMilitias\Systems\Crisis\CrisisEventSystem.cs
- Bagimliliklar: BountySystem, EventBus, NeuralEventRouter, SpatialGridSystem, WarlordSystem
- Durum: Pasif/Kosullu
- Risk: SilentCatch
### [DevDataCollector]
- Sinif: DevDataCollector
- Kategori: Systems/Dev
- Kaynak: BanditMilitias\Systems\Dev\DevDataCollector.cs
- Bagimliliklar: AISchedulerSystem, BanditBrain, DevDataCollector, EventBus, SettlementDistanceCache, SpatialGridSystem, StaticDataCache, WarlordEconomySystem, WarlordLegitimacySystem
- Durum: Aktif
- Risk: StaticCollection, SilentCatch
### [FearSystem]
- Sinif: FearSystem
- Kategori: Systems/Fear
- Kaynak: BanditMilitias\Systems\Fear\FearSystem.cs
- Bagimliliklar: EventBus, NeuralEventRouter, WarlordSystem
- Durum: Pasif/Kosullu
- Risk: -
### [HardcoreDynamicHideoutSystem]
- Sinif: HardcoreDynamicHideoutSystem
- Kategori: Systems/Spawning
- Kaynak: BanditMilitias\Systems\Spawning\DynamicHideoutSystem.cs
- Bagimliliklar: BindingFlags, EventBus
- Durum: Aktif
- Risk: -
### [JailbreakMissionSystem]
- Sinif: JailbreakMissionSystem
- Kategori: Systems/Events
- Kaynak: BanditMilitias\Systems\Events\JailbreakMissionSystem.cs
- Bagimliliklar: WarlordSystem
- Durum: Aktif
- Risk: -
### [MilitiaAssertionSystem]
- Sinif: MilitiaAssertionSystem
- Kategori: Systems/Diagnostics
- Kaynak: BanditMilitias\Systems\Diagnostics\MilitiaAssertionSystem.cs
- Bagimliliklar: EventBus, MilitiaAssertionSystem, PartyCleanupSystem, WarlordEconomySystem, WarlordSystem
- Durum: Aktif
- Risk: StaticCollection, SilentCatch
### [MilitiaMoraleSystem]
- Sinif: MilitiaMoraleSystem
- Kategori: Systems/Combat
- Kaynak: BanditMilitias\Systems\Combat\MilitiaMoraleSystem.cs
- Bagimliliklar: SeasonalEffectsSystem, WarlordCareerSystem, WarlordSystem
- Durum: Aktif
- Risk: -
### [MilitiaProgressionSystem]
- Sinif: MilitiaProgressionSystem
- Kategori: Systems/Progression
- Kaynak: BanditMilitias\Systems\Progression\MilitiaProgressionSystem.cs
- Bagimliliklar: WarlordSystem
- Durum: Pasif/Kosullu
- Risk: -
### [MilitiaRaidSystem]
- Sinif: MilitiaRaidSystem
- Kategori: Systems/Raiding
- Kaynak: BanditMilitias\Systems\Raiding\MilitiaRaidSystem.cs
- Bagimliliklar: EventBus, FearSystem, NeuralEventRouter, SeasonalEffectsSystem, WarlordLegitimacySystem, WarlordSystem
- Durum: Aktif
- Risk: -
### [MilitiaUpgradeSystem_LEGACY]
- Sinif: MilitiaUpgradeSystem
- Kategori: Systems/Progression
- Kaynak: BanditMilitias\Systems\Progression\MilitiaUpgradeSystem.cs
- Bagimliliklar: MilitiaProgressionSystem
- Durum: Pasif/Kosullu
- Risk: -
### [NarrativeSystem]
- Sinif: WarlordNarrativeSystem
- Kategori: Intelligence/Narrative
- Kaynak: BanditMilitias\Intelligence\Narrative\WarlordNarrative.cs
- Bagimliliklar: EventBus, WarlordLegitimacySystem, WarlordSystem
- Durum: Pasif/Kosullu
- Risk: StaticCollection
### [NervousSystem]
- Sinif: NervousSystem
- Kategori: Core/Neural
- Kaynak: BanditMilitias\Core\Neural\NervousSystem.cs
- Bagimliliklar: AISchedulerSystem, FearSystem, NervousSystem, NeuralAdvisor, NeuralEventRouter, PlayerTracker, WarlordLogisticsSystem, WarlordSystem
- Durum: Aktif
- Risk: LazySingleton_NoCleanup
### [SeasonalEffectsSystem]
- Sinif: SeasonalEffectsSystem
- Kategori: Systems/Seasonal
- Kaynak: BanditMilitias\Systems\Seasonal\SeasonalEffectsSystem.cs
- Bagimliliklar: -
- Durum: Aktif
- Risk: -
### [SpatialGrid]
- Sinif: SpatialGridSystem
- Kategori: Systems/Grid
- Kaynak: BanditMilitias\Systems\Grid\SpatialGridSystem.cs
- Bagimliliklar: -
- Durum: Pasif/Kosullu
- Risk: -
### [SpawningSystem]
- Sinif: MilitiaSpawningSystem
- Kategori: Systems/Spawning
- Kaynak: BanditMilitias\Systems\Spawning\MilitiaSpawningSystem.cs
- Bagimliliklar: AISchedulerSystem, BindingFlags, CaravanActivityTracker, DevDataCollector, EventBus, MBObjectManager, MilitiaSpawningSystem, SystemWatchdog, WarActivityTracker, WarlordCareerSystem, WarlordSystem
- Durum: Pasif/Kosullu
- Risk: StaticCollection
### [StaticDataCache]
- Sinif: StaticDataCache
- Kategori: Intelligence/AI
- Kaynak: BanditMilitias\Intelligence\AI\Components\DataCache.cs
- Bagimliliklar: SettlementDistanceCache, StaticDataCache
- Durum: Aktif
- Risk: -
### [SwarmCoordinator]
- Sinif: SwarmCoordinator
- Kategori: Intelligence/Swarm
- Kaynak: BanditMilitias\Intelligence\Swarm\SwarmCoordinator.cs
- Bagimliliklar: EventBus, MilitiaSmartCache, SpatialGridSystem, WarlordSystem
- Durum: Pasif/Kosullu
- Risk: StaticCollection
### [TerritoryInfluence]
- Sinif: TerritorySystem
- Kategori: Systems/Territory
- Kaynak: BanditMilitias\Systems\Territory\TerritorySystem.cs
- Bagimliliklar: EventBus, NeuralEventRouter, WarlordSystem
- Durum: Pasif/Kosullu
- Risk: -
### [TroopProgressionSystem_LEGACY]
- Sinif: TroopProgressionSystem
- Kategori: Systems/Progression
- Kaynak: BanditMilitias\Systems\Progression\TroopProgressionSystem.cs
- Bagimliliklar: MilitiaProgressionSystem
- Durum: Pasif/Kosullu
- Risk: -
### [WarlordBehaviorSystem]
- Sinif: WarlordBehaviorSystem
- Kategori: Systems/Behavior
- Kaynak: BanditMilitias\Systems\Behavior\WarlordBehaviorSystem.cs
- Bagimliliklar: EventBus, WarlordCareerSystem, WarlordSystem
- Durum: Aktif
- Risk: -
### [WarlordCareer]
- Sinif: WarlordCareerSystem
- Kategori: Systems/Progression
- Kaynak: BanditMilitias\Systems\Progression\WarlordProgression.cs
- Bagimliliklar: AscensionEvaluator, BanditEnhancementSystem, BlackMarketSystem, EventBus, FearSystem, NeuralEventRouter, WarlordLegitimacySystem, WarlordSystem, WarlordWorkshopSystem
- Durum: Aktif
- Risk: -
### [WarlordEconomy]
- Sinif: WarlordEconomySystem
- Kategori: Systems/Economy
- Kaynak: BanditMilitias\Systems\Economy\WarlordEconomySystem.cs
- Bagimliliklar: BanditEnhancementSystem, MBObjectManager, NeuralEventRouter, WarlordCareerSystem, WarlordLegitimacySystem, WarlordSystem, WarlordWorkshopSystem
- Durum: Aktif
- Risk: LazySingleton_NoCleanup
### [WarlordLegacy]
- Sinif: WarlordLegacySystem
- Kategori: Systems/Legacy
- Kaynak: BanditMilitias\Systems\Legacy\WarlordLegacySystem.cs
- Bagimliliklar: EventBus, NeuralEventRouter
- Durum: Pasif/Kosullu
- Risk: SilentCatch
### [WarlordLogisticsSystem]
- Sinif: WarlordLogisticsSystem
- Kategori: Systems/Logistics
- Kaynak: BanditMilitias\Systems\Logistics\WarlordLogisticsSystem.cs
- Bagimliliklar: BanditEnhancementSystem, MBObjectManager, WarlordSystem
- Durum: Pasif/Kosullu
- Risk: -
### [WarlordSuccessionSystem]
- Sinif: WarlordSuccessionSystem
- Kategori: Systems/Progression
- Kaynak: BanditMilitias\Systems\Progression\WarlordSuccessionSystem.cs
- Bagimliliklar: EventBus, WarlordCareerSystem, WarlordSystem
- Durum: Aktif
- Risk: -
### [WarlordTacticsSystem]
- Sinif: WarlordTacticsSystem
- Kategori: Systems/Enhancement
- Kaynak: BanditMilitias\Systems\Enhancement\EnhancementSystem.cs
- Bagimliliklar: WarlordCareerSystem, WarlordEconomySystem, WarlordSystem, WarlordTacticsSystem
- Durum: Aktif
- Risk: StaticCollection
### [WarlordWorkshopSystem]
- Sinif: WarlordWorkshopSystem
- Kategori: Systems/Workshop
- Kaynak: BanditMilitias\Systems\Workshop\WarlordWorkshopSystem.cs
- Bagimliliklar: EventBus, NeuralEventRouter, WarlordSystem
- Durum: Aktif
- Risk: -
### [WorldMemory]
- Sinif: WorldMemory
- Kategori: Core/Memory
- Kaynak: BanditMilitias\Core\Memory\WorldMemory.cs
- Bagimliliklar: EventBus
- Durum: Aktif
- Risk: -
