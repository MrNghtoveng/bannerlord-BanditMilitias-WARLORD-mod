# Bandit Militias: WARLORD Edition

> **TR:** Bannerlord için hibrit yapay zeka ve dinamik dünya simülasyonu  
> **EN:** Hybrid AI and dynamic world simulation for Mount & Blade II: Bannerlord

Bandit Militias: WARLORD Edition, sıradan haydut partilerini daha zeki, daha saldırgan ve daha geniş bölgesel sonuçlar üreten aktörlere dönüştürmeyi hedefleyen deneysel bir mod projesidir.

Bandit Militias: WARLORD Edition is an experimental mod project that aims to turn ordinary bandit parties into smarter, more aggressive, and more region-shaping actors.

> [!WARNING]
> **TR:** Bu mod aktif geliştirme aşamasındadır. Hatalar, eksik sistemler, dengesiz oynanış, kayıt uyumsuzluğu ve sürümler arasında davranış değişiklikleri görülebilir.  
> **EN:** This mod is under active development. Bugs, incomplete systems, unstable balance, save incompatibilities, and behavior changes between versions should be expected.

## Türkçe

### Bu Mod Ne Yapıyor?

Bu mod, Bannerlord'daki klasik haydutları yalnızca yolda gezen kolay hedefler olmaktan çıkarıp; kendi çıkarları, güç dengesi ve bölgesel etkileri olan yapılara dönüştürmeyi amaçlar.

Amaç yalnızca daha güçlü düşmanlar üretmek değildir. Asıl hedef, kampanya haritasında daha canlı, daha tehlikeli ve daha öngörülemez bir haydut ekosistemi oluşturmaktır.

### Öne Çıkan Noktalar

- **Daha akıllı haydut davranışı:** Gruplar yalnızca dolaşmaz; hedef seçer, baskı kurar ve fırsat kollar.
- **Warlord yükselişi:** Güçlenen gruplar zamanla liderlik yapıları ve politik ağırlık kazanır.
- **Bölgesel baskı:** Bazı alanlar kalıcı biçimde istikrarsız hale gelebilir.
- **Ekonomik dalga etkisi:** Ticaret yolları, köy üretimi ve şehir dengesi dolaylı olarak etkilenebilir.

### Oynanış Dinamikleri

#### Warlord Hiyerarşisi

Belirli bir güce ulaşan grupların başına, kendine özgü kariyer hedefleri olan liderler geçer. Her warlord; meşruiyet, halefiyet ve miras eksenlerinde takip edilir.

#### Bölgesel Hakimiyet

Haydutlar köyleri fiziksel olarak ablukaya alabilir. "Haydut Bölgesi" ilan edilen yerlerde üretim durur, güvenlik zayıflar ve krallık otoritesi geriler.

#### Ekonomik Yıkım (Kelebek Etkisi)

Kritik ticaret düğümlerinin ele geçirilmesi, şehirlere hammadde akışını keserek kıtlık, huzursuzluk ve isyan riskini tetikleyebilir.

### Evrim Sınıfları

Warlord kariyer sistemi, grupların güç kazandıkça geçtiği 6 aşamalı bir ilerleme yapısı kullanır:

1. **Eşkıya:** Hayatta kalma, yağma ve küçük ölçekli örgütlenme aşaması.
2. **Rebel:** Yerel düzeni zorlayan, daha saldırgan isyancı çekirdek.
3. **Famous Bandit:** Bölgesel ölçekte tanınan ve etki alanı büyüyen lider.
4. **Warlord:** Askeri ve siyasi ağırlığı belirginleşen güç odağı.
5. **Tanınmış:** Çevredeki aktörler tarafından ciddiye alınan, diplomatik etkisi büyüyen yapı.
6. **Fatih:** En üst kariyer seviyesi; hegemonya kurmaya yaklaşan baskın figür.

### Sistem Özeti

- **Stratejik katman:** `BanditBrain`, `WarlordSystem`, `HTNEngine`
- **Operasyonel katman:** `MilitiaDecider`, `AISchedulerSystem`, `SwarmCoordinator`, `AdaptiveAIDoctrineSystem`
- **Dünya ve performans katmanı:** `SpatialGridSystem`, `PartyCleanupSystem`, `PlayerTracker`, `FearSystem`

### EventBus ve Aktivasyon

- `EventBus`, mod yüklenirken temizlenir ve modüller kaydedildikten sonra `ModuleManager.InitializeAll()` ile subscriber bağlantıları açılır.
- Deferred initialization yüzünden bazı EventBus zincirleri oyun oturumu açılır açılmaz değil, `RunDeferredSystemInit()` ve session bootstrap tamamlandıktan sonra tam kapasite çalışır.
- Runtime sırasında kuyruk `SubModule.OnApplicationTick()` içinde işlenir. Aşırı hata fırtınasında veya emergency stop durumunda EventBus bilinçli olarak devre dışı bırakılır ve kuyruk boşaltılır.

### Son Güncellemeler (Mayıs 2026)

- **Bannerlord 1.3.15 Uyumluluğu:** Mod, Bannerlord'un 1.3.15 sürümü için tam uyumlu hale getirildi. Reflection tabanlı bridge yapıları ve XML tanımları bu sürümün gereksinimlerine göre güncellendi.
- **SaveSystem Ayrıklaştırma (Decoupling):** Modun kalıcı verileri oyunun kısıtlayıcı `SaveSystem` yapısından tamamen çıkarıldı. Artık tüm veriler runtime registry (`MilitiaBehavior`) üzerinden yönetiliyor, bu da kayıt dosyası bozulmalarını engelliyor ve modun güvenle eklenip çıkarılabilmesini sağlıyor.
- **Null-Safety ve Stabilite:** Kod genelindeki 200'den fazla C# null-safety uyarısı (CS86xx) giderildi. `AgentCrashGuard` sistemi entegre edilerek runtime hatalarına karşı ekstra koruma katmanı eklendi.
- **BannerlordTestSim Entegrasyonu:** Gelişmiş simülasyon ve automated testing framework'ü eklendi. Modun tüm fonksiyonları 230+ test senaryosu ile %100 başarı oranına ulaştırıldı.
- **Mimari Stabilizasyon:** `ModLifecycleManager` üzerinde "Cerrahi Düzeltme" yapılarak `ISystemInitiable` tasarım deseni (pattern) entegre edildi.
- **Fail-Fast (Hızlı Çökme) Prensibi:** `EventBus` içerisindeki sessiz hata yutan (silent catch) try-catch blokları kaldırılarak sistemin state bozulmaları (corruption) engellendi.
- `MilitiaHideoutCampaignBehavior` artık terk edilmiş sığınaklar için günlük yeniden dolum mantığını kullanır.
- `MilitiaRewardCampaignBehavior` oyuncuya verilen milis ödülünü ertesi güne kuyruklayarak öder.

### Gereksinimler ve Kurulum

Gerekli modlar:

1. `Bannerlord.Harmony`
2. `Bannerlord.UIExtenderEx`
3. `Bannerlord.ButterLib`

Opsiyonel mod:

1. `MCMv5`

Yükleme sırası:

1. `Bannerlord.Harmony`
2. `Bannerlord.UIExtenderEx`
3. `Bannerlord.ButterLib`
4. `MCMv5` (varsa)
5. `BanditMilitias`

Detaylı kurulum: [KURULUM.md](KURULUM.md)

### Proje Yapısı

Bu depo kök dizin tabanlıdır; aktif kaynak kod `src/BanditMilitias/...` altında değil, doğrudan proje kökünde yer alır.

- `BanditMilitias.csproj`: ana mod projesi
- `BanditMilitias.Tests/`: test projesi ve test kaynakları
- `Systems/`, `Behaviors/`, `Infrastructure/`, `Intelligence/`: ana C# kaynak kodu
- `ModuleData/`: XML, dil dosyaları ve oyun verisi
- `tools/`: yardımcı script ve doğrulama araçları

### Derleme ve Test

```powershell
dotnet build .\BanditMilitias.csproj -c Debug -nologo -v minimal
dotnet build .\BanditMilitias.csproj -c Release -nologo -v minimal
dotnet build .\BanditMilitias.csproj -c Release /p:UseMcm=true
dotnet test .\BanditMilitias.Tests\BanditMilitias.Tests.csproj -v minimal
```

### Runtime TestHub (Alt+~)

Gelişmiş diagnostic test sistemi için mod içerisinden şu konsol komutlarını kullanabilirsiniz:
- `bandit.test_list`: Mevcut tüm test ve sağlık kontrollerini listeler.
- `bandit.test_run all`: Tüm modülleri (warm-up, pipeline, warlord logic) test eder.
- `bandit.test_report`: "Cold module" analizi ve ihlal özetini sunar.

### Karakter Notu

Bazı terminal veya editör yapılandırmalarında Türkçe karakterler bozuk görünebilir. README dosyası UTF-8 olarak yazılmıştır; GitHub ve modern editörlerde doğru görünmelidir.

### Atıf

Bu proje, **JungleDruid** tarafından geliştirilen orijinal `BanditMilitias` modunu temel alan deneysel bir devam ve yeniden yorumlama çalışmasıdır. WARLORD Edition, bu temelin üzerine daha geniş yapay zeka, koordinasyon ve dünya simülasyonu hedefleri ekler.

**Proje lideri:** MrNghtoveng  
**AI destekli geliştirme araçları:** Antigravity IDE, Codex, Claude

## English

### What Does This Mod Do?

This mod aims to transform Bannerlord's standard bandits from simple roaming targets into groups with their own ambitions, pressure patterns, and regional consequences.

The goal is not simply to make bandits stronger. The real objective is to create a campaign map where bandit activity feels more alive, more dangerous, and less predictable.

### Highlights

- **Smarter bandit behavior:** Groups do more than roam; they choose targets, apply pressure, and exploit opportunities.
- **Rise of warlords:** As groups grow stronger, they develop leadership structures and political weight.
- **Regional pressure:** Some areas can become persistently unstable.
- **Economic ripple effects:** Trade routes, village production, and city stability can all be affected indirectly.

### Gameplay Dynamics

#### Warlord Hierarchy

Once groups reach a certain threshold of power, leaders with distinct long-term ambitions begin to emerge. Each warlord is tracked through legitimacy, succession, and legacy systems.

#### Regional Dominance

Bandit forces can physically blockade villages. In areas declared as "Bandit Zones", production can stall, security can deteriorate, and kingdom authority can weaken.

#### Economic Disruption (The Butterfly Effect)

Capturing critical trade nodes can cut the flow of raw materials into cities, potentially triggering shortages, unrest, and even rebellion.

### Evolution Classes

The warlord career system uses a 6-stage progression structure that reflects how groups evolve as they gain power:

1. **Eşkıya / Outlaw:** The survival stage focused on raiding and small-scale organization.
2. **Rebel:** A more aggressive insurgent core that begins to disrupt local order.
3. **Famous Bandit:** A regionally recognized leader with expanding reach and influence.
4. **Warlord:** A genuine military and political force rather than just a gang leader.
5. **Tanınmış / Recognized:** A power bloc taken seriously by surrounding actors, with growing diplomatic weight.
6. **Fatih / Conqueror:** The highest career stage, representing near-hegemonic dominance.

### System Snapshot

- **Strategic layer:** `BanditBrain`, `WarlordSystem`, `HTNEngine`
- **Operational layer:** `MilitiaDecider`, `AISchedulerSystem`, `SwarmCoordinator`, `AdaptiveAIDoctrineSystem`
- **World and performance layer:** `SpatialGridSystem`, `PartyCleanupSystem`, `PlayerTracker`, `FearSystem`

### EventBus and Activation

- The `EventBus` is cleared during module load, then module subscribers come online once `ModuleManager.InitializeAll()` runs.
- Because heavy systems use deferred initialization, some EventBus chains are intentionally delayed until `RunDeferredSystemInit()` and session bootstrap complete.
- The deferred queue is pumped from `SubModule.OnApplicationTick()`. During tick-storm recovery or emergency stop, the bus is intentionally disabled and the queue is drained.

### Recent Updates (May 2026)

- **Bannerlord 1.3.15 Compatibility:** The mod is now fully validated and patched for Bannerlord v1.3.15. Reflection bridges and XML schemas have been updated to match the latest engine requirements.
- **SaveSystem Decoupling:** Persistent data management has been completely decoupled from the game's restricted `SaveSystem`. All data is now handled via a runtime registry (`MilitiaBehavior`), preventing save corruption and allowing safe mod toggling.
- **Null-Safety & Stability:** Resolved 200+ C# null-safety warnings (CS86xx) across the codebase. Integrated the `AgentCrashGuard` system to provide an additional layer of protection against runtime failures.
- **BannerlordTestSim Framework:** Introduced a comprehensive simulation and automated testing framework. All core systems now maintain a 100% pass rate across 230+ test scenarios.
- **Architectural Stabilization:** Executed a "Surgical Fix" on `ModLifecycleManager` by integrating the `ISystemInitiable` pattern, enforcing a strict Leaf-to-Root cleanup hierarchy.
- **Fail-Fast Principle:** Removed silent exception swallowing (empty try-catch blocks) from the `EventBus` to expose architectural faults and prevent ghost state corruption.
- `MilitiaHideoutCampaignBehavior` now uses a daily abandoned-hideout replenishment flow.
- `MilitiaRewardCampaignBehavior` now queues player militia bounty payouts for the next daily tick.

### Requirements and Installation

Required mods:

1. `Bannerlord.Harmony`
2. `Bannerlord.UIExtenderEx`
3. `Bannerlord.ButterLib`

Optional mod:

1. `MCMv5`

Load order:

1. `Bannerlord.Harmony`
2. `Bannerlord.UIExtenderEx`
3. `Bannerlord.ButterLib`
4. `MCMv5` (if installed)
5. `BanditMilitias`

Detailed installation: [KURULUM.md](KURULUM.md)

### Project Layout

This repository uses a root-based layout; the active source tree lives directly in the project root, not under `src/BanditMilitias/...`.

- `BanditMilitias.csproj`: main mod project
- `BanditMilitias.Tests/`: test project and test sources
- `Systems/`, `Behaviors/`, `Infrastructure/`, `Intelligence/`: main C# source areas
- `ModuleData/`: XML, localization, and game data
- `tools/`: helper scripts and validation utilities

### Build and Test

```powershell
dotnet build .\BanditMilitias.csproj -c Debug -nologo -v minimal
dotnet build .\BanditMilitias.csproj -c Release -nologo -v minimal
dotnet build .\BanditMilitias.csproj -c Release /p:UseMcm=true
dotnet test .\BanditMilitias.Tests\BanditMilitias.Tests.csproj -v minimal
```

### Runtime TestHub (Alt+~)

You can use the following console commands for advanced diagnostics:
- `bandit.test_list`: Lists all available tests and health checks.
- `bandit.test_run all`: Tests all modules (warm-up, pipeline, warlord logic).
- `bandit.test_report`: Provides a "cold module" analysis and violation summary.

### Encoding Note

Turkish characters may appear broken in some terminal or editor configurations. The README file is written in UTF-8 and should display correctly on GitHub and in modern editors.

### Attribution

This project is an experimental continuation and reinterpretation built on top of the original `BanditMilitias` mod by **JungleDruid**. The WARLORD Edition expands that foundation with broader AI, coordination, and world simulation goals.

**Project lead:** MrNghtoveng  
**AI-assisted development tools:** Antigravity IDE, Codex, Claude
