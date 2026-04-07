# Bandit Militias: WARLORD - Directory Structure

Bu belge, projenin klasor yapisini yuksek seviyede ozetler. Amac tum tekil dosyalari listelemek degil; hangi klasorun ne is yaptigini ve gelistirme sirasinda nereye bakilacagini hizli gostermektir.

## Temel Prensip

Proje, sorumluluk alanlarina gore klasorlere ayrilir.

* `Behaviors` oyun event'lerini dinleyen campaign behavior katmanidir
* `Core` ortak altyapi, EventBus ve modul temel siniflarini tutar
* `Infrastructure` modul yonetimi, uyumluluk ve guvenlik yardimcilarini barindirir
* `Intelligence` stratejik, swarm ve taktik karar katmanlarini toplar
* `Systems` asil oyun mekaniklerini moduler servisler halinde uygular
* `Documentation` gelistirici dokumantasyonunu toplar
* `ModuleData` oyun tarafina giden XML, localization ve GUI asset'lerini tutar

Bu ayrim, davranis mantigi ile altyapi ve veri dosyalarinin tek yerde toplanmasini onler.

## Ust Dizin

Projenin kokunde dogrudan mod girisi, ayarlar ve paketleme dosyalari yer alir:

```text
BanditMilitias/
|-- BanditMilitias.csproj
|-- BanditMilitias.sln
|-- Settings.cs
|-- SubModule.cs
|-- SubModule.xml
|-- README.md
|-- KURULUM.md
|-- Module.xsd
|-- Behaviors/
|-- Components/
|-- Core/
|-- Debug/
|-- Documentation/
|-- GUI/
|-- Infrastructure/
|-- Intelligence/
|-- Models/
|-- ModuleData/
|-- Patches/
|-- Systems/
|-- tools/
|-- BanditMilitias.Tests/
```

## Ana Kod Klasorleri

### `Behaviors`

Campaign event baglantilari burada toplanir.

* `MilitiaBehavior.cs`: ana campaign behavior, bootstrap ve bazi inline alt behavior'lar
* `MilitiaDiplomacyCampaignBehavior.cs`: diplomasi event akislari
* `WarlordCampaignBehavior.cs`: warlord odakli campaign entegrasyonu

Not: `MilitiaHideoutCampaignBehavior` ve `MilitiaRewardCampaignBehavior` su anda ayri dosya yerine `MilitiaBehavior.cs` icinde yasamaktadir.

### `Core`

Tum sistemlerin ortak kullandigi cekirdek katmandir.

* `Core/Components`: `MilitiaModuleBase` ve modul uzmanlasmalari
* `Core/Config`: sabitler ve dinamik zorluk hesaplari
* `Core/Events`: `EventBus.cs` ve ortak event tipleri
* `Core/Registry`: modul registry mantigi

### `Infrastructure`

Altyapi ve guvenlik yardimcilari burada yer alir.

* `ModuleManager.cs`: modul lifecycle ve campaign event registration
* `CompatibilityLayer.cs`: farkli oyun surumleri ve uyumluluk korumalari
* `HealthCheck.cs`, `ExceptionMonitor.cs`, `SafeTelemetry.cs`: runtime saglik ve koruma akislari
* `Infrastructure/Mcm`: MCM uyumluluk katmani

### `Intelligence`

AI ve karar verme katmanlari bu dizinde toplanir.

* `Strategic`: `BanditBrain`, `StrategyEngine`, `WarlordSystem`
* `Swarm`: grup koordinasyonu ve parti isbirligi
* `Tactical`: HTN planner, doctrine ve mission behavior
* `ML`: veri toplama ve deneysel ogrenme katmanlari
* `Narrative`: warlord anlatim ve flavor sistemleri
* `Logging`: AI karar loglari

Not: `WarlordTacticalMissionBehavior.cs` aktif mission wiring noktasidir; `HTNCore.cs`, `TacticalDoctrines.cs` ve `TacticalTasks.cs` bunu besleyen taktik altyapidir.

### `Systems`

Modun asil oynanis mekanikleri buradadir. Alt klasorler alan bazli ayrilmistir:

* `AI`
* `Behavior`
* `Bounty`
* `Cleanup`
* `Combat`
* `Crisis`
* `Diagnostics`
* `Diplomacy`
* `Economy`
* `Enhancement`
* `Events`
* `Fear`
* `Grid`
* `Heroics`
* `Legacy`
* `Logistics`
* `Progression`
* `Raiding`
* `Scheduling`
* `Seasonal`
* `Spawning`
* `Territory`
* `Tracking`
* `Workshop`

Ornek aktif dosyalar:

* `Systems/Spawning/MilitiaSpawningSystem.cs`
* `Systems/Spawning/DynamicHideoutSystem.cs`
* `Systems/Scheduling/AISchedulerSystem.cs`
* `Systems/Fear/FearSystem.cs`
* `Systems/Diagnostics/BanditTestHub.cs`
* `Systems/Tracking/ActivityTracker.cs`

### `ModuleData`

Oyun tarafina tasinan veri ve arayuz dosyalari:

* `bandits.xml`
* `lords.xml`
* `Languages/EN/std_BanditMilitias_xml_en.xml`
* `Languages/TR/std_BanditMilitias_xml_tr.xml`
* `GUI/Prefabs/LackeyPanel.xml`

### `GUI`

Gauntlet UI ve ViewModel tarafini tutar.

* `GauntletUI/MilitiaIntelLayer.cs`
* `ViewModels/LackeyVM.cs`

### `Patches`

Harmony patch dosyalari burada tutulur.

* `AiPatrollingBehaviorPatch.cs`
* `BanditAiPatch.cs`
* `MilitiaSpeedPatch.cs`
* `SurrenderCrashPatch.cs`

### `Models`

Bannerlord game model override'lari bu klasordedir.

* `GameModels.cs`

### `BanditMilitias.Tests`

Unit, integration ve regression testleri bu proje altindadir.

* modul registry ve architecture testleri
* XML validation testleri
* spawn, AI, telemetry ve regression testleri

## Yardimci ve Uretim Disi Klasorler

Asagidaki klasorler gelistirme ve build surecinde olusur:

* `bin`
* `obj`
* `dist`
* `TestResults`

Bu klasorler kodun mantigini aciklamaz; derleme, paketleme ve test ciktilarini tutar.

## Hizli Navigasyon Onerisi

Belirli bir sorun icin ilk bakilacak yerler genelde sunlardir:

1. Campaign event veya bootstrap sorunuysa `SubModule.cs`, `Behaviors/`, `Infrastructure/ModuleManager.cs`
2. Event dagitimi sorunuysa `Core/Events/EventBus.cs`
3. AI karar sorunuysa `Intelligence/` ve `Systems/Scheduling/`
4. Oynanis mekanigi sorunuysa ilgili `Systems/*` alt klasoru
5. XML veya UI sorunuysa `ModuleData/` ve `GUI/`
6. Regression kontrolu icin `BanditMilitias.Tests/`

## Neden Onemli?

Bu dizin yapisi, projeyi sadece buyuk bir kod yigini olmaktan cikarir. Asil faydasi:

* yeni ozellik eklerken dogru katmana gitmeyi kolaylastirmak
* ghost, dead veya baglanmamis kodu daha hizli fark etmek
* test, XML ve runtime sistemlerini birbirinden ayri takip edebilmek

Yani klasor yapisi, hem gelistirme hizini hem de modun bakimini dogrudan etkiler.
