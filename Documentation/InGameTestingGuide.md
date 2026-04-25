# In-Game Testing Guide

Bu belge, **Bandit Militias: WARLORD Edition** için oyun içi testleri düzenli yapmak için hazırlanmış bir rehberdir.

## Test Hedefleri

- Milis AI'nın aktif olduğundan emin olun.
- Runtime tanı araçlarının (diagnostics) çalıştığını doğrulayın.
- Dünya üzerindeki milis sayısı ve "zombi" parti oluşumunu izleyin.
- Kayıt (Save/Load) sonrası sistemlerin kararlılığını test edin.
- Nöral Danışman (Neural Advisor) performansını ve kararlarını inceleyin.

## Önerilen Test Akışı

### 1. Başlangıç Kontrolü
Kampanya yüklendikten sonra:
- `militia.system_status`: Tüm modüllerin "Healthy" olduğundan emin olun.
- `bandit.test_list`: Runtime test hub'ın hazır olduğunu doğrulayın.

### 2. Runtime Tanı (Diagnostics)
- `bandit.test_run all`: Mevcut oturumdaki statik ve dinamik denetimleri çalıştırır.
- `bandit.test_report`: Test sonuçlarını özetler.
- `militia.failed_modules`: Hata veren modülleri listeler.

### 3. AI Davranış Gözlemi
Harita üzerinde milis gruplarını izlerken şu komutları kullanın:
- `militia.neural_status`: Nöral danışmanın güven (confidence) seviyesini ve karar istatistiklerini gösterir.
- `militia.doctrine_status`: Aktif doktrinleri ve taktiksel tercihleri listeler.
- `militia.full_sim_report`: Simülasyon verilerini (kararlar, rütbeler vb.) özetler.

### 4. Uzun Oturum Sağlık Kontrolü
Oyun süresi ilerledikçe:
- `militia.list_parties`: Haritadaki milislerin durumunu ve rütbelerini döker.
- `militia.watchdog_status`: Kritik sistemlerin (Tick hatası vb.) durumunu gösterir.
- `militia.slow_modules`: Performans darboğazı yaratan modülleri tespit eder.

## Ana Konsol Komutları (v1.3.15)

### Sistem ve Tanı
- `militia.system_status`
- `militia.module_status`
- `militia.diag_report`
- `militia.failed_modules`
- `militia.help`: Tüm `militia.` komutlarını listeler.

### Test Hub (Bandit)
- `bandit.test_list`
- `bandit.test_run <id|all>`
- `bandit.test_report`
- `bandit.test_reset`

### Nöral ve AI (Hibrid)
- `militia.neural_status`: Nöral sistem özeti.
- `militia.neural_toggle`: Nöral danışmanı açar/kapatır.
- `militia.neural_train`: [Geliştirici] Mevcut deneyim buffer'ı ile eğitim yapar.
- `militia.doctrine_status`: Aktif taktiksel doktrinler.

### Geliştirici ve Debug
- `bandit.force_spawn`: Rastgele milis partisi oluşturur.
- `bandit.toggle_test_mode`: Detaylı loglama ve test modunu açar.
- `militia.full_sim_test`: 30 milislik tam simülasyonu başlatır (Warlord dengesi için).

---
*Son Güncelleme: Nisan 2026*
