# Plan 008: Web Shell Session Semantics and Immutable Asset Serving

## Status

```text
Status: Engineering Complete — Frozen
Track: UI Modernization / UI-1.3
Depends on: Plan 007
Next: UI-2
```

## Scope

Correct the post-freeze Web Shell session, generation recovery, and runtime asset-serving boundaries
without changing Production, ModuleTest, module lifecycle, or the read-only Bridge capability set.

## Session semantics

Protocol v4 separates the one-use activation nonce from the generation-scoped session token. The
nonce activates exactly once and is then destroyed. The returned 256-bit session token authorizes
subsequent read-only ping and snapshot requests until reload, detach, process failure, or disposal.
Host and Mock enforce the same `PreReady -> ChallengeIssued -> Activated -> Disposed` state machine.
Every handler receives an explicit generation and session-cancellation context.

## Recovery and assets

Generation work is serialized. A process failure cancels and drains the old handshake before
starting recovery; stale Core events and same-generation duplicates remain ignored. Web assets are
verified once, capped at 256 files, 8 MiB per file, and 32 MiB total, then served exclusively from an
immutable memory snapshot. Asset roots, descendants, files, symbolic links, junctions, and all other
reparse points are rejected before reading. No virtual-host disk mapping remains.

## Verification boundary

Local protocol, frontend, immutable-asset, PowerShell 5.1/7, Debug/Release, real non-Mock repeated-ping
canary, frozen-core smoke, portable, installer roundtrip, and Preview upgrade checks pass. The pushed
implementation HEAD completed its exact GitHub validation successfully; UI-1 is frozen and UI-2 is next.
