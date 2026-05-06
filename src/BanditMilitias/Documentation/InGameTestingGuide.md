# In-Game Testing Guide

This document is a practical runtime testing guide for `Bandit Militias: WARLORD Edition`.

## Turkce

Bu belge, oyun ici testleri daha duzenli yapmak icin hazirlandi. Amac, sadece hata yakalamak degil; AI davranisi, dunya yuklenmesi, performans, cleanup ve telemetry akislarini ayni oturum icinde anlamli sekilde okumaktir.

## English

This guide is meant to make in-game testing more consistent. The goal is not only to catch bugs, but also to read AI behavior, world load, performance, cleanup, and telemetry in a structured way during the same session.

## Test Goals

- verify that militia AI is active
- verify that runtime diagnostics are reachable
- watch world growth and party inflation
- catch sleep, cleanup, and scheduler regressions
- confirm that logs and exported reports match what happened in game

## Recommended Session Order

### 1. Startup Check

After loading a campaign:

- confirm the module loaded without immediate red warnings
- wait until the activation window passes
- run `militia.system_status`
- run `bandit.test_list`

What you want to see:

- modules reporting healthy or at least not dead
- runtime test hub responding normally
- no immediate repeated initialization loops

### 2. Baseline Runtime Check

Run:

- `bandit.test_run all`
- `bandit.test_report`
- `militia.diag_report` if needed

Focus on:

- ghost module
- dead module
- stale module
- cold module
- event leak

If one of these fails, stop and record it before continuing to long-session testing.

### 3. AI Behavior Check

Watch several militia parties on the campaign map and look for:

- patrol behavior
- merge attempts
- recruit attempts
- flee under pressure
- swarm coordination when multiple militia parties are near each other

Useful commands:

- `militia.full_sim_test`
- `militia.full_sim_report`
- `militia.ml_status`
- `militia.doctrine_status`

What matters:

- AI should not freeze into one repeated fallback forever
- weak parties should not only flee; they should also try to merge or recruit when possible
- swarm should not fully suppress every other layer

### 4. Long Session World Health Check

Let the campaign run long enough to produce map pressure.

Track:

- total mobile party count
- zero troop or broken parties
- headless or invalid party remnants
- cleanup activity
- scheduler lag or tick storms

Warning signs:

- party count climbing uncontrollably
- many empty parties staying alive
- repeated cleanup without meaningful reduction
- logs filling with the same warning thousands of times

### 5. Save / Load Check

Before ending the session:

- make a save
- reload it
- run `bandit.test_run all` again
- compare `bandit.test_report`

You are checking for:

- broken module state after load
- lost telemetry or learning state
- repeated deferred initialization
- missing AI response after reload

## Main Runtime Commands

- `bandit.test_list`
- `bandit.test_run all`
- `bandit.test_report`
- `bandit.test_reset`
- `militia.full_sim_test`
- `militia.full_sim_report`
- `militia.failed_modules`
- `militia.system_status`
- `militia.module_status`
- `militia.watchdog_status`
- `militia.watchdog_check`
- `militia.ml_status`
- `militia.doctrine_status`

## What To Record During Testing

At minimum, keep these notes:

- real date and test build date
- campaign day and in-game season
- whether the session was new game or loaded save
- total session duration
- visible symptoms
- commands used
- paths to exported logs or CSV files

## High-Value Report Files

Typical useful outputs include:

- `Events.json`
- `session_summary.txt`
- `ai_decisions.csv`
- `sleep_analysis.csv`
- `CleanupHistory.csv`
- `BanditMilitias_FullSim.txt`

Read them together, not in isolation. A good analysis usually compares AI choices, party health, cleanup behavior, and overall session state.

## Common Failure Patterns

### Party Inflation

Symptoms:

- total party count grows too fast
- cleanup is active but not reducing pressure enough
- empty parties stay alive

Likely areas:

- `PartyCleanupSystem`
- spawn throttling
- invalid party destruction

### Sleep Logic Failure

Symptoms:

- sleep hours fall below zero
- parties remain sleeping forever
- AI appears stuck or unresponsive

Likely areas:

- sleep remaining hours clamping
- awake state transition
- scheduler interaction

### AI Fallback Lock

Symptoms:

- too many repeated evade or flee decisions
- very low decision variety
- weak parties never stabilize

Likely areas:

- `MilitiaDecider`
- fallback thresholds
- swarm pressure
- recruit and merge thresholds

### Patch Routing Failure

Symptoms:

- militia behavior looks fully vanilla
- expected AI telemetry never appears
- patch warnings show skipped targets

Likely areas:

- Harmony patch resolution
- AI tick interception
- patch registration during startup

## Minimal Release Gate

Before calling a build testable for others, try to confirm:

- runtime test hub responds
- no immediate dead-module state
- no runaway party inflation in the first long session
- no persistent negative sleep loop
- at least one save/load cycle survives

## Related Documents

- `Documentation/SystemFlowTree.md`
- `Documentation/AIArchitecture.md`
- `Documentation/AI_Assisted_Development.md`
