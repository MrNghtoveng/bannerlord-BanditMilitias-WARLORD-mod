# Bandit Militias: WARLORD - Sistem Akışı

Bu belge, modun ana veri ve olay akışını yüksek seviyede özetler. Buradaki amaç tek tek tüm event tiplerini listelemek değil; modun hangi katmanda düşünüp hangi katmanda harekete geçtiğini görünür kılmaktır.

## Temel Prensip

Proje, mümkün olduğunca olay tabanlı ve modüler ilerler.

* davranış katmanı oyundan sinyal alır
* stratejik ve sistem katmanı karar üretir
* uygulama katmanı parti, ekonomi, korku veya cleanup gibi alanlara bu kararları dağıtır
* diagnostik katman olan biteni log ve test hub üzerinden görünür kılar

Bu ayrım, tüm işin tek bir sınıfa yığılmasını önler.

## Aktivasyon ve EventBus Kapısı

Modun tüm olay zinciri oyun açıldığı anda tam güçte başlamaz. Ağır sistemler önce deferred init aşamasına alınır, ardından session bootstrap tamamlanınca EventBus bağlantıları tam olarak canlı hale gelir.

```mermaid
graph TD
    A\[OnSubModuleLoad] --> B\[EventBus.Clear]
    B --> C\[RegisterModules]
    C --> D\[Deferred Init Waiting]
    D --> E\[ModuleManager.InitializeAll]
    E --> F\[RegisterCampaignEvents]
    F --> G\[Session Bootstrap Complete]
    G --> H\[OnApplicationTick -> EventBus.ProcessQueue]
```

Bu kapı mekanizması iki sebeple önemlidir:

* erken kampanya yüklenme anında hatalı event dispatch riskini azaltır
* hata fırtınası veya emergency stop halinde kuyruk güvenli biçimde boşaltılabilir

## Ana Akış 1: Spawn ve İlk Kayıt

Yeni bir milis partisinin sisteme girişi tipik olarak şu akıştan geçer:

```mermaid
graph LR
    A\[MilitiaBehavior / Tick] --> B\[Spawning Evaluation]
    B --> C\[MilitiaSpawningSystem]
    C --> D\[Militia Party Created]
    D --> E\[EventBus]
    E --> F\[Tracking / Fear / Progression]
    E --> G\[Diagnostics]
```

Bu aşamada amaç sadece parti doğurmak değildir. Yeni partinin sistem tarafından izlenebilir ve yönetilebilir hale gelmesi gerekir.

Not: `MilitiaSpawnedEvent` bu hatta anlık yayınlanır; deferred queue daha çok ertelenebilir fırsat ve takip olayları için kullanılır.

## Ana Akış 2: Strateji ve Saha Kararı

Yüksek seviyeli kararlar, doğrudan tek bir emir yerine birkaç katmandan geçerek uygulanır.

```mermaid
graph TD
    A\[BanditBrain] --> B\[Strategic State]
    B --> C\[Warlord / Progression Context]
    C --> D\[Tactical Decision Rules]
    D --> E\[AISchedulerSystem]
    E --> F\[Militia Behavior Execution]
```

Bu hatta özellikle şu katmanlar önemlidir:

* `BanditBrain`: genel baskı ve yön tayini
* `Progression` sistemleri: partinin veya warlord hattının bağlamı
* taktik karar kuralları: kaç, saldır, birleş, devriye gibi alan kararları
* `AISchedulerSystem`: kararları zamana yayma

Görev bazlı taktik zincir artık görev önkoşullarını gerçekten kontrol eder. Böylece ambush, Turan ve savunmacı shield-wall geçişleri yalnızca uygun world-state oluştuğunda çalışır.

## Ana Akış 3: Savaş Sonrası Etki

Bir savaşın etkisi sadece kazanan ve kaybedene yazılmaz. Korku, ekonomi, veri toplama ve progression gibi başka hatlara da akar.

```mermaid
graph TD
    A\[Combat Result] --> B\[Reward / Loss]
    A --> C\[Fear Update]
    A --> D\[ML and Decision Telemetry]
    A --> E\[Progression Checks]
    E --> F\[Warlord Promotion or Career Updates]
```

Burada sistem tasarımı açısından önemli nokta, tek bir savaş sonucunun birden fazla alt sistemi aynı anda beslemesidir.

## Ana Akış 4: Temizlik ve Kararlılık

Uzun süreli testlerde dünya yükü arttıkça cleanup hattı devreye girer.

```mermaid
graph LR
    A\[World Growth] --> B\[Tracking]
    B --> C\[Cleanup Evaluation]
    C --> D\[Merge / Remove / Consolidate]
    D --> E\[World State Stabilization]
    D --> F\[Diagnostics and Logs]
```

Bu akışın hedefi:

* zayıf veya bozulmuş partileri azaltmak
* gereksiz dünya yükünü düşürmek
* uzun oturumlarda performansın tamamen dağılmasını önlemek

## Runtime Diagnostics Akışı

Projede klasik log yaklaşımına ek olarak oyun içi test ve tanılama akışı da vardır.

Temel komutlar:

* `bandit.test\_list`
* `bandit.test\_run all`
* `bandit.test\_report`

Bu komutlar, runtime test hub üzerinden şu tip sorunları görünür kılmak için kullanılır:

* ghost
* dead
* stale
* event leak
* cold module

Bu sayede sadece log okumak yerine oyun çalışırken doğrudan durum raporu alınabilir.

## Katmanlar Arası İlişki Özeti

En sade haliyle veri akışı şu düşünceyle okunabilir:

1. Oyun sinyal üretir.
2. Behavior ve tracking katmanı bu sinyali toplar.
3. Intelligence ve system katmanları karar üretir.
4. Scheduler ve execution katmanları bu kararı uygular.
5. Diagnostics katmanı sonucu görünür kılar.

## Neden Önemli?

Bu yapı, Bandit Militias'ı sadece yeni parti üreten bir mod olmaktan çıkarır. Asıl amaç:

* uzun oturumlarda veri toplayabilmek
* sorunları izole edebilmek
* davranış, ekonomi ve performans katmanlarını ayrı ayrı geliştirebilmek

Yani sistem akışı, modun hem oynanış tarafını hem de geliştirilebilirliğini belirleyen ana omurgadır.

