# BannerlordTestSim v2.0

**Mount & Blade II: Bannerlord** için gelişmiş test, simülasyon ve AI gözlem modu.

---

## Mimari

```
SimCore  (Orkestratör)
  │
  ├── SimEventBus          ← Pluginler arası typed pub/sub
  ├── SimConfig            ← JSON konfigürasyon
  ├── SimLogger            ← Renkli in-game + dosya logger
  │
  ├── ScenarioEngine       ← Branching durum makinesi
  │     └── BuiltInScenarios (6 hazır senaryo)
  │
  ├── RegressionRunner     ← CI benzeri headless test
  │     ├── SimAssertion   ← Fluent assertion API
  │     └── TestReport     ← HTML rapor üretici
  │
  ├── AIObserverPlugin     ← BM AI sistemlerini izler
  │     ├── QTableObserver ← Q-learning değer takibi
  │     ├── HTNTracer      ← HTN plan geçmişi
  │     └── BehaviorTimeline ← Kronolojik olaylar
  │
  ├── SimMetricCollector   ← CSV streaming metrik toplayıcı
  │
  ├── SimHudPlugin         ← F12 ile açılan HUD
  │
  └── Reflection Layer
        ├── BanditMilitiasReflector   ← BM iç sistemleri
        └── AgentCrashGuardReflector  ← ACG iç durumu
```

---

## Kurulum

1. `BannerlordTestSim` klasörünü `Modules/` altına koy
2. `SubModule.xml` doğru yerde mi kontrol et
3. `.csproj` içinde `BANNERLORD_GAME_DIR` ortam değişkenini ayarla
4. Build → DLL `bin/Win64_Shipping_Client/` altına kopyalanır
5. Bannerlord launcher'da modu etkinleştir

---

## Konfigürasyon

`BannerlordTestSimConfig.json` (Modules klasörü yanında otomatik oluşturulur):

```json
{
  "EnableHUD": true,
  "EnableAIObserver": true,
  "EnableMetrics": true,
  "HeadlessMode": false,
  "VerboseLogging": false,
  "HUDToggleKey": "F12",
  "HUDUpdateIntervalSeconds": 1.0,
  "ScenarioDefaultTimeoutDays": 30,
  "AutoRunStartupScenario": false,
  "StartupScenarioId": "",
  "AutoGenerateReport": true,
  "AIObserverTraceHTN": true,
  "AIObserverTraceQTable": true
}
```

---

## Konsol Komutları

Oyun içi `~` (tilde) konsolu ile kullanılır.

### Genel
| Komut | Açıklama |
|-------|----------|
| `sim.status` | Tüm pluginlerin anlık durumu |
| `sim.version` | Sürüm bilgisi |
| `sim.flush` | Log/metrik dosyalarını diske yaz |
| `sim.reset` | Senaryolar + assertion'ları sıfırla |
| `sim.headless` | Headless modunu aç/kapat |

### Senaryo
| Komut | Açıklama |
|-------|----------|
| `sim.scenario.list` | Kayıtlı senaryolar |
| `sim.scenario.run <id>` | Senaryo başlat |
| `sim.scenario.stop` | Tüm aktif senaryoları durdur |
| `sim.scenario.status` | Aktif senaryo durumu |

### AI Gözlemci
| Komut | Açıklama |
|-------|----------|
| `sim.ai.qtable` | Q-Table anlık görüntüsü |
| `sim.ai.htn` | HTN plan geçmişi |
| `sim.ai.timeline` | Davranış zaman çizelgesi |
| `sim.ai.timeline 30` | Son 30 olay |
| `sim.ai.report` | Tam AI raporu |
| `sim.ai.sample` | Manuel örnekleme yap |

### Regresyon
| Komut | Açıklama |
|-------|----------|
| `sim.reg.status` | Assertion özeti |
| `sim.reg.failed` | Başarısız assertion'lar |
| `sim.reg.report` | HTML rapor üret |
| `sim.reg.reset` | Assertion'ları sıfırla |
| `sim.reg.assert <id> true` | Manuel assertion tetikle |

### Metrik
| Komut | Açıklama |
|-------|----------|
| `sim.metric.summary` | İstatistik özeti |
| `sim.metric.get <name>` | Tek metrik detayı |
| `sim.metric.record <name> <val>` | Manuel metrik kaydet |
| `sim.metric.flush` | CSV diske yaz |

### Plugin Yönetimi
| Komut | Açıklama |
|-------|----------|
| `sim.plugin.list` | Plugin listesi |
| `sim.plugin.enable <id>` | Plugin etkinleştir |
| `sim.plugin.disable <id>` | Plugin devre dışı bırak |

---

## Yerleşik Senaryolar

| ID | Ad | Açıklama |
|----|----|----------|
| `spawn_stress` | Spawn Stres Testi | 30 günde 10+ milisya |
| `warlord_career` | Warlord Kariyer Testi | 60 günde Tier 3 |
| `economy_stability` | Ekonomi Stabilite | 20 gün negatife düşme |
| `ai_observation` | AI Gözlem | 15 gün metrik toplama |
| `combined_regression` | Kombine Regresyon | Tüm sistemler sıralı |
| `headless_smoke` | Smoke Test | CI için hızlı sağlık kontrol |

---

## Özel Senaryo Yazma

```csharp
var def = new ScenarioDefinition
{
    Id = "my_scenario",
    Name = "Benim Senaryom",
    DefaultTimeoutDays = 20,
    Steps = new List<ScenarioStep>
    {
        new ScenarioStep
        {
            StepId = "start",
            Actions = new List<ScenarioAction>
            {
                ScenarioAction.Log("Başladı."),
                ScenarioAction.Alert("Test aktif."),
            },
            WaitCondition = ScenarioCondition.MilitiaAbove(5),
            Branches = new List<ScenarioBranch>
            {
                new ScenarioBranch
                {
                    BranchId = "ok",
                    Condition = ScenarioCondition.MilitiaAbove(5),
                    TargetStepId = "success",
                }
            },
        },
        new ScenarioStep
        {
            StepId = "success",
            Actions = new List<ScenarioAction>
            {
                ScenarioAction.Log("✅ Başarı!"),
                ScenarioAction.Metric("my_test", 1),
            }
        },
    }
};

// Kaydet ve başlat
Core.ScenarioEngine.Register(def);
Core.ScenarioEngine.StartScenario("my_scenario");
```

---

## Çıktı Dosyaları

| Dosya | Konum | İçerik |
|-------|-------|--------|
| `sim_YYYYMMDD_HHmmss.log` | `TestSimOutput/` | Tam log |
| `metrics_YYYYMMDD_HHmmss.csv` | `TestSimOutput/` | CSV metrik stream |
| `TestReport_YYYYMMDD_HHmmss.html` | `TestSimOutput/` | HTML regresyon raporu |
| `BannerlordTestSimConfig.json` | Modules yanı | Konfigürasyon |

---

## v1.x → v2.0 Farklar

| Özellik | v1.x | v2.0 |
|---------|------|------|
| Mimari | Monolitik | Plugin Host |
| Senaryo | Doğrusal zincir | Branching + koşullu dal |
| Assertion API | Temel | Fluent, zengin tip desteği |
| AI İzleme | Yok | QTableObserver + HTNTracer + BehaviorTimeline |
| Rapor | Yok | HTML (renk kodlu, metrikli) |
| Konfigürasyon | Kod içi | JSON dosyası |
| Headless | Sınırlı | Tam CI modu |
| EventBus | Yok | Typed pub/sub |
| Komutlar | 7 | 25 |
| Dosya sayısı | 13 | 15 |
| Satır sayısı | ~2.500 | ~4.800 |

---

*BannerlordTestSim v2.0 — BanditMilitias: Warlord Edition için*
