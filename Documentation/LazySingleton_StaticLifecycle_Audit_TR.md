# Lazy Singleton ve Statik Yasam Dongusu Denetimi (TR)

Tarih: 2026-04-25
Toplam isaretlenen modul: **36**

| Module ID | Class | Lazy | StaticCollection | StaticInstance | Cleanup | Risk | Dosya |
|---|---|---|---|---|---|---|---|
| AdaptiveAIDoctrineSystem | AdaptiveAIDoctrineSystem | True | True | True | True | StaticCollection | BanditMilitias\Systems\AI\AdaptiveAIDoctrineSystem.cs |
| AIScheduler | AISchedulerSystem | False | False | True | True | Observe | BanditMilitias\Systems\Scheduling\AISchedulerSystem.cs |
| AscensionEvaluator | AscensionEvaluator | True | True | True | True | StaticCollection | BanditMilitias\Systems\Progression\AscensionEvaluator.cs |
| BanditPoliticsSystem | BanditPoliticsSystem | True | False | True | True | Observe | BanditMilitias\Systems\Diplomacy\BanditPoliticsSystem.cs |
| BountySystem | BountySystem | True | False | True | True | Observe | BanditMilitias\Systems\Bounty\BountySystem.cs |
| CaravanTaxSystem | CaravanTaxSystem | True | False | True | True | Observe | BanditMilitias\Systems\Economy\CaravanTaxSystem.cs |
| CleanupSystem | PartyCleanupSystem | False | False | True | True | Observe | BanditMilitias\Systems\Cleanup\PartyCleanupSystem.cs |
| ConsolidationSystem | MilitiaConsolidationSystem | True | False | True | False | Lazy_NoCleanup, StaticInstance_NoCleanup | BanditMilitias\Systems\Cleanup\MilitiaConsolidationSystem.cs |
| CrisisEvents | CrisisEventSystem | True | False | True | True | Observe | BanditMilitias\Systems\Crisis\CrisisEventSystem.cs |
| DevDataCollector | DevDataCollector | False | True | True | True | StaticCollection | BanditMilitias\Systems\Dev\DevDataCollector.cs |
| FearSystem | FearSystem | True | False | True | True | Observe | BanditMilitias\Systems\Fear\FearSystem.cs |
| HardcoreDynamicHideoutSystem | HardcoreDynamicHideoutSystem | True | False | True | True | Observe | BanditMilitias\Systems\Spawning\DynamicHideoutSystem.cs |
| JailbreakMissionSystem | JailbreakMissionSystem | True | False | True | True | Observe | BanditMilitias\Systems\Events\JailbreakMissionSystem.cs |
| MilitiaAssertionSystem | MilitiaAssertionSystem | False | True | True | True | StaticCollection | BanditMilitias\Systems\Diagnostics\MilitiaAssertionSystem.cs |
| MilitiaMoraleSystem | MilitiaMoraleSystem | True | False | True | True | Observe | BanditMilitias\Systems\Combat\MilitiaMoraleSystem.cs |
| MilitiaProgressionSystem | MilitiaProgressionSystem | True | False | True | True | Observe | BanditMilitias\Systems\Progression\MilitiaProgressionSystem.cs |
| MilitiaRaidSystem | MilitiaRaidSystem | True | False | True | True | Observe | BanditMilitias\Systems\Raiding\MilitiaRaidSystem.cs |
| MilitiaUpgradeSystem_LEGACY | MilitiaUpgradeSystem | True | False | True | True | Observe | BanditMilitias\Systems\Progression\MilitiaUpgradeSystem.cs |
| NarrativeSystem | WarlordNarrativeSystem | True | True | True | True | StaticCollection | BanditMilitias\Intelligence\Narrative\WarlordNarrative.cs |
| NervousSystem | NervousSystem | True | False | True | False | Lazy_NoCleanup, StaticInstance_NoCleanup | BanditMilitias\Core\Neural\NervousSystem.cs |
| SeasonalEffectsSystem | SeasonalEffectsSystem | True | False | True | True | Observe | BanditMilitias\Systems\Seasonal\SeasonalEffectsSystem.cs |
| SpatialGrid | SpatialGridSystem | False | False | True | True | Observe | BanditMilitias\Systems\Grid\SpatialGridSystem.cs |
| SpawningSystem | MilitiaSpawningSystem | False | True | False | True | StaticCollection | BanditMilitias\Systems\Spawning\MilitiaSpawningSystem.cs |
| StaticDataCache | StaticDataCache | False | False | True | True | Observe | BanditMilitias\Intelligence\AI\Components\DataCache.cs |
| SwarmCoordinator | SwarmCoordinator | False | True | True | True | StaticCollection | BanditMilitias\Intelligence\Swarm\SwarmCoordinator.cs |
| TerritoryInfluence | TerritorySystem | False | False | True | True | Observe | BanditMilitias\Systems\Territory\TerritorySystem.cs |
| TroopProgressionSystem_LEGACY | TroopProgressionSystem | True | False | True | True | Observe | BanditMilitias\Systems\Progression\TroopProgressionSystem.cs |
| WarlordBehaviorSystem | WarlordBehaviorSystem | True | False | True | True | Observe | BanditMilitias\Systems\Behavior\WarlordBehaviorSystem.cs |
| WarlordCareer | WarlordCareerSystem | True | False | True | True | Observe | BanditMilitias\Systems\Progression\WarlordProgression.cs |
| WarlordEconomy | WarlordEconomySystem | True | False | True | False | Lazy_NoCleanup, StaticInstance_NoCleanup | BanditMilitias\Systems\Economy\WarlordEconomySystem.cs |
| WarlordLegacy | WarlordLegacySystem | True | False | True | True | Observe | BanditMilitias\Systems\Legacy\WarlordLegacySystem.cs |
| WarlordLogisticsSystem | WarlordLogisticsSystem | True | False | True | True | Observe | BanditMilitias\Systems\Logistics\WarlordLogisticsSystem.cs |
| WarlordSuccessionSystem | WarlordSuccessionSystem | True | False | True | True | Observe | BanditMilitias\Systems\Progression\WarlordSuccessionSystem.cs |
| WarlordTacticsSystem | WarlordTacticsSystem | True | True | True | True | StaticCollection | BanditMilitias\Systems\Enhancement\EnhancementSystem.cs |
| WarlordWorkshopSystem | WarlordWorkshopSystem | True | False | True | True | Observe | BanditMilitias\Systems\Workshop\WarlordWorkshopSystem.cs |
| WorldMemory | WorldMemory | True | False | True | True | Observe | BanditMilitias\Core\Memory\WorldMemory.cs |

## Aksiyon Kurali
- P1: `Lazy_NoCleanup` veya `StaticInstance_NoCleanup` olan modullere `ResetForNewSession` ve `CleanupForUnload` eklenmeli.
- P2: `StaticCollection` olan modullerde tekil clear noktasi ve test senaryosu zorunlu olmali.
- P3: Sadece `Observe` olanlarda telemetry ile session-id takibi yeterli olabilir.
