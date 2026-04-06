# Module Hierarchy / Modül Hiyerarşisi

## Türkçe

Bu belge, projedeki ana sistem gruplarını ve sorumluluklarını özetler. Amaç tüm dosyaları tek tek listelemek değil, modun hangi iş yüklerini hangi katmanlara böldüğünü anlaşılır hale getirmektir.

---

## English

This document summarizes the main system groups in the project and their responsibilities. The goal is not to list every file, but to make clear how the mod divides its workloads across layers.

---

## 1. Intelligence

**Türkçe:** Bu katman yüksek seviyeli karar üretimi ve davranış yönlendirmesiyle ilgilenir.

**English:** This layer handles high-level decision generation and behavior direction.

| Folder | TR | EN |
|---|---|---|
| `Strategic/` | Küresel hedef üretimi, duruş değişimleri ve baskın stratejileri | Global target generation, posture shifts, and dominant strategies |
| `Tactical/` | Sahaya yakın karar kuralları ve anlık tepki mekanikleri | Near-field decision rules and immediate reaction mechanics |
| `Swarm/` | Birden fazla grubun koordinasyon mantığı | Multi-party coordination logic |
| `ML/` | Veri toplama, karar telemetrisi ve öğrenme desteği | Data collection, decision telemetry, and learning support |
| `Narrative/` | Warlord kimliği, kişilik ve tematik anlatı desteği | Warlord identity, personality, and thematic narrative support |
| `AI/`, `Logging/` | Davranışa yakın yardımcı bileşenler ve tanı akışı | Behavior-adjacent helpers and diagnostic flow |

---

## 2. Systems

**Türkçe:** Projede en yoğun iş yükü burada toplanır. `Systems` klasörü, doğrudan oynanışı etkileyen modülleri barındırır.

**English:** The heaviest workload in the project lives here. The `Systems` folder contains the modules that directly affect gameplay.

### Core Flow / Çekirdek Akış

| Folder | TR | EN |
|---|---|---|
| `Spawning/` | Yeni milis partilerinin oluşumu, koşulları ve ilk yapılandırması | New militia party creation, conditions, and initial configuration |
| `Scheduling/` | AI işlemlerini sıraya koyup zamana yayma | Queuing and time-distributing AI operations |
| `Cleanup/` | Zayıf, bozulmuş veya gereksiz parti yükünü azaltma | Reducing weak, broken, or surplus party load |
| `Grid/` | Uzamsal sorgular ve komşuluk hesapları | Spatial queries and neighborhood calculations |
| `Tracking/` | Dünya olayları, oyuncu etkisi ve hareket verilerini izleme | Tracking world events, player influence, and movement data |

### Gameplay and Pressure / Oynanış ve Baskı Katmanı

| Folder | TR | EN |
|---|---|---|
| `Raiding/` | Baskın ve yağma davranışları | Raid and pillage behaviors |
| `Fear/` | Korku, ihanet, panik ve moral etkileri | Fear, betrayal, panic, and morale effects |
| `Combat/` | Savaş sonucu ve çatışma bağlantılı destek mantığı | Combat outcome and conflict-related support logic |
| `Territory/` | Bölgesel etki ve alan baskısı | Regional influence and area pressure |
| `Diplomacy/` | Düello, harç ve ilişki tabanlı etkileşimler | Duels, tribute, and relationship-based interactions |
| `Bounty/` | Ödül ve hedef takibi | Bounty and target tracking |

### Progression and World Persistence / İlerleme ve Dünya Kalıcılığı

| Folder | TR | EN |
|---|---|---|
| `Progression/` | Warlord kariyeri, meşruiyet, halefiyet ve yükselme kuralları | Warlord career, legitimacy, succession, and ascension rules |
| `Economy/` | Gelir, gider, kaynak ve uzun vadeli büyüme | Income, expenses, resources, and long-term growth |
| `Logistics/` | Taşıma, destek ve operasyonel sürdürülebilirlik | Supply, support, and operational sustainability |
| `Workshop/` | Altyapı veya üretim odaklı genişleme mantıkları | Infrastructure and production-focused expansion |
| `Legacy/` | Ölüm sonrası miras ve devamlılık davranışları | Post-death inheritance and continuity behaviors |

### Support Layers / Destekleyici Katmanlar

| Folder | TR | EN |
|---|---|---|
| `Diagnostics/` | Oyun içi test hub ve teknik gözlem araçları | In-game test hub and technical observation tools |
| `Dev/` | Geliştirme sürecinde kullanılan yardımcı akışlar | Utility flows used during development |
| `Events/` | Sistemler arası olay yönlendirmeleri | Cross-system event routing |
| `Enhancement/` | Ekipman ve güçlendirme tabanlı destekler | Equipment and upgrade-based supports |
| `Seasonal/` | Dönemsel veya zamana bağlı kurallar | Seasonal or time-bound rules |
| `Crisis/` | Kaos, baskı ve sistemik kırılma anları | Chaos, pressure spikes, and systemic break points |

---

## 3. Infrastructure

**TR:** Bu katman oyun motoruyla daha teknik seviyede konuşur.  
**EN:** This layer communicates with the game engine at a more technical level.

- Save binding and registry helpers
- Compatibility and protection layers
- Optional mod integrations
- MCM connections
- Shared infrastructure utilities

`CompatibilityLayer` in particular makes it easier to behave correctly alongside external mods or protection systems.

---

## 4. Core

**TR:** `Core/` klasörü olaylar, temel sözleşmeler ve tekrar kullanılan yapı taşları için vardır. Üst katmanlar mümkün olduğunca bu çekirdek sözleşmeler üzerinden iletişim kurar.

**EN:** `Core/` exists for events, base contracts, and reusable building blocks. Upper layers communicate through these core contracts wherever possible.

---

## 5. Components

**TR:** `Components/`, partilere eklenen mod verilerini barındırır. En kritik örnek, bir partinin Bandit Militias sistemi tarafından yönetildiğini belirleyen bileşenlerdir.

**EN:** `Components/` holds mod data attached to parties. The most critical example is the component that marks a party as managed by the Bandit Militias system.

---

## 6. Behaviors

**TR:** `Behaviors/`, Bannerlord kampanya döngüsüne bağlanan davranış sınıflarını içerir. Bu katman, modüllerin oyunun tick akışına doğru zamanda bağlanmasını sağlar.

**EN:** `Behaviors/` contains the behavior classes that hook into Bannerlord's campaign loop. This layer ensures modules attach to the game's tick flow at the right time.

---

## 7. Patches

**TR:** `Patches/`, Harmony ile yapılan motor düzeyi müdahaleleri içerir. Bu kodlar genelde vanilla davranışını bastırmak, uyumluluk sağlamak veya çökme riskini düşürmek için kullanılır.

**EN:** `Patches/` contains engine-level interventions made with Harmony. This code is generally used to suppress vanilla behavior, ensure compatibility, or reduce crash risk.

---

## 8. GUI

**TR:** `GUI/`, oyuncuya bilgi sunan arayüz elemanları ve görünür geri bildirim akışları için ayrılmıştır.

**EN:** `GUI/` is reserved for UI elements that present information to the player and visible feedback flows.

---

## 9. Tests and Documentation

**TR:** `BanditMilitias.Tests/` saf mantık ve entegrasyon testleri için kullanılır. `Documentation/` mimari, klasör yapısı ve sistem akışı notlarını içerir. Bu ayrım sayesinde proje hem oyun içi runtime testlerle hem de masaüstü testleriyle doğrulanabilir bir yapıda tutulur.

**EN:** `BanditMilitias.Tests/` is used for pure logic and integration tests. `Documentation/` contains architecture, folder structure, and system flow notes. This separation keeps the project verifiable both through in-game runtime tests and through desktop tests.
