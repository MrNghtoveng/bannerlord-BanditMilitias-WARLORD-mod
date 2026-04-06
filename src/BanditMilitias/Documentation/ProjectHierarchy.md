# Bandit Militias: WARLORD - Proje Klasör Yapısı

Bu belge, repodaki ana klasörlerin ne işe yaradığını ve birbirleriyle nasıl ilişki kurduğunu özetler.

## Kök Düzey

### `BanditMilitias.csproj`

Ana mod projesidir. Bannerlord referansları, paketler, build seçenekleri ve paketleme hedefleri burada tanımlanır.

### `BanditMilitias.sln`

Mod ve test projelerini birlikte açmak için kullanılan çözüm dosyasıdır.

### `SubModule.xml`

Bannerlord mod yükleyicisinin okuduğu temel mod tanımıdır.

### `KURULUM.md`

Kurulum ve derleme notlarını kısa formatta taşır.

### `README.md`, `README.html`, `README.txt`

Projenin kullanıcıya dönük açıklama katmanlarıdır:

- `README.md`: ana kaynak belge
- `README.html`: daha görsel sunum
- `README.txt`: düz metin sürüm

## Kaynak Kod Klasörleri

### `Core/`

Temel olaylar, ortak veri sözleşmeleri ve tekrar kullanılan çekirdek yapı taşları.

### `Infrastructure/`

Save bağlama, uyumluluk, yardımcı servisler ve teknik altyapı katmanı.

### `Components/`

Partilere veya özel veri yapılarına eklenen mod bileşenleri.

### `Behaviors/`

Kampanya davranışları ve oyunun tick döngüsüne bağlanan giriş noktaları.

### `Systems/`

Oynanışın büyük kısmını oluşturan modüler sistemler. Spawn, cleanup, progression, economy, diagnostics ve diğer iş akışları burada yaşar.

### `Intelligence/`

Stratejik düşünme, taktik yönlendirme, sürü koordinasyonu, anlatı ve ML odaklı zeka katmanı.

### `Patches/`

Harmony yamaları ve vanilla davranışa yapılan kontrollü müdahaleler.

### `GUI/`

Oyuncuya bilgi sunan arayüz ve görünür geri bildirim kodu.

### `Models/`

Sistemlerin paylaştığı veri taşıyıcı sınıflar ve domain modelleri.

### `Debug/`

Geliştirme sırasında kullanılan tanı ve teknik destek araçları.

### `tools/`

Yardımcı geliştirme araçları veya üretim destek dosyaları.

## İçerik ve Dağıtım Klasörleri

### `ModuleData/`

Bannerlord mod yapısında oyuna kopyalanan veri ve içerik dosyaları.

### `dist/`

Paketleme veya dağıtım için hazırlanan çıktı klasörü.

### `bin/` ve `obj/`

Build çıktıları ve derleme ara dosyaları.

## Test ve Belgeler

### `BanditMilitias.Tests/`

Saf mantık, entegrasyon ve dokümantasyon doğrulamaları için masaüstü test projesi.

### `Documentation/`

Mimari yapıyı ve sistem ilişkilerini anlatan yardımcı belgeler:

- `ProjectHierarchy`
- `ModuleHierarchy`
- `SystemFlowTree`
- `InGameTestingGuide`
- `AIArchitecture`
- `AI_Assisted_Development`

## Nasıl Okunmalı?

Projeyi ilk kez incelerken en verimli sıra genelde şöyledir:

1. `README.md`
2. `Documentation/ProjectHierarchy.md`
3. `Documentation/ModuleHierarchy.md`
4. `Documentation/SystemFlowTree.md`
5. `Systems/` ve `Intelligence/` altındaki ilgili modüller

Bu sıralama, önce dış yüzü sonra klasör mantığını sonra da veri akışını anlamayı kolaylaştırır.
