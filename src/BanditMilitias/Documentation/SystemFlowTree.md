# System Flow Tree / Sistem Akış Ağacı

## Türkçe

Bu belge, modun ana veri ve olay akışını yüksek seviyede özetler. Amaç, tüm event tiplerini tek tek listelemek değil; modun hangi katmanda düşünüp hangi katmanda harekete geçtiğini görünür kılmaktır.

---

## English

This document summarizes the mod's main data and event flow at a high level. The goal is not to list every event type, but to make visible which layer thinks and which layer acts.

---

## Core Principle / Temel Prensip

**TR:** Proje, mümkün olduğunca olay tabanlı ve modüler ilerler:
- Davranış katmanı oyundan sinyal alır
- Stratejik ve sistem katmanı karar üretir
- Uygulama katmanı bu kararları parti, ekonomi, korku veya cleanup gibi alanlara dağıtır
- Diagnostik katman olan biteni log ve test hub üzerinden görünür kılar

Bu ayrım, tüm iş yükünün tek bir sınıfa yığılmasını önler.

**EN:** The project advances in an event-driven, modular fashion wherever possible:
- The behavior layer receives signals from the game
- The strategic and system layer generates decisions
- The execution layer distributes those decisions to areas like parties, economy, fear, or cleanup
- The diagnostics layer makes what happened visible through logs and the test hub

This separation prevents all workload from being piled into a single class.

---

## Flow 1: Spawn and Initial Registration / Spawn ve İlk Kayıt

**TR:** Yeni bir milis partisinin sisteme girişi tipik olarak şu akıştan geçer. Bu aşamada amaç yalnızca parti doğurmak değildir — yeni partinin sistem tarafından izlenebilir ve yönetilebilir hale gelmesi gerekir.

**EN:** A new militia party's entry into the system typically follows this flow. The goal at this stage is not only to spawn a party — the new party must also become trackable and manageable by the system.

```
MilitiaBehavior / Tick
    └── Spawning Evaluation
            └── MilitiaSpawningSystem
                    └── Militia Party Created
                            ├── EventBus
                            │       ├── Tracking / Fear / Progression
                            │       └── Diagnostics
```

---

## Flow 2: Strategic Decision to Field Action / Stratejik Karardan Saha Eylemine

**TR:** Yüksek seviyeli kararlar, doğrudan tek bir emir yerine birkaç katmandan geçerek uygulanır. Bu hatta özellikle `BanditBrain` (genel baskı ve yön tayini), `Progression` sistemleri (warlord bağlamı) ve `AISchedulerSystem` (kararları zamana yayma) kritik rol oynar.

**EN:** High-level decisions are applied through several layers rather than as a single direct command. On this path, `BanditBrain` (global pressure and direction), `Progression` systems (warlord context), and `AISchedulerSystem` (time-distributing decisions) play critical roles.

```
BanditBrain
    └── Strategic State
            └── Warlord / Progression Context
                    └── Tactical Decision Rules
                            └── AISchedulerSystem
                                    └── Militia Behavior Execution
                                            (patrol / merge / recruit / flee / swarm)
```

---

## Flow 3: Post-Combat Effects / Savaş Sonrası Etki

**TR:** Bir savaşın etkisi yalnızca kazanana ve kaybedeğe yazılmaz. Korku, ekonomi, veri toplama ve progression gibi başka hatlara da akar. Önemli nokta: tek bir savaş sonucunun birden fazla alt sistemi aynı anda beslemesidir.

**EN:** The effects of a battle are not written only to the winner and loser. They also flow into other tracks such as fear, economy, data collection, and progression. The key design point is that a single combat result feeds multiple subsystems simultaneously.

```
Combat Result
    ├── Reward / Loss
    ├── Fear Update
    ├── ML and Decision Telemetry
    └── Progression Checks
            └── Warlord Promotion or Career Updates
```

---

## Flow 4: Cleanup and World Stabilization / Temizlik ve Dünya Kararlılığı

**TR:** Uzun süreli testlerde dünya yükü arttıkça cleanup hattı devreye girer. Bu akışın hedefi, zayıf veya bozulmuş partileri azaltmak, gereksiz dünya yükünü düşürmek ve uzun oturumlarda performansın tamamen dağılmasını önlemektir.

**EN:** As world load increases during extended sessions, the cleanup path activates. The goal of this flow is to reduce weak or broken parties, lower unnecessary world load, and prevent performance from degrading completely over long sessions.

```
World Growth
    └── Tracking
            └── Cleanup Evaluation
                    └── Merge / Remove / Consolidate
                            ├── World State Stabilization
                            └── Diagnostics and Logs
```

---

## Runtime Diagnostics Flow / Runtime Diagnostics Akışı

**TR:** Projede klasik log yaklaşımına ek olarak oyun içi test ve tanılama akışı da vardır. Temel komutlar şu tür sorunları görünür kılmak için kullanılır: `ghost`, `dead`, `stale`, `event leak`, `cold module`. Bu sayede yalnızca log okumak yerine oyun çalışırken doğrudan durum raporu alınabilir.

**EN:** In addition to the classic logging approach, the project also has an in-game test and diagnostics flow. Core commands are used to surface problems such as: `ghost`, `dead`, `stale`, `event leak`, `cold module`. This allows getting a direct status report while the game is running, rather than only reading log files.

```
bandit.test_list
bandit.test_run all
bandit.test_report
militia.system_status
militia.watchdog_check
```

---

## Layer Relationship Summary / Katmanlar Arası İlişki Özeti

| Step | Layer | Action |
|---|---|---|
| 1 | Game | Produces signals (ticks, events, combat results) |
| 2 | Behavior & Tracking | Collects those signals |
| 3 | Intelligence & Systems | Generates decisions |
| 4 | Scheduler & Execution | Applies those decisions |
| 5 | Diagnostics | Makes the results visible |

---

## Why This Matters / Neden Önemli?

**TR:** Bu yapı, Bandit Militias'ı yalnızca yeni parti üreten bir moddan daha fazlası haline getirir. Asıl amaç: uzun oturumlarda veri toplayabilmek, sorunları izole edebilmek ve davranış, ekonomi, performans katmanlarını ayrı ayrı geliştirebilmektir. Sistem akışı, modun hem oynanış tarafını hem de geliştirilebilirliğini belirleyen ana omurgadır.

**EN:** This architecture makes Bandit Militias more than a mod that simply spawns new parties. The real goals are: to collect data over long sessions, to isolate problems, and to develop the behavior, economy, and performance layers independently. The system flow is the backbone that determines both the gameplay side of the mod and its long-term maintainability.
