# Experimental resource-policy abstention

`nosl.experimental_policy.ExperimentalPolicyGuard` is an optional, narrow
composition around the unchanged `nosl.inference.Inference`. It makes the first
pilot abstain at known unpriced-resource boundaries. It does not calibrate the
objective, repair missing labels, or promote the student.

## Why this boundary exists

The [first bounded pilot report](FIRST_BOUNDED_PILOT.md) records that every one of
the 44 potion-use and 44 potion-discard validation actions lacks a utility target.
All 193 valued potion-category candidates are play/end-turn actions; no potion
root supports a complete utility-ranking comparison. The learned utility head
can nevertheless extrapolate a numerical score for these actions. The raw
closed-loop student spent four additional potions relative to B0; those results
do not establish a net utility advantage or a price for the items.

The [candidate objective](../configs/objective_profile.candidate.json) has empty
inventory and permanent-future value tables and remains uncalibrated. A potion
consideration threshold is not a universal resource price. Auxiliary predicted
HP, win/death, or potion-count outcomes cannot supply the missing utility support.

## Behavior

The wrapper first delegates the exact input to the existing inference boundary.
Its strict schema, explicit terminal/waiting envelopes, model errors, untrained
status, and default rejection of unpromoted weights stay authoritative. Merely
enabling diagnostics does not enable experimental inference.

When explicitly opted-in experimental inference succeeds:

- If any legal candidate has native kind `potion` or `discard_potion`, the whole
  decision returns `POLICY_INAPPLICABLE`, `selected_action: null`, and
  `selected_index: null`. This applies even if the raw argmax is a play/end-turn
  action. It never chooses a potion-preserving substitute or silently falls back
  to B0
- Public history containing `potion_used`, or an accepted `action` with kind
  `potion`/`discard_potion`, also causes abstention. This evidence remains after
  the bottle is gone and during a pending choice, including a potion action that
  has not yet emitted its completion event. Absolute utility is anchored to
  combat-start inventory, so removing current potion actions cannot establish
  target applicability. Evidence-event indices are retained, never converted
  into a net inventory count: action and completion can describe the same use,
  and initial/gained inventory is not reconstructed from these events
- Public v2 `gold != startGold` also causes conservative abstention. These exact
  public fields identify an observed resource delta that the
  [asset ledger](../src/Nosl.Worker/CombatAssetSnapshot.cs) treats as a permanent
  change. Its unresolved future value makes absolute-utility target applicability
  unverified. This is not a proof that every relative candidate comparison is
  invalid: a prior sunk gold or potion change may be a common offset across
  future branches
- Illegal potion candidates do not trigger abstention. All candidates, legal
  masks, and original indices remain intact; nothing is filtered before inference
- Ordinary unchanged gold, relic presence, and potion ownership alone do not
  trigger these tests. Predicted resource deltas are never treated as observed
  evidence or used to invent a price
- Otherwise the raw selected action is retained only with
  `EXPERIMENTAL_UNCALIBRATED` and `certifies_applicability: false`. Passing this
  screen means no listed blocker was observed, not that the remaining content,
  interactions, choice rankings, or utility estimates are supported

The top-level `predictions` list is empty. Explicit `include_diagnostics=True`
or `--include-diagnostics` places unchanged raw estimates under `diagnostics`,
marked `usable_as_policy: false`. That object has no selected action or selected
index, including after abstention. Original candidate indices, illegal-action
rows, HP bins, and teacher-continuation semantics remain available for inspection.
Those predictions are not measured student rollout outcomes or calibrated
probabilities. Computing them internally never authorizes executing the raw
argmax. Consumers must honor top-level status and selection.

This version only admits the experimental base status; an apparent promoted
base response is outside this wrapper's scope and also produces no action.

## Use and compatibility

```sh
# Default still returns MODEL_UNVALIDATED for the first trained pilot.
PYTHONPATH=python python -m nosl.experimental_policy \
  --bundle artifacts/experiments/pilot-v4-first-trial < public-input.jsonl

# Explicit offline experimental selection, subject to whole-decision abstention.
PYTHONPATH=python python -m nosl.experimental_policy \
  --bundle artifacts/experiments/pilot-v4-first-trial \
  --allow-experimental --include-diagnostics < public-input.jsonl

# Schema/control-only inspection, with no weights.
PYTHONPATH=python python -m nosl.experimental_policy \
  --config configs/student.pilot.json < public-input.jsonl
```

Python callers can use `ExperimentalPolicyGuard(existing_inference,
include_diagnostics=False)` or `ExperimentalPolicyGuard.from_bundle(...)`.
The wrapper never changes the base runner's `allow_experimental` flag. It has no
simulator, teacher, search, or private audit input and takes no optimizer steps.

All existing fingerprinted Python sources, weights, manifests, default HP bins,
and frozen evaluator sources remain untouched. The old `nosl.inference` command
and archived evaluation are therefore still replayable as raw diagnostics. This
new command does not retroactively alter their results or automatically replace
the archived evaluation policy. A later evaluation using this wrapper must
declare the new policy and freeze the wrapper source separately; the original
bundle's implementation fingerprint does not include this newly added module.

## Deliberate limits

The public observation does not provide full combat-start permanent deck/relic
snapshots or a start-max-HP field. This guard does not infer them from initial HP,
current counters, or card names. It does not certify absence of future resource
generation, permanent effects, inventory loss on defeat, or effects triggered by
play/choice/end-turn. Generic permanent-state coverage is still unverified.
Legacy public v1 also lacks the gold comparison. Broader applicability requires
reviewed public evidence and appropriate target/continuation support, not a
calibration flag or a test that manufactures resource prices.

The caller is responsible for complete truthful legal-action enumeration. An
input that omits a legal potion candidate can evade this narrow screen; the
wrapper cannot reconstruct missing simulator actions. A rejected decision is
unresolved, not a game loss or permission to continue under a different policy.

## Focused verification

```sh
PYTHONPATH=python python -m unittest discover -s tests/python \
  -p test_experimental_policy.py -v
```

The tests use deterministic synthetic output fixtures, with no fitting. They
cover legal versus illegal use/discard candidates, raw potion and non-potion
winners, complete candidate preservation, opt-in diagnostic isolation, default
model rejection, choices, terminal/waiting, malformed/no-decision input, gold
gain/spend, prior potion events after an empty bottle or during a choice,
duplicate evidence without inventory reconstruction, unchanged resource presence,
and the standalone JSONL command.

The final guard check passed all 165 Python tests, including 17 guard tests. The
suite log is `artifacts/reports/m3-m6/experimental-policy-tests.log`. A separate
read-only check loaded the original learned bundle and both archived
`low-hp-poison` student revision-14 decisions: each has empty potion slots and
only play/end-turn candidates, but retained SwiftPotion use history. Both now
abstain, with all five diagnostic prediction rows exactly unchanged. The proof,
including trace, guard-source and weight checksums, is
`artifacts/reports/m3-m6/experimental-policy-revision14-proof.json`. All nine
original frozen source hashes still match. This check runs no simulator, teacher
evaluation, or training and provides no new held-out strength evidence.
