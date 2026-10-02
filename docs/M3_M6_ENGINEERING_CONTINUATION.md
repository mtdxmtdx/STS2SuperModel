# Continued M3–M6 engineering verification

This checkpoint follows the published 8fae1ad engineering snapshot. It closes
specific implementation gaps while preserving the original datasets, sealed
holdout, experimental weights and default worker binaries. It does not declare
full M3–M6 acceptance or restart the failed scale gate.

## Implemented and independently reviewed

- [Outcome accounting](OUTCOME_EVENT_ACCOUNTING.md) observes actual committed HP
  loss/healing/direct sets/max caps and potion generation, consumption and discard.
  Each branch owns an immutable copied prefix. Unknown removal stays incomplete;
  diagnostic damage is never charged twice
- [Forced events](FORCED_EVENT_LIFECYCLE.md) use the actual native owner, return and
  reward paths for BattlewornDummy, DenseVegetation, PunchOff, FakeMerchant and
  TheLanternKey. Ordinary unresolved rewards prevent premature owner return.
  Native offered choices remain unselected and are not treated as acquired assets
- [Native carry-in](NATIVE_BELIEF_EXTENSION_V2.md) reconstructs reviewed public
  monster/power memory and independently samples unknown order and future RNG.
  [Pending choices](NATIVE_CHOICE_REPLAY.md) replay an owned stable origin and
  condition every public suffix packet; they do not clone a suspended coroutine
- [Generation potions](NATIVE_GENERATION_POTIONS.md) admit untouched held Attack
  and Power potions at reviewed stable Silent/A10 roots. Actual eligible pools,
  all 39 generated descendants, hand overflow, cancellation, delayed choices and
  settlement have native tests. Consumed histories and generated pending imports
  remain explicit exclusions; no root card/power allowlist was silently widened
- [Public student v2](STUDENT_V2_ENGINEERING.md) validates and encodes public entry
  assets, forced events and finite Hunt anchor/deadline/status/history. Production
  preparation protects source groups and frozen split aliases before filtering.
  The bounded trainer binds exact source/runtime/data identities, keeps unresolved
  costs and masks, and exports standalone bundles. Action applicability reflects
  actual committed objective supervision; untrained and unvalidated plans abstain

## Current native diagnostic result

The original 200 roots represent six runs, 26 source battles and 15 encounter
families. The versioned increment supports 174 roots and all 2,222 allocated
candidate/world continuations settle. All source inputs/traces and all 150 older
supported target payloads remain unchanged. The 26 rejections are CorpseSlugsWeak
(16), TwoTailedRatsNormal (8), and RubyRaiders (2).

There are 494 empirical objective action targets and 617 masked values. All 174
complete-ranking masks remain false. This is not evidence that every supported
root supplies a usable full-objective preference. The frozen T0 cancels optional
card generation; its 378 newly admitted continuations are distinct from the
separate nonempty generation tests. Every inspected native record stays
non-trainable. A separate fresh collection/admission operation is being completed.

## Verification and reproducibility

| Check | Result | Scope |
|---|---:|---|
| Integrated native suite | 1,037 passed | Current merged source through 65bdfb3 |
| Current vendored Core | 4,480 passed, 3 opt-in skips | Complete run after observer/lifecycle hooks; later commits do not change Core |
| Data preparation | 140 passed | Includes v2 protected append and provenance rejection |
| V2 bounded trainer | 21 passed | Backward and optimizer steps forbidden |
| V2 student | 19 passed | Forward/schema/inference; earlier backward test deliberately excluded |
| Choice and provenance | 10 passed | Focused subset, not additional independent test count |

The earlier 185-test Python aggregate remains a separate source checkpoint.
Exactly one versioned production integration batch already exercised backward;
all weights remained unchanged, with zero optimizer steps. It was not repeated
by integration or independent review. The only fitted weights remain the earlier
497-step experimental pilot.

[The machine-readable report](../configs/m3_m6_continued_engineering_report.json)
binds each current report with its SHA-256. The vendored source reconstructs
byte-for-byte from pinned upstream plus the two documented local patches;
[packaging verification](../configs/vendor_patch_verification.json) covers all
2,227 tracked vendored files. Neither simulator remote is modified.

## Remaining work

The fresh native-pilot exporter/admission, separate finite Regen healing-stall
controller/context, and one native better-later potion comparison are active
bounded engineering work. They do not require new fitting or arbitrary item prices.

Broader native posterior/content coverage and calibrated resource/permanent/reward
values still prevent full-scope readiness. Unknown utility components stay masked.
The two authored preference comparisons constrain candidate values; they do not
identify a unique risk model or price table. The historical data-quality failure
continues to block scale and another fit.

General learned strength, original-client differential and final deployment-format
parity belong to later policy/delivery acceptance. No macro is used; macro expansion
proof is conditional future work, not a reason to label current atomic execution
incorrect. See the [milestone checkpoint](M3_M6_ACCEPTANCE_CHECKPOINT.md) and
[case map](M3_M6_ACCEPTANCE_MAP.md) for qualified statuses.
