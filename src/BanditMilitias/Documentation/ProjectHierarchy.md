# Project Folder Structure / Proje Klasör Yapısı

## Türkçe

Bu belge, repodaki ana klasörlerin ne işe yaradığını ve birbirleriyle nasıl ilişki kurduğunu özetler.

---

## English

This document summarizes what each top-level folder in the repository does and how they relate to each other.

---

## Root Level Files / Kök Düzey Dosyalar

| File | TR | EN |
|---|---|---|
| `BanditMilitias.csproj` | Ana mod projesi. Bannerlord referansları, paketler, build seçenekleri ve paketleme hedefleri burada tanımlanır. | The main mod project. Bannerlord references, packages, build options, and packaging targets are defined here. |
| `BanditMilitias.sln` | Mod ve test projelerini birlikte açmak için kullanılan çözüm dosyası. | Solution file used to open both the mod and test projects together. |
| `SubModule.xml` | Bannerlord mod yükleyicisinin okuduğu temel mod tanımı. | The core mod definition read by the Bannerlord mod loader. |
| `KURULUM.md` | Kurulum ve derleme notlarını kısa formatta taşır. | Carries installation and build notes in a short format. |
| `README.md` | Ana kaynak belge — kullanıcıya dönük tam açıklama. | Main source document — full user-facing description. |
| `README.html` | Daha görsel sunum için HTML sürüm. | HTML version for a more visual presentation. |
| `README.txt` | Düz metin sürüm. | Plain text version. |

---

## Source Code Folders / Kaynak Kod Klasörleri

| Folder | TR | EN |
|---|---|---|
| `Core/` | Temel olaylar, ortak veri sözleşmeleri ve yeniden kullanılan çekirdek yapı taşları. | Base events, shared data contracts, and reusable core building blocks. |
| `Infrastructure/` | Save bağlama, uyumluluk, yardımcı servisler ve teknik altyapı katmanı. | Save binding, compatibility, utility services, and technical infrastructure layer. |
| `Components/` | Partilere veya özel veri yapılarına eklenen mod bileşenleri. | Mod components attached to parties or custom data structures. |
| `Behaviors/` | Kampanya davranışları ve oyunun tick döngüsüne bağlanan giriş noktaları. | Campaign behaviors and entry points that hook into the game's tick loop. |
| `Systems/` | Oynanışın büyük kısmını oluşturan modüler sistemler. Spawn, cleanup, progression, economy, diagnostics ve diğer iş akışları burada yaşar. | The modular systems that make up most of the gameplay. Spawn, cleanup, progression, economy, diagnostics, and other workflows live here. |
| `Intelligence/` | Stratejik düşünme, taktik yönlendirme, sürü koordinasyonu, anlatı ve ML odaklı zeka katmanı. | The intelligence layer: strategic reasoning, tactical direction, swarm coordination, narrative, and ML support. |
| `Patches/` | Harmony yamaları ve vanilla davranışa yapılan kontrollü müdahaleler. | Harmony patches and controlled interventions into vanilla behavior. |
| `GUI/` | Oyuncuya bilgi sunan arayüz ve görünür geri bildirim kodu. | Interface and visible feedback code that presents information to the player. |
| `Models/` | Sistemlerin paylaştığı veri taşıyıcı sınıflar ve domain modelleri. | Shared data carrier classes and domain models used across systems. |
| `Debug/` | Geliştirme sırasında kullanılan tanı ve teknik destek araçları. | Diagnostic and technical support tools used during development. |
| `tools/` | Yardımcı geliştirme araçları veya üretim destek dosyaları. | Auxiliary development tools or build support files. |

---

## Content and Distribution / İçerik ve Dağıtım Klasörleri

| Folder | TR | EN |
|---|---|---|
| `ModuleData/` | Bannerlord mod yapısında oyuna kopyalanan veri ve içerik dosyaları. | Data and content files copied into the game as part of the Bannerlord mod structure. |
| `dist/` | Paketleme veya dağıtım için hazırlanan çıktı klasörü. | Output folder prepared for packaging or distribution. |
| `bin/`, `obj/` | Build çıktıları ve derleme ara dosyaları. | Build outputs and compilation intermediate files. |

---

## Tests and Documentation / Test ve Belgeler

| Folder | TR | EN |
|---|---|---|
| `BanditMilitias.Tests/` | Saf mantık, entegrasyon ve doğrulama testleri için masaüstü test projesi. | Desktop test project for pure logic, integration, and documentation validation tests. |
| `Documentation/` | Mimari yapıyı ve sistem ilişkilerini anlatan yardımcı belgeler. | Supporting documents that describe the architecture and system relationships. |

### Documentation Files / Belge Dosyaları

| File | Contents |
|---|---|
| `ProjectHierarchy.md` | This file — folder structure and layer overview |
| `ModuleHierarchy.md` | Module groups and their responsibilities |
| `SystemFlowTree.md` | Data flow and event routing between layers |
| `InGameTestingGuide.md` | Runtime test commands and session workflow |
| `AIArchitecture.md` | AI system overview and layer summary |
| `AI_Assisted_Development.md` | AI tools used during development |

---

## Recommended Reading Order / Önerilen Okuma Sırası

**TR:** Projeyi ilk kez incelerken en verimli sıra genellikle şöyledir:  
**EN:** When exploring the project for the first time, the most efficient order is generally:

1. `README.md`
2. `Documentation/ProjectHierarchy.md` *(this file)*
3. `Documentation/ModuleHierarchy.md`
4. `Documentation/SystemFlowTree.md`
5. `Documentation/InGameTestingGuide.md`
6. `Documentation/AIArchitecture.md`
7. Relevant modules under `Systems/` and `Intelligence/`

This order moves from the public-facing surface, into folder logic, then into data flow, and finally into specific implementation detail.
