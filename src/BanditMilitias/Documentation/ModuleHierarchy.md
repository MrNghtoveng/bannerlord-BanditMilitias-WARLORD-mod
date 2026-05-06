# Bandit Militias: WARLORD - Modül Hiyerarşisi

Bu belge, projedeki ana sistem gruplarını ve bunların sorumluluklarını özetler. Amaç, tüm dosyaları tek tek listelemekten çok modun hangi iş yüklerini hangi katmanlara böldüğünü anlaşılır hale getirmektir.

## Intelligence

Bu katman yüksek seviyeli karar üretimi ve davranış yönlendirmesi ile ilgilenir.

- `Strategic/`: küresel hedef üretimi, duruş değişimleri ve baskın stratejileri
- `Tactical/`: sahaya yakın karar kuralları ve anlık tepki mekanikleri
- `Swarm/`: birden fazla grubun koordinasyon mantığı
- `ML/`: veri toplama, karar telemetrisi ve öğrenme odaklı destek katmanı
- `Narrative/`: warlord kimliği, kişilik ve tematik anlatı desteği
- `AI/` ve `Logging/`: davranışa yakın yardımcı bileşenler ve tanı akışı

## Systems

Projede en yoğun iş yükü burada toplanır. `Systems` klasörü, doğrudan oynanışı etkileyen modülleri barındırır.

### Çekirdek Akış

- `Spawning/`: yeni milis partilerinin oluşumu, koşulları ve ilk yapılandırması
- `Scheduling/`: AI işlemlerini sıraya koyup zamana yayma
- `Cleanup/`: zayıf, bozulmuş veya gereksiz parti yükünü azaltma
- `Grid/`: uzamsal sorgular ve komşuluk hesapları
- `Tracking/`: dünya olayları, oyuncu etkisi ve hareket verileri

### Oynanış ve Baskı Katmanı

- `Raiding/`: baskın ve yağma davranışları
- `Fear/`: korku, ihanet, panik ve moral etkileri
- `Combat/`: savaş sonucu ve çatışma bağlantılı destek mantığı
- `Territory/`: bölgesel etki ve alan baskısı
- `Diplomacy/`: düello, harç ve ilişki tabanlı etkileşimler
- `Bounty/`: ödül ve hedef takibi

### İlerleme ve Dünya Kalıcılığı

- `Progression/`: warlord kariyeri, meşruiyet, halefiyet ve yükselme kuralları
- `Economy/`: gelir, gider, kaynak ve uzun vadeli büyüme ilişkileri
- `Logistics/`: taşıma, destek ve operasyonel sürdürülebilirlik
- `Workshop/`: altyapı veya üretim odaklı genişleme mantıkları
- `Legacy/`: ölüm sonrası miras ve devamlılık davranışları

### Destekleyici Katmanlar

- `Diagnostics/`: oyun içi test hub ve teknik gözlem araçları
- `Dev/`: geliştirme sürecinde kullanılan yardımcı akışlar
- `Events/`: sistemler arası olay yönlendirmeleri
- `Enhancement/`: ekipman ve güçlendirme temelli destekler
- `Seasonal/`: dönemsel veya zamana bağlı kurallar
- `Crisis/`: kaos, baskı ve sistemik kırılma anları

## Infrastructure

Bu katman oyun motoru ile daha teknik seviyede konuşur.

- kayıt ve save bağlama işleri
- uyumluluk ve koruma katmanları
- opsiyonel mod entegrasyonları
- MCM bağlantıları
- ortak altyapı yardımcıları

Özellikle `CompatibilityLayer`, dış modlarla veya koruma sistemleriyle uyumlu davranmayı kolaylaştırır.

## Core

`Core/` klasörü olaylar, temel sözleşmeler ve tekrar kullanılan yapı taşları için bulunur. Üst katmanlar mümkün olduğunca bu çekirdek sözleşmeler üzerinden konuşur.

## Components

`Components/`, partilere eklenen mod verilerini barındırır. En kritik örnek, bir partinin Bandit Militias sistemi tarafından yönetildiğini belirleyen bileşenlerdir.

## Behaviors

`Behaviors/`, Bannerlord kampanya döngüsüne bağlanan davranış sınıflarını içerir. Bu katman, modüllerin oyunun tick akışına doğru zamanda bağlanmasını sağlar.

## Patches

`Patches/`, Harmony ile yapılan motor düzeyi müdahaleleri içerir. Buradaki kodlar genelde vanilla davranışı bastırmak, uyumluluk sağlamak veya crash riskini düşürmek için kullanılır.

## GUI

`GUI/`, oyuncuya bilgi sunan arayüz elemanları ve görünür geri bildirim akışları için ayrılmıştır.

## Test ve Dokümantasyon

- `BanditMilitias.Tests/`: saf mantık ve entegrasyon testleri
- `Documentation/`: mimari, klasör yapısı ve sistem akışı notları

Bu ayrım sayesinde proje, hem oyun içi runtime testlerle hem de masaüstü testleriyle doğrulanabilir bir yapıda tutulur.
