# Silent Catch Audit (TR)

Tarih: 2026-04-25
Kapsam: Bos `catch { }` yakalamalari

Toplam tespit: **66**

## Ozet
- P1: 26
- P2: 16
- P3: 24

## Tam Envanter (dosya: satir)
| Oncelik | Dosya | Satir | Ornek |
|---|---|---:|---|
| P1 | BanditMilitias\Behaviors\MilitiaBehavior.cs | 203 | `try { FileLogger.LogError($"OnSessionLaunched failed: {ex}"); } catch {}` |
| P1 | BanditMilitias\Behaviors\MilitiaBehavior.cs | 435 | `try { BanditMilitias.Infrastructure.FileLogger.LogError(err); } catch { }` |
| P1 | BanditMilitias\Behaviors\MilitiaDiplomacyCampaignBehavior.cs | 453 | `catch { }` |
| P1 | BanditMilitias\Behaviors\WarlordCampaignBehavior.cs | 22 | `catch { }` |
| P1 | BanditMilitias\Core\Config\Constants.cs | 316 | `catch { }` |
| P1 | BanditMilitias\Infrastructure\CompatibilityLayer.cs | 776 | `try { FileLogger.LogError(warn); } catch { }` |
| P1 | BanditMilitias\Infrastructure\CompatibilityLayer.cs | 784 | `try { FileLogger.LogError(err); } catch { }` |
| P1 | BanditMilitias\Infrastructure\CompatibilityLayer.cs | 1091 | `catch { }` |
| P1 | BanditMilitias\Infrastructure\CompatibilityLayer.cs | 1292 | `catch { }` |
| P1 | BanditMilitias\Infrastructure\ExceptionMonitor.cs | 163 | `catch { }` |
| P1 | BanditMilitias\Infrastructure\ModuleInfra.cs | 298 | `catch { }` |
| P1 | BanditMilitias\Infrastructure\ModuleManager.cs | 1124 | `try { BanditMilitias.Infrastructure.FileLogger.LogError(fullError); } catch { }` |
| P1 | BanditMilitias\Systems\Cleanup\PartyCleanupSystem.cs | 720 | `catch { }` |
| P1 | BanditMilitias\Systems\Cleanup\PartyCleanupSystem.cs | 759 | `catch { }` |
| P1 | BanditMilitias\Systems\Crisis\CrisisEventSystem.cs | 119 | `catch { }` |
| P1 | BanditMilitias\Systems\Dev\DevDataCollector.cs | 417 | `catch { }` |
| P1 | BanditMilitias\Systems\Dev\DevDataCollector.cs | 538 | `catch { }` |
| P1 | BanditMilitias\Systems\Dev\DevDataCollector.cs | 580 | `catch { }` |
| P1 | BanditMilitias\Systems\Dev\DevDataCollector.cs | 739 | `catch { }` |
| P1 | BanditMilitias\Systems\Dev\DevDataCollector.cs | 835 | `catch { }` |
| P1 | BanditMilitias\Systems\Dev\DevDataCollector.cs | 846 | `catch { }` |
| P1 | BanditMilitias\Systems\Dev\DevDataCollector.cs | 1003 | `catch { }` |
| P1 | BanditMilitias\Systems\Dev\DevDataCollector.cs | 1156 | `catch { }` |
| P1 | BanditMilitias\Systems\Diagnostics\BanditTestHub.cs | 62 | `try { _ = TaleWorlds.Core.MBRandom.RandomFloat; } catch { } // warm-up` |
| P1 | BanditMilitias\Systems\Diagnostics\MilitiaAssertionSystem.cs | 415 | `catch { }` |
| P1 | BanditMilitias\Systems\Legacy\WarlordLegacySystem.cs | 211 | `catch { }` |
| P2 | AgentCrashGuard\Analysis\CrashAnalyzer.cs | 119 | `catch { }` |
| P2 | AgentCrashGuard\Analysis\IncrementalDeepScanner.cs | 209 | `catch { }` |
| P2 | AgentCrashGuard\Analysis\KnowledgeBase.cs | 76 | `catch { }` |
| P2 | AgentCrashGuard\Analysis\LogAggregator.cs | 87 | `try { sb.AppendLine("  " + Diagnostics.HarmonyConflictDetector.GetOneLinerSummary()); } catch { }` |
| P2 | AgentCrashGuard\Analysis\LogAggregator.cs | 127 | `catch { }` |
| P2 | AgentCrashGuard\Analysis\LogAggregator.cs | 265 | `catch { }` |
| P2 | AgentCrashGuard\Analysis\LogAggregator.cs | 357 | `catch { }` |
| P2 | AgentCrashGuard\DiagnosticLogger.cs | 85 | `try { File.Copy(_pathTR, _pathTRLatest, true); } catch { }` |
| P2 | AgentCrashGuard\DiagnosticLogger.cs | 86 | `try { File.Copy(_pathEN, _pathENLatest, true); } catch { }` |
| P2 | AgentCrashGuard\DiagnosticLogger.cs | 119 | `catch { }` |
| P2 | AgentCrashGuard\DiagnosticLogger.cs | 201 | `catch { }` |
| P2 | AgentCrashGuard\DiagnosticLogger.cs | 240 | `catch { }` |
| P2 | AgentCrashGuard\DiagnosticLogger.cs | 256 | `catch { }` |
| P2 | AgentCrashGuard\Diagnostics\HarmonyConflictDetector.cs | 168 | `catch { }` |
| P2 | AgentCrashGuard\OptionalModules\BanditMilitias\BanditMilitiasObserver.cs | 278 | `catch { }` |
| P2 | AgentCrashGuard\OptionalModules\BanditMilitias\BanditMilitiasObserver.cs | 368 | `catch { }` |
| P3 | BanditMilitias\Debug\Debug.cs | 40 | `try { _instance.Dispose(); } catch { }` |
| P3 | BanditMilitias\Debug\Debug.cs | 1226 | `try { FileLogger.Log($"[PipelineCheck]\n{result}"); } catch { }` |
| P3 | BanditMilitias\Intelligence\AI\ScoringFunctions.cs | 102 | `catch { }` |
| P3 | BanditMilitias\Intelligence\Neural\NeuralAdvisor.cs | 481 | `catch { }` |
| P3 | BanditMilitias\Intelligence\Neural\NeuralDataExporter.cs | 97 | `catch { }` |
| P3 | BanditMilitias\Intelligence\Neural\NeuralDataExporter.cs | 134 | `catch { }` |
| P3 | BannerlordTestSim\src\AIObserver\AIObserverPlugin.cs | 163 | `catch { }` |
| P3 | BannerlordTestSim\src\Core\SimLogger.cs | 155 | `catch { }` |
| P3 | BannerlordTestSim\src\FaultInjection\FaultInjector.cs | 434 | `catch { }` |
| P3 | BannerlordTestSim\src\Http\SimHttpServer.cs | 100 | `try { _listener?.Stop(); } catch { }` |
| P3 | BannerlordTestSim\src\Http\SimHttpServer.cs | 115 | `catch { }` |
| P3 | BannerlordTestSim\src\Http\SimHttpServer.cs | 259 | `catch { }` |
| P3 | BannerlordTestSim\src\Http\SimHttpServer.cs | 262 | `try { ctx.Response.Close(); } catch { }` |
| P3 | BannerlordTestSim\src\HUD\SimHudPlugin.cs | 96 | `catch { }` |
| P3 | BannerlordTestSim\src\HUD\SimHudPlugin.cs | 243 | `catch { }` |
| P3 | BannerlordTestSim\src\Metrics\SimMetricCollector.cs | 104 | `catch { }` |
| P3 | BannerlordTestSim\src\Metrics\SimMetricCollector.cs | 188 | `catch { }` |
| P3 | BannerlordTestSim\src\Output\AutoOutputCollector.cs | 256 | `catch { }` |
| P3 | BannerlordTestSim\src\Recording\ScenarioRecorder.cs | 142 | `catch { }` |
| P3 | BannerlordTestSim\src\Reflection\BanditMilitiasReflector.cs | 256 | `catch { }` |
| P3 | BannerlordTestSim\src\Reflection\BanditMilitiasReflector.cs | 312 | `catch { }` |
| P3 | BannerlordTestSim\src\Reflection\BanditMilitiasReflector.cs | 327 | `catch { }` |
| P3 | BannerlordTestSim\src\Watch\WatchSystem.cs | 277 | `catch { }` |
| P3 | BannerlordTestSim\src\Watch\WatchSystem.cs | 364 | `catch { }` |
