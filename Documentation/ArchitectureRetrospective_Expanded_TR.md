# BanditMilitias - WARLORD Edition
## Mimari Retrospektif Ek Dokumani (System Card + Silent Catch)

Tarih: 2026-04-25
Kapsam: BanditMilitias, AgentCrashGuard, BannerlordTestSim kaynak kodu

## 1) System Card Standardi

Her kart ayni formatta tutulur:
- Sorumluluk
- Girdi/Cikti
- Bagimliliklar
- Hata Sinyali
- Sahiplik Durumu

Durum sozlugu:
- Aktif: Uretim akisinda kritik veya aktif kullanilan
- Pasif: Kosullu/gelistirme odakli veya kisitli kullanim
- Pasif/Kosullu: Gecis veya yedek amacli, varsayilan olarak aktif akista olmayan

## 2) System Cards

### Card 01 - AI
- Sorumluluk: Doktrin adaptasyonu, ekipman dagitimi, AI karar kalibrasyonu.
- Girdi/Cikti: Kampanya tehdidi, parti durumu -> karar agirliklari, ekipman aksiyonlari.
- Bagimliliklar: Settings, SpatialGridSystem, Warlord/Progression, EventBus.
- Hata Sinyali: Karar osilasyonu, asiri agresif/pasif davranis, tick sure artisi.
- Sahiplik Durumu: Aktif.

### Card 02 - Behavior
- Sorumluluk: Warlord davranisinin kampanya eventlerine baglanmasi.
- Girdi/Cikti: CampaignEvents -> davranis tetikleme, state gecisleri.
- Bagimliliklar: ModuleManager, CompatibilityLayer, Core.Events.
- Hata Sinyali: Session launch cikislari, event leak, davranis cagrilmama.
- Sahiplik Durumu: Aktif.

### Card 03 - Bounty
- Sorumluluk: Odul/infamy tarafi takip ve odul ekonomisi etkisi.
- Girdi/Cikti: Kill/raid olaylari -> bounty artisi/azalisi.
- Bagimliliklar: Diplomacy, Progression, Tracking.
- Hata Sinyali: Bounty sifirlanmama, tutarsiz cezalandirma.
- Sahiplik Durumu: Aktif.

### Card 04 - Cleanup
- Sorumluluk: Parti yasam dongusu temizligi, zombi parti avlama, uninstall cleanup.
- Girdi/Cikti: Parti olumu/yok olma eventleri -> temizleme, unregister.
- Bagimliliklar: ModuleManager, Warlord systems, Campaign parties.
- Hata Sinyali: Haritada headless/zombi parti birikmesi, state sizintisi.
- Sahiplik Durumu: Aktif (P1 guvenilirlik modulu).

### Card 05 - Combat
- Sorumluluk: Morale ve catisma etkilerinin dengelenmesi.
- Girdi/Cikti: Battle/map eventleri -> morale puani, combat modifier etkisi.
- Bagimliliklar: Settings, Tracking, Progression.
- Hata Sinyali: Morale lock, beklenmedik toplu kacis.
- Sahiplik Durumu: Aktif.

### Card 06 - Crisis
- Sorumluluk: Kriz olaylari ve acil durum tetikleri.
- Girdi/Cikti: Global baski/senaryo durumlari -> kriz olayi dogurma.
- Bagimliliklar: EventBus, Economy/Spawning, Diagnostics.
- Hata Sinyali: Kriz event spam veya hic tetiklenmeme.
- Sahiplik Durumu: Aktif (ancak catch sertlestirme gerekli).

### Card 07 - Dev
- Sorumluluk: Gelistirme veri toplama, simulation csv, perf izleme.
- Girdi/Cikti: Event akisleri -> rapor/csv/telemetry.
- Bagimliliklar: File IO, Diagnostics, TestHub.
- Hata Sinyali: IO hatalari, log kanalinda sessiz yutma.
- Sahiplik Durumu: Pasif (DevMode bagimli), refactor adayi.

### Card 08 - Diagnostics
- Sorumluluk: In-game saglik kontrolleri, assertion, modul sagligi.
- Girdi/Cikti: Runtime state -> assertion/log/uyari.
- Bagimliliklar: ModuleRegistry, FileLogger, DebugLogger.
- Hata Sinyali: Assertion false-positive, warmup catch yutma.
- Sahiplik Durumu: Aktif (uretimi destekleyen gozlem katmani).

### Card 09 - Diplomacy
- Sorumluluk: Warlord politikasi, extortion, propaganda, duel.
- Girdi/Cikti: Warlord/settlement olaylari -> itibar, iliski, gelir etkisi.
- Bagimliliklar: Bounty, Progression, Economy.
- Hata Sinyali: Diplomasi eylemlerinin tek yone kilitlenmesi.
- Sahiplik Durumu: Aktif.

### Card 10 - Economy
- Sorumluluk: Black market, caravan tax, warlord gelir-gider akisi.
- Girdi/Cikti: Ticaret/harac/ganimet -> Warlord.Gold ve party ticari altin etkisi.
- Bagimliliklar: WarlordProgression, Upgrade, Trade hooks.
- Hata Sinyali: Gold var ama upgrade yok, ekonomi kanallari desenkron.
- Sahiplik Durumu: Aktif (P1 senkronizasyon adayi).

### Card 11 - Enhancement
- Sorumluluk: Birim ve sistem guclendirme kurallari.
- Girdi/Cikti: Progression state -> bonus/multiplier.
- Bagimliliklar: Progression, Combat, Settings.
- Hata Sinyali: Denge bozulmasi, cap disi buff birikimi.
- Sahiplik Durumu: Aktif.

### Card 12 - Events
- Sorumluluk: Ozel gameplay eventleri (orn. jailbreak mission akislari).
- Girdi/Cikti: Trigger state -> event baslatma/sonlandirma.
- Bagimliliklar: CampaignEvents, Behavior, Tracking.
- Hata Sinyali: Event orphan kalmasi, cift tetik.
- Sahiplik Durumu: Aktif.

### Card 13 - Fear
- Sorumluluk: Bolgesel korku/tehdit metri gi ve davranis etkisi.
- Girdi/Cikti: Raid/katliam/kayip sinyalleri -> fear index, karar etkisi.
- Bagimliliklar: Territory, Progression, Bounty.
- Hata Sinyali: Fear stale kalmasi veya asiri dalgalanma.
- Sahiplik Durumu: Aktif (ilk tickten once pasif initialize davranisi var).

### Card 14 - Grid
- Sorumluluk: Mekansal indeksleme, spatial sorgular ve yakinlik karar hizi.
- Girdi/Cikti: Parti konumlari -> spatial buckets/sorgu sonuclari.
- Bagimliliklar: Campaign map, AI, Tracking.
- Hata Sinyali: Yanlis komsuluk, performans dususu.
- Sahiplik Durumu: Aktif.

### Card 15 - Heroics
- Sorumluluk: Klasor var, aktif sistem dosyasi yok.
- Girdi/Cikti: N/A.
- Bagimliliklar: N/A.
- Hata Sinyali: Mimari gürültu ve dokumantasyon yanilsamasi.
- Sahiplik Durumu: Pasif/Kosullu (net kapsam tanimi ile izlenmeli).

### Card 16 - Legacy
- Sorumluluk: Geriye donuk uyumluluk ve gecis davranislari.
- Girdi/Cikti: Eski save/state -> normalize edilmis yeni state.
- Bagimliliklar: CompatibilityLayer, Core.Config.
- Hata Sinyali: Migration bug, tekrarlanan fallback.
- Sahiplik Durumu: Pasif/Aktif arasi (stabilizasyonda sadeleme adayi).

### Card 17 - Logistics
- Sorumluluk: Warlord ikmal, dagitim ve hat/rota surecleri.
- Girdi/Cikti: Kaynak stoklari -> besleme, transfer, kapasite.
- Bagimliliklar: Economy, Territory, Spawning.
- Hata Sinyali: Tedarik kilidi, parti acikta kalma.
- Sahiplik Durumu: Aktif.

### Card 18 - Progression
- Sorumluluk: Ascension, legitimacy, succession, troop/warlord gelisimi.
- Girdi/Cikti: Performans/olay metrikleri -> tier/promotion/upgrades.
- Bagimliliklar: Fear, Bounty, Economy, Cleanup.
- Hata Sinyali: Promotion tikanmasi, upgrade zinciri kirilmasi.
- Sahiplik Durumu: Karmasik:
  - Aktif: AscensionEvaluator, MilitiaProgressionSystem, WarlordProgression, WarlordSuccession
  - Pasif/Kosullu: MilitiaUpgradeSystem, TroopProgressionSystem (acikca IsEnabled=false)

### Card 19 - Raiding
- Sorumluluk: Baskin davranisi ve ganimet akisi.
- Girdi/Cikti: Hedef secimi -> raid sonucu, fear/economy etkisi.
- Bagimliliklar: AI, Fear, Economy, Tracking.
- Hata Sinyali: Raid spam veya raid hic cikmama.
- Sahiplik Durumu: Aktif.

### Card 20 - Scheduling
- Sorumluluk: AI ve modul tick dagitimi, zamanlama dengesi.
- Girdi/Cikti: Tick olaylari -> planli yurutme sirasi.
- Bagimliliklar: ModuleManager, AI subsystems.
- Hata Sinyali: Tick starvation, zamanlayici drift.
- Sahiplik Durumu: Aktif.

### Card 21 - Seasonal
- Sorumluluk: Mevsimsel etkiler ve ortam bazli modifierlar.
- Girdi/Cikti: Takvim/iklim durumlari -> davranis ve stat etkisi.
- Bagimliliklar: Settings, Territory, Combat.
- Hata Sinyali: Mevsim gecislerinde ani dengesizlik.
- Sahiplik Durumu: Pasif/Aktif (ozellik bayraklarina bagli).

### Card 22 - Spawning
- Sorumluluk: Militia spawn, hideout dinamigi, isimlendirme.
- Girdi/Cikti: Uygunluk/bolge sinyali -> parti olusumu ve kaydi.
- Bagimliliklar: ClanCache, Globals, Cleanup, ModuleManager.
- Hata Sinyali: Spawn patlamasi, kayitsiz parti olusumu.
- Sahiplik Durumu: Aktif (cekirdek gameplay).

### Card 23 - Territory
- Sorumluluk: Alan kontrolu, hotspot kaydi, bolgesel baski haritasi.
- Girdi/Cikti: Savas/katilim verisi -> bolgesel kontrol skorlar.
- Bagimliliklar: Fear, Tracking, Combat.
- Hata Sinyali: Territorial heatmap stale veya bozuk.
- Sahiplik Durumu: Aktif.

### Card 24 - Tracking
- Sorumluluk: Oyuncu ve aktivite izleme, olay gecmisi.
- Girdi/Cikti: Event akislari -> history/metrics kaydi.
- Bagimliliklar: EventBus, Diagnostics, Analytics.
- Hata Sinyali: Bellek birikimi, cleanup gecikmesi.
- Sahiplik Durumu: Aktif.

### Card 25 - Workshop
- Sorumluluk: Warlord workshop/uretim ekonomisi (ozel kanal).
- Girdi/Cikti: Kaynak + altin -> ekipman/uretim ciktilari.
- Bagimliliklar: Economy, Logistics, Progression.
- Hata Sinyali: Uretim kuyrugu kilitlenmesi.
- Sahiplik Durumu: Aktif.

## 3) Platform-Level Cards (Sistemler arasi cekirdek)

### Card P1 - Core
- Sorumluluk: EventBus, Registry, config, memory ve ortak kontratlar.
- Girdi/Cikti: Sistem eventleri -> publish/subscribe, registry saglik verisi.
- Bagimliliklar: Tum modul katmani.
- Hata Sinyali: Event dagitimi kaybi, module discovery hatasi.
- Sahiplik Durumu: Aktif (mimari cekirdek).

### Card P2 - Infrastructure
- Sorumluluk: ModuleManager, compatibility, exception monitor, dosya altyapisi.
- Girdi/Cikti: Mod lifecycle ve IO -> module init/cleanup, log akisi.
- Bagimliliklar: Core, Behaviors, Systems.
- Hata Sinyali: Init sikismasi, cleanup eksigi, catch yutma.
- Sahiplik Durumu: Aktif (P1 operasyonel omurga).

### Card P3 - Intelligence
- Sorumluluk: Stratejik/taktik AI, neural advisor, swarm/htn katmani.
- Girdi/Cikti: Dunya durumu -> karar skorlari ve doctrine ciktilari.
- Bagimliliklar: Grid, Tracking, Progression, Diagnostics.
- Hata Sinyali: AI fallback dongusu, confidence dengesizligi.
- Sahiplik Durumu: Aktif.

### Card P4 - Behaviors
- Sorumluluk: Oyun eventlerini mod sistemlerine baglayan campaign davranislari.
- Girdi/Cikti: CampaignEvents -> module triggerleri.
- Bagimliliklar: Infrastructure, Core.Events, Systems.
- Hata Sinyali: Session launch hatasi, event listener sizintisi.
- Sahiplik Durumu: Aktif.

### Card P5 - AgentCrashGuard
- Sorumluluk: Gozlem/analiz/kriz tani katmani, log toplayici.
- Girdi/Cikti: Runtime log ve telemetry -> tanisal cikarim.
- Bagimliliklar: File system, optional observer modules.
- Hata Sinyali: Tani aracinda sessiz catch nedeniyle gorunmez kayip.
- Sahiplik Durumu: Pasif/Aktif (operasyonel yardimci, runtime disi agirlik artiyor).

### Card P6 - BannerlordTestSim
- Sorumluluk: Simulasyon, fault injection, test telemetry, HTTP disa aktarim.
- Girdi/Cikti: Sim olaylari -> metrics, snapshot, scenario export.
- Bagimliliklar: Reflection adapters, HTTP, output pipeline.
- Hata Sinyali: Sim katmaninda sessiz catch ile yalanci basari.
- Sahiplik Durumu: Pasif/Aktif (test platformu, uretim ana akistan ayri tutulmali).

## 4) Ownership Backlog (Aktif/Pasif/Kosullu)

- Pasif/Kosullu (izleme listesi):
  - `Systems/Heroics` (bos iskelet)
  - `Systems/Progression/MilitiaUpgradeSystem.cs` (IsEnabled=false)
  - `Systems/Progression/TroopProgressionSystem.cs` (IsEnabled=false)
- Pasif ama korunacak:
  - `Systems/Dev/*` (DevMode)
  - Legacy gecis kodlari
  - TestSim ve AgentCrashGuard'in oyun cekirdeginden ayrik gorevleri
- Aktif ve kritik:
  - Cleanup, Spawning, Economy, Progression, Infrastructure, Core

## 5) 3.2 Silent Catch (Sessiz Hata Yutma) - Derinlesmis Dokumantasyon

Bu proje icinde bos `catch { }` kalibi 66 noktada tespit edildi.

### 5.1 Neden kritik?
- Exception sink olusturur: Hata olusur ama geri bildirim cikmaz.
- Fallback davranisini normal gorunur hale getirir.
- Regresyon tespitini zorlastirir.

### 5.2 Risk siniflandirma
- P1 (Kritik): Gameplay ve lifecycle etkileyen catch'ler
  - `BanditMilitias/Systems/*`
  - `BanditMilitias/Behaviors/*`
  - `BanditMilitias/Infrastructure/*`
- P2 (Yuksek): Diagnostik/observer ama karar destekte kullanilan catch'ler
  - `AgentCrashGuard/*`
- P3 (Orta): Test/sim yardimci catch'ler
  - `BannerlordTestSim/*`

### 5.3 Zorunlu minimum standart
Her catch blogu icin:
- En az `Warning` logu
- `Exception type + Message + ContextTag`
- Rate limit (ornek: ayni event id icin 60 saniye)

### 5.4 Is emri sirasi
1. P1 dosyalardaki tum bos catch'leri gorunur warning'e cevir.
2. Economy/Progression/Cleanup yolunda event-id standardi uygula.
3. P2/P3 katmanlarinda telemetry kanalini sessiz basariya kapat.

Not: Tam dosya/satir envanteri `Documentation/SilentCatch_Audit_TR.md` dosyasina ayrica yazilmistir.

## 6) 3.3 Lazy Singleton ve Statik Yasam Dongusu (Derinlesmis)

Problem:
- `new Lazy<T>` + `static` alanlar New Game sonrasi CLR tarafinda bellekte kalmaya devam edebilir.
- Bu durum oturumlar arasi "kirli state" ureterek phantom bug olusturur.

Etki:
- Tekrarlanamayan davranis
- Yanlis regresyon sinyali
- Save/load sonrasi farkli runtime sonucu

Uygulanabilir cozum seti:
1. SessionScope Registry
- Tum static singletonlar bir `SessionScopeRegistry` icine kaydedilir.
- `OnGameEnd` aninda toplu `Reset/Cleanup` zorunlu calisir.

2. Explicit Reset Contract
- `IResettableSessionState` arayuzu:
  - `ResetForNewSession()`
  - `CleanupForUnload()`
- Singleton/static cache tutan her modul bu kontrati uygular.

3. Static Collection Policy
- `static Dictionary/List/HashSet` kullanan siniflarda:
  - init noktasi tek
  - cleanup noktasi tek
  - lock + clear stratejisi standart

4. Lazy<T> Kullanim Rehberi
- `Lazy<T>` sadece immutable veya readonly servislerde serbest.
- Oyun state'i tutan yapilarda `Lazy<T>` yerine ModuleManager yasam dongusune bagli instance tercih.

5. Lifecycle Smoke Tests
- Test senaryosu: `New Game -> Save -> Exit -> New Game`
- Beklenti: onceki session state hash'i yeni oturuma tasinmaz.

6. Telemetry
- `SessionId` alanini loglara ekle.
- Farkli session id ile gelen state okumalarina warning bas.

Hizli tarama kriterleri (P1):
- `Lazy<ModuleType>` + `Cleanup()` bos veya yok
- `static` koleksiyon + NewGame reset yok
- `Instance => _instance.Value` deseni + session temizligi yok



