# Progression Modulleri Cakişma Durumu (TR)

Tarih: 2026-04-25

## Sonuc
Aktif progression mantigi tek kaynakta: `MilitiaProgressionSystem`.

## Kontrol Bulgulari
- `MilitiaUpgradeSystem`:
  - `IsEnabled => false`
  - `[AutoRegister]` yok
  - `UpgradePartyTroops(...)` sadece bridge olarak `MilitiaProgressionSystem.UpgradePartyTroopsCompat(...)` cagiriyor.
- `TroopProgressionSystem`:
  - `IsEnabled => false`
  - `[AutoRegister]` yok
  - `AddToSharedPool(...)` sadece `MilitiaProgressionSystem.AddToHordePool(...)` cagiriyor.
- Canli cagrilar:
  - Combat zafer akisi `MilitiaProgressionSystem.Instance.OnBattleVictory(...)` uzerinden ilerliyor.

## Risk Notu
Legacy iki sinif kod tabaninda tutuluyor fakat aktif degil. Bu sayede eski referanslar kirilmadan, cift mantik calismasi engellenmis durumda.
