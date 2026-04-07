# AI Architecture

This document summarizes the actual AI-related systems used inside `Bandit Militias: WARLORD Edition`.

## Core Idea

The mod does not use one single AI model. Instead, it combines layered systems:

- strategic reasoning
- tactical decision rules
- scheduler-driven execution
- telemetry and learning support
- world-context and pressure systems
- patch-based integration into Bannerlord AI flow

## Strategic Systems

### `BanditBrain`

The main strategic coordinator.

- evaluates targets
- reads threat and pressure context
- shapes larger intent for militia and warlord behavior

### `WarlordSystem`

Handles warlord-level identity, control, and progression.

- warlord ownership
- promotion and decline
- larger coordination hooks

### `HTNEngine`

Used as an execution-oriented planning layer for converting high-level intent into action-like structures.

It is the campaign-side execution bridge used by the bandit AI patch. It does not replace Bannerlord AI outright; instead it decides when the mod should take control and when vanilla behavior should continue.

## Tactical Systems

### `MilitiaDecider`

The main tactical decision point for militia parties.

Typical outputs include:

- patrol
- merge
- recruit
- ambush
- evade
- flee

### `SwarmCoordinator`

Coordinates multi-party behavior when militia groups operate close together.

This is where swarm pressure and formation-style coordination appear.

### `AdaptiveAIDoctrineSystem`

Applies doctrine-style adaptation based on context and observed threats.

The doctrine choice is also forwarded into the mission layer, where `WarlordTacticalMissionBehavior` builds live HTN plans for battle formations.

### `WarlordTacticalMissionBehavior`

Mission-time tactical planner for warlord-led battles.

- primes a lightweight `WorldState` before planning
- converts doctrine choice into HTN compound tasks
- now uses active primitive-task precondition checks
- hands control back to vanilla battle AI once melee engagement starts

The currently wired live doctrine tasks include:

- ambush setup and hold
- Turan bait-and-collapse behavior
- defensive deep shield wall transitions for killbox / double-square style plans

## Learning, Telemetry, and Feedback

### `AILearningSystem`

Tracks decision context and exports learning-oriented data. In this project, learning is part experimentation, part telemetry support.

### Runtime Diagnostics

- `BanditTestHub`
- diagnostics reports
- full simulation exports
- regression tests

These systems are critical because they make AI behavior visible instead of leaving it fully opaque.

## World Context Systems

### `PlayerTracker`

Tracks player pressure, route habits, and other context used by decision layers.

### `FearSystem`

Spreads local pressure, fear, betrayal, and settlement-side response into the campaign layer.

### `SpatialGridSystem`

Supports fast local-world lookups and helps keep large map queries manageable.

### `PartyCleanupSystem`

Not an AI brain by itself, but essential for keeping long-session world behavior stable.

## Integration Layer

### Harmony Patches

The mod uses Harmony patches to intercept or redirect parts of Bannerlord's native bandit AI flow.

This is how the custom decision layers can influence selected parties without rewriting the entire engine.

### EventBus

The internal `EventBus` is the runtime glue between campaign systems.

- synchronous `Publish()` is used for immediate in-frame reactions
- `PublishDeferred()` is used for queued, budgeted follow-up work
- the deferred queue is pumped from `SubModule.OnApplicationTick()`
- subscriber registration becomes fully active after deferred bootstrap completes

In practice this means the bus is active in normal play, but intentionally delayed during early startup and intentionally disabled during emergency-stop conditions.

## What Counts As "AI" Here

In this project, "AI" means the combined behavior of:

- decision rules
- scheduling
- context tracking
- telemetry-backed tuning
- world simulation feedback

It is best described as a hybrid AI architecture rather than a single machine learning system.

## Related Documents

- `Documentation/SystemFlowTree.md`
- `Documentation/InGameTestingGuide.md`
- `Documentation/AI_Assisted_Development.md`
