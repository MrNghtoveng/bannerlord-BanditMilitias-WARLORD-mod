# Bandit Militias: WARLORD - Modül Hiyerarşisi

Bu belge, projedeki ana sistem gruplarını ve bunların sorumluluklarını güncel (v1.3.15) haliyle özetler.

## 1. Intelligence (Karar Katmanı)

Yüksek seviyeli karar üretimi ve hibrid AI mantığı burada toplanır.

- `Strategic/`: `BanditBrain` üzerinden küresel hedef üretimi ve `WarlordSystem` ile rütbe yönetimi.
- `Neural/`: Heuristic sisteme paralel çalışan, hafif siklet Nöral Danışman (Neural Advisor).
- `Tactical/`: HTN (Hierarchical Task Network) planlayıcı ile görev bazlı hareket kontrolü.
- `Swarm/`: Birden fazla grubun sürü (swarm) psikolojisi ile hareket koordinasyonu.
- `AI/`: `MilitiaDecider` ile role-bazlı (Guardian, Raider, Captain) taktiksel karar kuralları.
- `Narrative/`: Warlord kimliği ve tematik anlatı desteği.

## 2. Systems (Oynanış Mekanikleri)

Modun ana iş yükünü taşıyan modüler servisler.

### Operasyonel Katman
- `Scheduling/`: `AISchedulerSystem` ile AI kararlarını zamana yayıp CPU yükünü optimize eder.
- `Spawning/`: Dinamik milis oluşumu ve sığınak (hideout) yönetimi.
- `Cleanup/`: Performans için geçersiz veya "zombi" partilerin temizlenmesi.
- `Grid/`: `SpatialGridSystem` ile hızlı çevre taraması.

### Etkileşim ve Baskı Katmanı
- `Fear/` & `Bounty/`: Dinamik korku yayılımı ve kelle avcısı sistemleri.
- `Progression/`: Warlord kariyer gelişimi ve meşruiyet (Legitimacy) kuralları.
- `Diplomacy/`: Karşılıklı etkileşimler, harçlar ve düellolar.
- `Crisis/`: Sistemik kırılma anları ve büyük kaos olayları.

## 3. Core & Infrastructure (Altyapı)

Sistemin temelini oluşturan ve motorla konuşan katmanlar.

- `Core/Memory/`: **WorldMemory** (Bedrock, Geology, Weather) ile üç katmanlı dünya verisi sağlayan bellek sistemi.
- `Core/Events/`: `EventBus` üzerinden merkezi iletişim ve olay yönetimi.
- `Infrastructure/`: `CompatibilityLayer` ile Steam/Epic ve Bannerlord sürüm uyumluluğu.
- `Infrastructure/ModuleManager`: Tüm modüllerin (`MilitiaModuleBase`) başlatılması ve temizlenmesi (Cleanup).

## 4. Integration & UI

- `Behaviors/`: Bannerlord kampanya döngüsüne (Tick, Daily, Hourly) bağlantı noktaları.
- `Patches/`: Harmony ile motor düzeyinde yapılan müdahaleler (Hız, AI intercept vb.).
- `GUI/`: Gauntlet UI ve ViewModel tabanlı bilgilendirme ekranları.

---
*Son Güncelleme: Nisan 2026*
