# AI Architecture

## Türkçe

Bu belge, `Bandit Militias: WARLORD Edition` içinde kullanılan yapay zeka ile ilgili sistemleri özetler.

### Temel Fikir

Mod, tek bir yapay zeka modeli kullanmaz. Bunun yerine birbirine bağlı katmanlı sistemler bir araya getirilir:

- stratejik akıl yürütme
- taktik karar kuralları
- zamanlayıcı tabanlı yürütme
- telemetri ve öğrenme desteği
- dünya bağlamı ve baskı sistemleri
- Bannerlord AI akışına yama tabanlı entegrasyon

### Sistem Listesi

| Katman | Sistem | Rol |
|---|---|---|
| Stratejik | `BanditBrain` | Global baskı değerlendirmesi ve yön tayini |
| Stratejik | `WarlordSystem` | Warlord yaşam döngüsü ve tehdit projeksiyonu |
| Stratejik | `HTNEngine` | Hiyerarşik görev ağı planlaması |
| Stratejik | `AILearningSystem` | Telemetri destekli uyarlanma |
| Operasyonel | `MilitiaDecider` | Stratejik niyeti somut parti eylemlerine dönüştürür |
| Operasyonel | `AISchedulerSystem` | AI işlemlerini kuyruğa alır ve zamana yayar |
| Operasyonel | `SwarmCoordinator` | Çoklu parti koordinasyonu |
| Operasyonel | `AdaptiveAIDoctrineSystem` | Bağlama göre doktrin değişimi |
| Dünya | `SpatialGridSystem` | Uzamsal sorgular ve komşuluk hesapları |
| Dünya | `PartyCleanupSystem` | Bozulmuş ya da gereksiz partilerin temizlenmesi |
| Dünya | `PlayerTracker` | Oyuncu aktivitesi ve etki takibi |
| Dünya | `FearSystem` | Korku yayılımı, moral ve panik durumları |
| Entegrasyon | Harmony patches | Motor düzeyinde vanilla AI akışına müdahale |

### Özet

Bu projede "yapay zeka", kural tabanlı sistemlerin, zamanlayıcıların, bağlam takibinin, telemetri destekli ayarlamanın ve dünya simülasyonu geri bildiriminin birleşik davranışını ifade eder.

Tek bir makine öğrenmesi sistemi değil, **hibrit yapay zeka mimarisi** olarak tanımlanır.

---

## English

This document summarizes the AI-related systems used inside `Bandit Militias: WARLORD Edition`.

### Core Idea

The mod does not use a single AI model. Instead, it combines interconnected layered systems:

- strategic reasoning
- tactical decision rules
- scheduler-driven execution
- telemetry and learning support
- world-context and pressure systems
- patch-based integration into the Bannerlord AI flow

### System List

| Layer | System | Role |
|---|---|---|
| Strategic | `BanditBrain` | Global pressure evaluation and directional intent |
| Strategic | `WarlordSystem` | Warlord lifecycle and threat projection |
| Strategic | `HTNEngine` | Hierarchical Task Network planning |
| Strategic | `AILearningSystem` | Telemetry-backed tuning and adaptation |
| Operational | `MilitiaDecider` | Converts strategic intent into concrete party actions |
| Operational | `AISchedulerSystem` | Queues and time-distributes AI operations |
| Operational | `SwarmCoordinator` | Multi-party coordination logic |
| Operational | `AdaptiveAIDoctrineSystem` | Runtime doctrine switching based on context |
| World | `SpatialGridSystem` | Spatial queries and neighborhood calculations |
| World | `PartyCleanupSystem` | Removes broken or surplus parties |
| World | `PlayerTracker` | Player activity and influence tracking |
| World | `FearSystem` | Fear propagation, morale, and panic states |
| Integration | Harmony patches | Engine-level interception of vanilla AI flow |

### Summary

In this project, "AI" means the combined behavior of rule-based systems, scheduling, context tracking, telemetry-backed tuning, and world simulation feedback.

It is best described as a **hybrid AI architecture** rather than a single machine learning system.
