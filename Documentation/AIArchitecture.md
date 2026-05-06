# AI Architecture

This document summarizes the hybrid AI architecture used in `Bandit Militias: WARLORD Edition`, specifically updated for **Bannerlord v1.3.15**.

## Core Idea: Hybrid Neural-Heuristic Architecture

The mod does not rely on a single opaque AI model. Instead, it combines deterministic heuristic rules with a lightweight, custom-built neural "advisor" system:

- **Heuristic Layer**: Provides safe, deterministic, and balanced decision-making. It is the primary driver of behavior.
- **Neural Advisor**: A lightweight FeedForward neural network that "observes" the heuristic decisions and provides confidence-weighted advice to fine-tune outcomes.
- **Strategic Reasoning**: High-level intent shaped by warlord identity and global pressure.
- **Tactical Execution**: Real-time reactive behavior managed by HTN planners and doctrine systems.

## Strategic Systems

### `BanditBrain`
The main strategic coordinator. It evaluates targets, reads threat/pressure context, and shapes the larger intent for militias.

### `WarlordSystem`
Handles warlord-level identity, control, and progression (tiers 0 to 6). It manages warlord ownership, promotion, and the overall coordination of assigned militias.

### `NeuralAdvisor` (New / Refined)
A purely C#-based, lightweight neural network layer that operates as a parallel advisor.
- **Tier-based Confidence**: Only higher-tier warlords (Warlord+) utilize neural advice.
- **Blend Logic**: Neural outputs are blended with heuristic scores using a confidence-weighted formula.
- **Zero Dependencies**: Does not require external libraries (Torch, TensorFlow, etc.), ensuring stability across Bannerlord versions.

## Tactical Systems

### `MilitiaDecider`
The primary tactical decision engine for individual militia parties.
- **Outputs**: Patrol, Merge, Recruit, Ambush, Evade, Flee.
- **Role-based Logic**: Decisions vary significantly based on whether the party is a *Guardian*, *Raider*, or *Captain*.
- **Fallback Matrix**: Robust state-aware logic for weak or overdue parties (e.g., merging under threat).

### `SwarmCoordinator`
Coordinates multi-party behavior when militia groups operate in close proximity, implementing "Swarm Pressure" and formation-style coordination.

### `AdaptiveAIDoctrineSystem`
Applies doctrine-style adaptation based on context and observed threats. Doctrine choices influence both campaign-map behavior and mission-layer formations.

### `WarlordTacticalMissionBehavior`
Mission-time tactical planner for warlord-led battles.
- Primes a lightweight `WorldState` before planning.
- Converts doctrine choices into HTN compound tasks.
- Hands control back to vanilla battle AI once melee engagement begins to ensure performance.

## Telemetry and Diagnostics

### `AIDecisionLogger`
Logs all tactical decisions, including the source (Heuristic vs. Neural) and the calculated confidence scores. This data is critical for balancing and debugging.

### Runtime Diagnostics
- **BanditTestHub**: In-game developer dashboard.
- **Full Simulation Reports**: Automated CSV exports for balancing warlord progression.
- **Regression Tests**: A suite of 229 tests ensuring architectural stability and preventing event leaks.

## Integration Layer

### Harmony Patches
Intercepts or redirects Bannerlord's native bandit AI flow, allowing custom decision layers to influence specific parties without breaking engine compatibility.

### EventBus
The runtime glue between systems, supporting both synchronous `Publish()` and asynchronous `PublishDeferred()` for performance-critical updates.

---

*Updated for v1.3.15 Compatibility - April 2026*
