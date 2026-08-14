# Plan 016 — Web module host version boundary

## Goal

Close the concrete compatibility gap exposed by the QingTransfer Web migration: the public `v0.2.5-alpha` host predates `ModuleUiKind.Web`, while current `toolbox` and current Web modules already require the post-release Web module host contract.

This plan is intentionally narrow. It establishes a truthful host-version boundary and a release/upgrade validation path for Web modules. It does **not** reopen the Web module architecture, change B1/B2.1 transaction semantics, or add new module capabilities.

## Current mismatch

- Public `v0.2.5-alpha` only supports `ModuleUiKind.None` and `ModuleUiKind.Wpf`; `OutOfProcess + Web` is unsupported there.
- Current `toolbox` supports `ModuleUiKind.Web`, `webEntry`, `IWebToolModule`, Web ModuleHost windows, bridge, and presentation projection.
- Current QingTransfer declares `uiKind: Web` but still advertises a very old `minimumHostVersion`.
- `toolbox` still reports `0.2.5-alpha`, so two hosts with materially different module compatibility currently share the same visible SemVer.

That makes a user on the official `0.2.5-alpha` reasonably believe the latest QingTransfer should import, even though the released host cannot deserialize/validate the Web module manifest.

## 016A — Establish the version boundary

For the next host release candidate:

- advance the host version to the next Preview SemVer (expected `0.2.6-alpha`, unless release planning selects a later version before implementation);
- keep the already-published `v0.2.5-alpha` tag immutable;
- treat Web Module UI support as beginning at that new host version;
- update Web modules such as QingTransfer to advertise a truthful `minimumHostVersion` matching the first released host that supports their manifest/runtime contract;
- make old-host import failures user-readable as a host-version incompatibility where the package metadata can be inspected safely, rather than presenting only a generic import failure.

Do not backport the entire Web Module foundation into the already-published `0.2.5-alpha` release.

## 016B — Release/upgrade asset correctness

The next Preview installer must explicitly validate an in-place upgrade from the **immediately previous public Preview**, not an older hard-coded baseline.

This matters because the host Web UI uses hashed assets and `WebAssetIdentity` requires the installed `WebUI` tree to match the manifest exactly. A stale file from the previous release can cause `AssetFileSetMismatch`, at which point the Shell intentionally restores the native WPF workspace.

Required release checks:

- generate obsolete-host cleanup from the immediately previous public host payload manifest;
- update the checked-in installer cleanup baseline when advancing Preview versions;
- update CI upgrade resolution from the previous public tag (for `0.2.6-alpha`, that means `v0.2.5-alpha`);
- after in-place upgrade, verify obsolete previous `WebUI/assets/*` files are gone;
- verify every current host-payload manifest entry exists with the expected hash;
- launch the upgraded Shell and verify the **Web workspace reaches acknowledged Ready state**, not merely that the WPF main window becomes responsive;
- fail the release gate if the upgraded process silently falls back to native WPF because of asset identity, navigation, activation, repeated-ping, or WebView initialization failure.

The native WPF fallback remains a safety fallback for genuine runtime failure; the release upgrade test must ensure a normal supported upgrade does not accidentally take that fallback path.

## Scope limits

Do not use this work to:

- remove the native WPF fallback;
- redesign Web Shell readiness/attestation;
- weaken Web asset identity checks to tolerate stale files;
- change module update transactions;
- add a new Web bridge protocol;
- bundle modules into the host installer;
- introduce a generic compatibility framework.

Prefer correcting version metadata, previous-release cleanup input, upgrade verification, and user-facing compatibility reporting.

## Validation

A release candidate satisfying Plan 016 must prove:

1. official `v0.2.5-alpha` is recognized as too old for current Web modules;
2. the next host version imports a current QingTransfer package successfully when no duplicate module is installed;
3. the same package declares the new truthful minimum host version;
4. a real `v0.2.5-alpha -> next Preview` in-place installer upgrade preserves user settings/modules/data;
5. obsolete previous host Web assets are removed;
6. current Web assets pass host-bound manifest/hash validation;
7. the upgraded Shell reaches the Vue Web workspace Ready state and does not merely survive by displaying the WPF fallback;
8. fallback remains available for an intentionally corrupted/missing Web asset test.

## Stop rule

Once version reporting, immediately-previous-release cleanup, and upgraded-Web-workspace activation are proven, stop. Do not reopen the frozen Web Shell or module runtime boundaries unless a new P0/P1 defect is reproduced.
