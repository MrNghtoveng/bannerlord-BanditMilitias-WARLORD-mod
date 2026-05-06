# Bandit Militias: WARLORD - Directory Notes

Bu belge, `Directory Structure.md` icin tamamlayici notlar tutar. Burada amac tam agac cizmek degil; son wiring kararlarini ve dosya yerlesimiyle ilgili kolayca unutulan noktalarini belgelemektir.

## Ozel Notlar

* `MilitiaHideoutCampaignBehavior` ve `MilitiaRewardCampaignBehavior` ayri `.cs` dosyalari yerine `Behaviors/MilitiaBehavior.cs` icindedir.
* `HardcoreDynamicHideoutSystem` ayri bir dosya degil; `Systems/Spawning/DynamicHideoutSystem.cs` icinde tanimlidir.
* `EventBus` cekirdegi `Core/Events/EventBus.cs` altindadir; aktif dispatch ise deferred init ve session bootstrap sonrasinda devreye girer.
* Taktik HTN zincirinin aktif mission baglantisi `Intelligence/Tactical/WarlordTacticalMissionBehavior.cs` uzerindendir.
* `AdaptiveDoctrineDataLogger.cs` ve `AdaptiveDoctrineTypes.cs` yardimci AI dosyalaridir; dogrudan campaign module kaydi beklenmez.

## Tarama Sirasinda Dikkat

Ghost veya dead kod ararken su ayrimi yapmak gerekir:

* `MilitiaModuleBase` tureyen siniflar kayit ve lifecycle bekler
* helper, doctrine, task, logger veya static utility siniflar her zaman dogrudan kayit beklemez
* ayni namespace icinde ama farkli dosyada tanimlanmis siniflar dosya adiyla bire bir eslesmeyebilir

## Son Guncellenen Alanlar

Son dokumantasyon ve wiring guncellemeleriyle birlikte su basliklar ozellikle gunceldir:

* yeni MCM ayarlari: `HideoutReplenishChance`, `BountyGoldPerTroop`
* EventBus aktivasyon modeli ve deferred queue davranisi
* taktik HTN gorev onkosullarinin aktif hale gelmesi
* localization XML girdilerinin EN ve TR tarafinda senkron kalmasi
