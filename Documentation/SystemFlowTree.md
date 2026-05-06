# Bandit Militias: WARLORD - Sistem Akışı

Bu belge, modun ana veri ve olay akışını yüksek seviyede özetler.

## 1. Aktivasyon ve Başlatma Kapısı

Modun tüm olay zinciri oyun açıldığı anda başlamaz. Ağır sistemler önce ertelemeli (deferred) aşamaya alınır, ardından bootstrap tamamlanınca `EventBus` tam olarak aktifleşir.

```mermaid
graph TD
    A[OnSubModuleLoad] --> B[EventBus.Clear]
    B --> C[RegisterModules]
    C --> D[Deferred Init Waiting]
    D --> E[ModuleManager.InitializeAll]
    E --> F[RegisterCampaignEvents]
    F --> G[WorldMemory.Build]
    G --> H[Session Bootstrap Complete]
    H --> I[EventBus Active]
```

## 2. Karar Akışı: Hibrid AI Karar Zinciri

Yüksek seviyeli kararlar artık hibrid (Heuristic + Neural) bir hiyerarşiden geçerek uygulanır.

```mermaid
graph TD
    A[BanditBrain: Stratejik Hedef] --> B[Warlord / Career Context]
    B --> C{MilitiaDecider: Heuristic Rules}
    C --> D{Neural Advisor: Confidence Check}
    D -- "High Confidence" --> E[Blend: Neural + Heuristic]
    D -- "Low Confidence / Low Tier" --> F[Pure Heuristic Outcome]
    E --> G[AIDecisionLogger & SmartCache]
    F --> G
    G --> H[AISchedulerSystem: Queued Execution]
```

### Karar Katmanları:
- **Heuristic**: `MilitiaDecider` içindeki role-bazlı (Guardian, Raider vb.) kurallar.
- **Neural Advisor**: Saf C# ağı üzerinden gelen olasılık tavsiyesi.
- **SmartCache**: Performans için benzer kararların kısa süreli önbelleğe alınması.
- **AIScheduler**: Kararların zamana yayılarak CPU yükünün dengelenmesi.

## 3. Savaş Sonrası ve Geri Bildirim

Bir savaşın etkisi birden fazla sisteme paralel olarak akar:

```mermaid
graph TD
    A[Combat Result] --> B[Reward / Loss Calculation]
    B --> C[Fear & Pressure Update]
    B --> D[Neural Experience Tracking]
    B --> E[Warlord Progression / Legitimacy]
    B --> F[AIDecisionLogger: Telemetry Export]
```

## 4. Dünya Hafızası (WorldMemory) Akışı

Karar vericiler veriyi doğrudan oyun motorundan çekmek yerine üç katmanlı hafızadan beslenir:

- **Bedrock (Katman 1)**: Statik coğrafya, yerleşim yerleri ve kNN (en yakın komşu) grafiği.
- **Geology (Katman 2)**: Bölgesel refah, sahiplik ve ekonomik güç verileri (7 günlük periyot).
- **Weather (Katman 3)**: Hareketli kervanlar, lord partileri ve anlık yoğunluk (6 saatlik periyot).

## 5. Temizlik ve Performans (Cleanup)

```mermaid
graph LR
    A[World Density Increase] --> B[Cleanup Evaluation]
    B --> C[Identify Zombie/Headless Parties]
    C --> D[Merge or Remove]
    D --> E[Diagnostics & Performance Metrics]
```

---
*Son Güncelleme: Nisan 2026*

## Module ID Baglanti Agaci (Guncel)
Detayli modul-id seviyesinde baglanti agaci icin:
- `Documentation/ModuleConnectionTree_TR.md`
- `Documentation/Module_ID_Assessment_TR.md`
- `ModuleCodeExports/ModuleID_*.txt`
