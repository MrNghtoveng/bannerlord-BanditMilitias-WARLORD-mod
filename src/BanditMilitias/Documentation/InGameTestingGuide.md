# In-Game Testing Guide

## Türkçe

Bu belge, oyun içi testleri daha düzenli yapmak için hazırlandı. Amaç yalnızca hata yakalamak değil; AI davranışı, dünya yüklenmesi, performans, cleanup ve telemetri akışlarını aynı oturum içinde anlamlı biçimde okumaktır.

---

## English

This guide is meant to make in-game testing more consistent. The goal is not only to catch bugs, but also to read AI behavior, world load, performance, cleanup, and telemetry in a structured way within the same session.

---

## Test Goals

- Verify that militia AI is active and ticking correctly
- Verify that runtime diagnostics are reachable
- Watch world growth and party inflation over time
- Catch sleep, cleanup, and scheduler regressions
- Confirm that logs and exported reports match in-game behavior

---

## Recommended Session Order

### 1. Startup Check

After loading a campaign:

1. Confirm the module loaded without immediate red warnings
2. Wait for the activation window to pass
3. Run `militia.system_status`
4. Run `bandit.test_list`

### 2. Baseline Runtime Check

```
bandit.test_run all
bandit.test_report
militia.diag_report
```

### 3. AI Behavior Check

Watch several militia parties on the campaign map. Look for:

- Patrol behavior
- Merge attempts between nearby parties
- Recruit attempts
- Flee behavior under pressure
- Swarm coordination when multiple militia parties are near each other

Useful commands:

```
militia.full_sim_test
militia.full_sim_report
militia.ml_status
militia.doctrine_status
```

### 4. Long Session World Health Check

Track the following over extended play:

- Total mobile party count
- Zero-troop or broken parties
- Headless or invalid party remnants
- Cleanup activity rate
- Scheduler lag or tick storms

### 5. Save / Load Regression Check

1. Make a save mid-session
2. Reload it
3. Run `bandit.test_run all` again
4. Compare the new `bandit.test_report` against the previous one

---

## Main Runtime Commands

| Command | Purpose |
|---|---|
| `bandit.test_list` | List all registered tests |
| `bandit.test_run all` | Run all tests |
| `bandit.test_report` | Print last test report |
| `bandit.test_reset` | Reset test state |
| `militia.full_sim_test` | Run full simulation test |
| `militia.full_sim_report` | Print simulation report |
| `militia.failed_modules` | List modules with failures |
| `militia.system_status` | Print overall system status |
| `militia.module_status` | Print per-module status |
| `militia.watchdog_status` | Print watchdog status |
| `militia.watchdog_check` | Trigger a manual watchdog check |
| `militia.ml_status` | Print ML and telemetry status |
| `militia.doctrine_status` | Print active doctrine state |

---

## Common Runtime Test States

| State | Meaning |
|---|---|
| `ghost module` | Module is registered but not ticking |
| `dead module` | Module has stopped responding entirely |
| `stale module` | Module data has not been updated recently |
| `event leak` | Events are accumulating without being consumed |
| `cold module` | Module has not yet been activated |

---

## High-Value Report Files

| File | Contents |
|---|---|
| `Events.json` | Raw event stream from the current session |
| `session_summary.txt` | High-level session overview |
| `ai_decisions.csv` | AI decision log with timestamps |
| `sleep_analysis.csv` | Sleep and wakeup analysis per module |
| `CleanupHistory.csv` | Cleanup events and removed parties |
| `BanditMilitias_FullSim.txt` | Full simulation test output |

---

## Common Failure Patterns

| Pattern | Symptoms |
|---|---|
| Party inflation | Mobile party count grows unbounded over time |
| Sleep logic failure | Modules not entering or exiting sleep correctly |
| AI fallback lock | AI stuck on a fallback decision indefinitely |
| Patch routing failure | Vanilla AI behavior overrides mod AI unexpectedly |

---

## Related Documents

- [Documentation/SystemFlowTree.md](SystemFlowTree.md)
- [Documentation/AIArchitecture.md](AIArchitecture.md)
- [Documentation/AI_Assisted_Development.md](AI_Assisted_Development.md)
