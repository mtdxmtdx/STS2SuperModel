# Confirmed preference feedback, 2026-10-02 v1

This version adds two confirmed strict preference cases and one tentative answer.
It supplies constraints within the existing terminal objective family; it does not
complete M3 calibration, choose unique coefficients, assign resource prices, admit
formal-label profiles, or authorize training. The fixed candidate remains
`K=1000`, `eta=0.2`, `calibrated=false`, with empty inventory and permanent-value
tables. No default objective, T0 continuation, existing dataset or weight changes
are part of this feedback.

The source is [the versioned feedback file](../configs/preference_feedback_2026_10_02.json).
The probabilities below are exact assumptions in authored hypothetical questions.
They are not empirical rates, a reconstruction of 100 simulator worlds, or evidence
that any actual action has those probabilities.

## Verbatim questions and answer

Question 1:

> 初始70血、资源不变：A必胜并剩50血；B有99%概率获胜并剩70血、1%概率死亡。你更偏好哪个？

Question 2:

> 两种方案都必胜，本场后续没有更好的喝药时机，也不会自动补药：A用掉一瓶火焰药水，剩50血；B保留药水，剩38血。你选哪个？换成稀有／高价值药水时会改变吗？

Reply:

> 1偏好B 2偏好A，换成稀有药水可能偏向A

The first answer gives a hard `B > A` preference for the stated death-risk case.
The Fire Potion answer gives hard `A > B` for the stated potion context. “可能”
makes the rare/high-value-potion answer tentative: retain the leaning toward A,
but create no hard label, hard constraint, item identity, or price for it.

## Death-risk constraint

For the fixed combat-start reference and unchanged resources, the candidate
terminal cost is `K * 1[loss] + L + eta * max(L,0)^2 / H_ref`, where
`L = H0 - H_end` and `H_ref = max(1,H0)`. In question 1, `H0=70` explicitly:

```text
C_A = 20 + (400/70) * eta
C_B = (1/100) * (K + 70 + 70 * eta)

B > A  iff  C_B < C_A
       iff  K < 1930 + (3510/7) * eta
```

`3510/7` is approximately `501.428571`, not a rounded comparison coefficient.
At candidate `K=1000`, `eta=1/5`, exact costs are `C_A=148/7`
(approximately `21.142857`) and `C_B=271/25` (`10.84`); the candidate agrees.
The corresponding strict K upper bound is `14212/7` (approximately `2030.285714`).
Equality is a tie and fails the strict preference. The fixture checks both sides
of the boundary using exact arithmetic.

This comparison narrows the feasible coefficient family. It does not uniquely
identify K or eta, validate the entire quadratic family, imply a universal 1%
risk budget, or establish a preference for other rewards, starting HP, resource
losses or death probabilities. Older evidence describing all micro-death-risk
preferences as unknown predates this specific confirmed comparison; broader risk
calibration remains open.

## Fire Potion: parameterized bound, no assigned price

Question 2 does **not** state the initial HP. Its common combat-start value `H0`
remains an unknown parameter; question 1's 70 HP is not inherited. Let v denote
the future inventory value of the one consumed Fire Potion. Then:

```text
C_A = H0 - 50 + eta/H_ref * max(H0-50,0)^2 + v
C_B = H0 - 38 + eta/H_ref * max(H0-38,0)^2

A > B  iff  v < 12 + eta/H0 * (max(H0-38,0)^2 - max(H0-50,0)^2)
```

The last expression holds for every positive H0: when H0 is below 1 the two
positive-part terms are both zero, so the `H_ref=max(1,H0)` convention does not
change this difference. Positive-part terms matter if the endpoints include net
healing; do not expand the squares as though H0 were known to exceed 50.

For the **illustrative assumption only** `H0=70`, at candidate `eta=1/5`, the
strict upper bound is `2412/175`, approximately `13.782857`. At `H0=60` it is
`13.28`, and at `H0<=38` it is `12`. None is an assigned price. `v < 12` is
sufficient for the preference at every positive H0 and nonnegative eta; it is
not a necessary value inferred from the answer. For example, illustrative
`H0=70`, `eta=1/5`, `v=13` also satisfies the preference. These examples never
populate the candidate's empty resource-value table.

The numerical Fire Potion ranking remains unresolved because both H0 and v are
unspecified. Its confirmed preference and symbolic bound remain usable feedback.
The condition “no better later use in this combat, no automatic replenishment”
is preserved; it does not eliminate the potion's future value after the combat.

Inventory is charged once via start minus retained end inventory: the A arm loses
one bottle; the B arm retains it. Do not add a per-use fee or an extra 9-HP charge.
The existing 9-HP consideration gate is separate from the future-value price and
does not force use. Rare and higher-value items remain uncertain; this response
cannot fill a whole prices table or a universal exception rule.

## Reproduce the analytical checks

From the repository root, using the Python standard library only:

```sh
python -B tools/check_preference_feedback.py
python -B -m unittest discover -s tests/calibration -p test_preference_feedback.py -v
```

The tool always reads `configs/objective_profile.candidate.json` and the dated
feedback file. It loads decimals exactly, uses `Fraction` for weights, bounds
and strict comparisons, and cross-checks question 1 against the frozen V4 Python
objective. Its compact report is written under the ignored path
`artifacts/reports/m3-m6/preference-feedback-2026-10-02.json`, with source hashes.
The output option is restricted to that report directory. No fitting, sampler,
native game execution, empirical confidence interval or training runs here.

`checks_passed=true` means the fixture/algebra checks succeed and the candidate
agrees with the numerically identified death-risk case. It does not claim a
numerical potion ranking or full calibration. The report separately records two
hard preference cases, only one numerically evaluated hard case, zero simulator
worlds, the unresolved Fire Potion value, tentative rare answer, empty formal
profile admission and `formal_labels_allowed=false`. Ties or reversed death
rankings fail the check; malformed or inconsistent input is rejected.

Regressions cover exact boundary neighbors, ties, missing H0, net healing, the
sufficient-but-not-necessary bound, unpriced inventory, duplicate inventory
charges, tentative rare answers, invalid/inconsistent profiles, incomplete
probability mass, invalid outcomes, and preservation of the source files.

## Future relabeling under a new objective identity

Keep saved terminal vectors as facts: fixed combat-start HP, settled HP, true
win/loss, inventory snapshots with loss-aware retention, permanent changes,
settlement/continuation IDs, allocated-world ledger and validity masks. Keep
incomplete/error worlds and unresolved fields; do not renormalize or invent
prices to make utility targets available.

Once further feedback and item-specific evidence support a defensible profile,
create a **new objective ID** with its own versioned coefficients, value table,
evidence and admission decision. Derive a separate target artifact from eligible
saved vectors, binding the original source hashes and the new objective ID.
Preserve old target values, objective IDs, masks, dataset hashes and manifests.
Do not rewrite archived labels in place or mark this candidate calibrated merely
because these two examples have been recorded.

Keep the original frozen T0 continuation and its ID unchanged. Relabeling the
same terminal vectors evaluates a new utility under that same continuation; it
does not claim trajectories from a new policy or a new optimal continuation.
If a proposed objective needs facts absent from saved vectors, leave those values
unresolved or collect a separately versioned corpus. Formal-label admission,
broader M3 validation and training authorization remain separate decisions.
