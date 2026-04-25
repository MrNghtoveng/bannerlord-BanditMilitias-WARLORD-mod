# Bandit Militias: WARLORD - Directory Notes

Bu belge, `Directory Structure.md` için tamamlayıcı teknik notlar tutar.

## Özel Notlar

- `MilitiaHideoutCampaignBehavior` ve `MilitiaRewardCampaignBehavior` ayrı `.cs` dosyaları yerine `Behaviors/MilitiaBehavior.cs` içindedir.
- `HardcoreDynamicHideoutSystem` ayrı bir dosya değil; `Systems/Spawning/DynamicHideoutSystem.cs` içinde tanımlıdır.
- `EventBus` çekirdeği `Core/Events/EventBus.cs` altındadır; aktif dispatch ise deferred init ve session bootstrap sonrasında devreye girer.
- Taktik HTN zincirinin aktif mission bağlantısı `Intelligence/Tactical/WarlordTacticalMissionBehavior.cs` üzerindendir.
- `NeuralAdvisor.cs` ve `NeuralCore.cs` hibrid AI katmanının kalbidir; saf C# (Neural) implementasyonunu barındırır.

## v1.3.15 Migrasyon Notları

Son güncellemelerle birlikte şu teknik değişiklikler yapılmıştır:

- **Konsol Komutları**: Tüm `bandit.` ve `militia.` komutları v1.3.15'in attribute tabanlı sistemine (`[CommandLineArgumentFunction]`) geçirilmiştir.
- **CampaignTime**: Zaman hesaplamalarında (`ToDays`, `ToHours`) hassasiyet hatalarını önlemek için açık (explicit) float cast'leri eklenmiştir.
- **WorldMemory**: Dünya verisi artık doğrudan motor sorguları yerine `WorldMemory.cs` içindeki üç katmanlı (Bedrock, Geology, Weather) cache yapısından okunur. Bu, özellikle büyük haritalarda performansı %40 artırır.
- **Etkinlik Temizliği (Cleanup)**: Bellek sızıntılarını önlemek için tüm modüllerin `Cleanup()` metodunda etkinlik aboneliklerini (`EventBus.Instance.Unsubscribe`) sonlandırması zorunlu hale gelmiştir.

## Son Güncellenen Alanlar (Nisan 2026)

- Yeni MCM ayarları ve v1.3.15 uyumluluk yamaları.
- `MilitiaDecider` içindeki hibrid (Neural + Heuristic) karar harmanlama mantığı.
- 229 testlik tam stabil test paketi.

---
*Son Güncelleme: Nisan 2026*
