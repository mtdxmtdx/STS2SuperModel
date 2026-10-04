# Constructed native-tape action labels at the full-policy engineering boundary

`python/nosl/constructed_native_policy_v5.py` accepts the exact UTF-8 bytes of a
new `nosl.native-constructed-tape.raw-report.v1` report. Its source law must be
`constructed_under_explicit_label_tape_prior`. This is a declared constructed
experiment, not a natural default-start run. The bridge returns every raw record
as a `nosl.dataset.full-policy.record.v5.1` engineering fixture with source kind
`constructed_native_tape_action_fixture_v1`, `native_run:false`, `trainable:false`
and no producer receipt. It does not admit a corpus or authorize learning.

The full report is retained verbatim in each record's versioned evidence, including
all outcomes, execution counters, excluded/failed/unexecuted source attempts,
recipes, the frozen collection declaration and the exact runtime receipt. The
adapter hashes the exact whole report and the exact nested raw-record bytes. It
checks the exact embedded declaration, prior, source-generation and build-receipt
hashes and cross-binds their content to each row and attempt. These bindings are
integrity checks. They cannot authenticate the producer or prove that asserted
source hashes produced a particular binary.

The declared setup excludes actual source seeds and private state. The bridge
checks the recorded missing-run-start gap, incomplete run context and absence of
invented maps or natural combat indices. Only the unchanged full public packet
enters the model; source recipes, runtime information and outcomes remain in the
audit/evidence channel. A locally complete combat history does not establish a
complete natural run history.

## Target projection and accounting

Every action value, mask, quality, and allocated/completed/truncated/error/other
world count is copied unchanged. The raw per-action `requested_worlds`,
`candidate_worlds_returned`, and `executed_worlds` counters are checked against the
independent execution ledger and retained verbatim in the embedded raw evidence.
Those three execution-only fields are omitted from the normalized target object
because the existing full-policy loss target schema does not contain them. No
full-policy target schema has been relaxed.

The adapter independently evaluates each terminal outcome with the existing pure
Candidate objective math. Fixed combat-start HP/max-HP, the actual entry inventory,
resource events, permanent changes, completed settlement, and cleanup evidence
must agree. Every requested evaluation seed has exactly one outcome slot for
every legal action. True source masks must match the empirical outcome values;
false source masks remain false even if the arithmetic is known. Unknown resource
prices remain unresolved. Incomplete/error outcomes retain their mass and cannot
become losses, zero values, successful outcomes or normalized targets. There are
no invented plan targets or uncertified ranking pairs.

## Isolation and closed gates

The canonical source group is the conservative primitive RNG family. Its identity
excludes root selection and horizon, so changing the selected public revision
cannot split one source family. The full selection prior remains independently
bound. Every attempt's source/public aliases, including attempts without a record,
participate in existing isolation checks. Known roots retained after source cleanup
failure remain in this denominator. This metadata creates no producer receipt or
split ownership, and no source is deleted to improve a completion rate.

Historic protection remains INCOMPLETE. Existing historic backward and source-hash
evidence is immutable. The engineering subtype cannot enter natural candidate or
production admission, and fitting remains blocked regardless of positive action
value coverage. Its validator joins the training source-hash closure, not the
inference source set. No historical native guards are weakened.

## Forward-only inspection

After the reviewed native producer has retained a new bounded report with actual
source/runtime hashes, run:

```sh
python -B tools/check_constructed_native_policy_v5_forward.py /path/to/report.raw.json \
  --normalized-output /path/to/constructed.engineering.jsonl
```

The command validates and loads the normalized data, calls the actual full-policy
`batch_loss` under `torch.no_grad()`, verifies unchanged parameters and absent
gradients, and reports coverage plus the closed fit gate. It traps backward,
autograd and optimizer construction; it does not launch the native producer,
create a training session or save weights. The optional JSONL is loaded again
through `PolicyDatasetV5` before success is reported.

Integration tests require an explicitly retained native fixture directory through
`NOSL_CONSTRUCTED_TAPE_FIXTURES`. Missing artifacts cause a declared skip; synthetic
source receipts are never substituted. Unit mutation tests are adversarial parser
checks, not additional native runs or evidence of new outcomes.
