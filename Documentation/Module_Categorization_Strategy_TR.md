# Modulleri Kategorize ve Siniflandirma Stratejisi (TR)

Tarih: 2026-04-25

## Amac
Modullerin teknik sahipligini, risk seviyesini ve yasam dongusunu netlestirmek.

## Onerilen cok eksenli siniflandirma
1. Islevsel eksen
- Gameplay Core (Spawning, Cleanup, Economy, Progression)
- Decision AI (Intelligence, Doctrine, Brain)
- Platform (Core, Infrastructure)
- Observability (Diagnostics, Dev, AgentCrashGuard)

2. Yasam dongusu ekseni
- Session-scoped
- Campaign-scoped
- Process-scoped (kacinilacak)

3. Guvenilirlik ekseni
- P1 kritik (oynanis kirilir)
- P2 yuksek (denge/surecleri bozar)
- P3 destek (diagnostic/sim)

4. Durum ekseni
- Aktif
- Pasif/Kosullu

## Modul kimlik standardi
- Kimlik: `ModuleName` (tekil)
- Takma adlar: Registry alias (cakisma denetimli)
- Dokuman anahtari: `Module ID`

## Yonetisim kurallari
- Her modul icin zorunlu kart:
  - Sorumluluk
  - Girdi/Cikti
  - Bagimlilik
  - Hata sinyali
  - Lifecycle sinifi
  - Sahiplik durumu
- Her release oncesi:
  - Silent catch sayisi
  - Static state aciklari
  - Dependency drift raporu

## Otomasyon fikirleri
- Module lint:
  - AutoRegister var mi?
  - ModuleName tekil mi?
  - Cleanup override var mi?
  - Static koleksiyon var mi?
- Dependency graph export:
  - Mermaid + csv
- Session leak test:
  - NewGame tekrariyla state hash karsilastirma

## Shadow Empire'a tasinabilir cekirdek secimi
- Keep: Core/Infrastructure + temiz gameplay core
- Refactor: Legacy/Dev agir moduller
- Review: IsEnabled=false + tekrarli miras kod
