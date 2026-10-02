# M3 terminal objective and calibration evidence

Historical validation date: 2026-10-01. The archived-corpus sections below preserve
that checkpoint. Current evidence also includes the bounded Hunt and potion
integrations and the confirmed 2026-10-02 preferences described here; historical
statements that all death-risk preferences were unknown do not supersede them.

## Current preference update, 2026-10-02

The user confirmed two comparisons: B (99% win at 70 HP / 1% death) over A
(certain win at 50 HP), from 70 HP with unchanged resources; and consuming
FirePotion to win at 50 HP over retaining it to win at 38 HP in the specified
no-better-timing/no-refill context. The rare/high-value potion answer is tentative.

The [exact versioned fixtures](CONFIRMED_PREFERENCE_FEEDBACK.md) yield
`K < 1930 + (3510/7) * eta`; candidate K=1000, eta=0.2 agrees. The FirePotion
comparison supplies a parameterized upper bound on its future inventory value,
because starting HP was not specified. No item price is assigned. Forty-six
calibration tests pass. These examples constrain a family; a uniquely identified
coefficient is not required to run empirical pilots, and the feedback does not
by itself admit a formal calibrated profile.

Full resource calibration remains partial. Public Hunt/Regen context, complete committed HP/potion accounting and five native event owners are now implemented. Broader posterior/content coverage remains engineering/evidence work; choosing a number cannot supply that coverage. See the
[current acceptance checkpoint](M3_M6_ACCEPTANCE_CHECKPOINT.md).

The remaining historical sections distinguish the earlier original-checkout C#
contract/integration run from reconciliation of `engineering-200-v2`. The latter
tests saved arithmetic, not current simulator execution, the corrected public
boundary or independent original-client fidelity.

## Result

The candidate objective preserves the confirmed preference examples and 9/5/80 boundary contracts. **Calibration is still incomplete.** `K=1000`, `eta=0.2`, resource bounds, and all test-only resource prices remain engineering candidates. The configuration has `calibrated=false`; formal-label requests fail closed. These results support restricted engineering/pilot diagnostics, not formal training or full-game readiness.

## Implemented behavior

- `src/Nosl.Objectives` is a pure DTO/math library with no simulator dependency. It records true win/loss separately from truncation, engine errors and policy nontermination, retaining every allocated world.
- Settled HP defines net loss, using the fixed combat-start reference. Damage/healing diagnostics, turn count and atomic action count are not extra rewards or penalties.
- Inventory is priced once, as start minus retained end inventory. Equal unknown inventory cancels on wins. On loss, end inventory and permanent benefits have no retained future value. Consumption/generation events are provenance, not another charge.
- Changed unpriced inventory or permanent benefits have null objective values and explicit reasons. Both inventory snapshots and the permanent-change ledger require explicit completeness declarations. Missing max-HP changes are detected. Invalid terminal facts cannot receive valid targets.
- Profile values require finite bounds and evidence. Aggregate resource adjustments beyond configured bounds stay unresolved. These resource bounds do not establish a universal bound on HP utility.
- The 9-HP potion gate returns consideration, not an instruction to drink. Exact 5 extra expected HP loss and exact .8 unconditional success are inclusive. Intervals crossing a boundary stay unresolved; safety failure rejects a bonus plan.
- Anchored finite plans preserve the original public anchor, baseline version, template and deadline. No per-turn budget reset exists. Multi-turn healing plans require a separate finite deadline and remain unresolved without their own evaluation; arbitrary conditional counterfactual budget management is not implemented.
- Fixed-sample Hoeffding intervals require the predeclared final sample count and supplied finite support. They are not valid for repeatedly peeking and stopping. Batch probability bounds describe unresolved empirical outcome mass, not confidence intervals for the population.

## Archived 200-root corpus: completed evidence pass

Source: `artifacts/data/engineering-200-v2`. Reproduce without rebuilding or executing the simulator:

```sh
python -B tools/calibrate_objective.py --corpus artifacts/data/engineering-200-v2 --output configs/objective_calibration_engineering200.json
python -B -m unittest discover -s tests/calibration -v
```

Result: **21,180 per-record reconciliation checks passed; 16 evidence-tool mutation regressions passed**. The original 30 contract tests were rerun successfully in `docs/spec/v4`. The original archived generator hash matches its declared fingerprint, every record matches the archived generation configuration, and all five source files remained unchanged during the audit. The JSON report retains their SHA-256 fingerprints and the archived runtime hashes.

Observed facts:

- 200 accepted public roots come from 68 source-run/combat groups; 800 root-world draws were copied across 1,041 candidate actions, producing 4,164 action-world outcomes
- The accepted records contain 4,095 true wins and 69 true losses, with no missing action-world records. Nine source attempts failed separately: five response deadlines and four ended-before-requested-boundary resets. They are retained in the attempt journal, not counted as game losses. Accepted-root completion is not an unconditional success rate across failed source attempts
- 977 action utility targets reconstruct from settled HP, actual loss flags and fixed combat-start HP within a maximum absolute error of `3.552713678800501e-15`; all 64 unpriced action targets remain null with their value masks disabled
- All raw terminal HP, win/death, HP-distribution and inventory-count auxiliary targets reconcile. Both inventory and permanent-change snapshots are declared complete for all outcomes. Seven potion identities occur; resource value remains unresolved where inventory value changes or retained inventory is lost on defeat
- 44 outcomes preserve an unpriced `earned_extra_reward_opportunity:GoldReward` fact. They describe offered extra-reward opportunities, not claimed gold or a measured designated-finish success probability. Their utility is masked even though persistent asset snapshots are unchanged
- 24 outcomes have negative net HP loss. None changes maximum HP. Every healing-received diagnostic is null, and every HP-event completeness and resource-provenance completeness flag is false. The corpus therefore cannot validate the complete damage/heal or consumed/generated/discarded event ledger
- No root has current HP different from combat-start HP, so this corpus does not exercise the important anti-reanchoring case after prior HP loss. The existing unit test and dedicated integration scenario remain separate evidence
- All designated-finish, earned-bonus and deadline outcome fields are null. There is no empirical finite bonus-plan probability or anchored counterfactual budget estimate in these records

The report audits every root and candidate, including utility-masked actions. Sensitivity summaries use only the **177 roots where every candidate's utility is resolved**; the other 23 are excluded from comparative utility analysis explicitly, not assigned invented resource prices.

Across those 177 roots, empirical minimizing action sets do not change over `K ∈ {100,1000,10000}` and ten eta values from 0 to 12. This is descriptive stability on a mostly simple corpus, not evidence that any coefficient is correct: it cannot even distinguish eta values that contradict one of the confirmed synthetic preferences. No empirical minimizing set is called a proven equivalent-action set or an optimal-policy result.

The tool also retains 80 paired immediate-potion versus inventory-preserving candidate contrasts without picking a sample-best baseline. Their four-world point estimates include 53 below 9 HP, 12 exactly 9 and 15 above 9. These are descriptive whole-rollout comparisons under the saved continuation. Every contrast remains `UNRESOLVED_NO_CERTIFIED_SUPPORT_OR_FIXED_BASELINE_PLAN`; four identical observations do not establish an exact expectation or justify a drink-now recommendation. Baseline and potion losses are both retained.

The source uses archived `nosl.public.v2` observations. The runtime boundary has since been corrected and verified in the separate 856-case integration suite; this archived corpus itself remains at its old fingerprint. **This report approves no use of that corpus for current training or corrected-boundary validation.** Arithmetic evidence can be preserved while affected public inputs and labels are regenerated under a new fingerprint.

## M3 acceptance disposition and finite next work

The exact M3 passing criteria in `PLAN_NOSL_FULL_COMBAT_V4.md` are narrower than claiming a fully calibrated model. The current disposition is:

| Criterion | Evidence closed now | Still open |
|---|---|---|
| Preserve confirmed risk preferences | Original 30 contracts, candidate costs and sensitivity checks pass | No unique K/eta identified; small-death-risk tradeoffs remain unconfirmed |
| 9 means consideration only | Inclusive synthetic boundary, rescue integration and uncertainty behavior pass; archived potion contrasts are retained without eligibility claims | A certified whole-policy marginal estimate for each relevant item and explicit exception valuations |
| 5 is a whole anchored-plan budget | Immutable-anchor/deadline and no-restart unit tests; actual 5-HP delayed-branch difference | Real bonus template with a frozen robust baseline, failure exits and whole-plan evidence |
| Separate exact .8 from uncertainty | Exact synthetic .8 is admitted; crossing intervals remain unresolved | Saved bonus success/deadline facts and either exact finite-support enumeration or adequate fixed final samples |
| No HP/resource double counting | Earlier real healing/maxHP/consumption integrations and archived utility reconstruction pass; unpriced resources stay masked | Complete event provenance; non-maxHP persistent effects and broader interaction evidence |
| Decide formal-label profiles | Decision is explicit: **none admitted** | Candidate remains calibrated=false and resource/permanent-future tables are unresolved |

Within the proposed quadratic family, the two strict all-win examples imply only `0 < eta < 150/13 ≈ 11.53846`. They place no constraint on K. This is a constraint on a proposed family, not user endorsement of that family or a fitted risk coefficient. At fixed 95% Hoeffding coverage, four successes out of four have a lower bound of about .321, far below .8; even 80/100 is an estimate with a crossing interval rather than an exactly known .8 probability.

Finite follow-up work that can improve evidence without inventing preferences:

1. After the public-boundary version is fixed, regenerate a bounded accounting corpus and rerun this audit with its new fingerprints. Keep failed attempts and all allocated action worlds
2. Complete damage/heal and resource-movement instrumentation, then reconcile snapshots to events across healing, maximum-HP changes, consumption, generation and discards. Until then leave completeness flags false
3. Add a finite designated-bonus template with the initial public anchor, immutable next-turn deadline, fixed baseline and explicit actual-benefit/failure events. Use a truly enumerable belief support for exact boundary cases when feasible; a sample fraction of 4/5 alone is not proof of probability .8
4. Exercise partial-plan updates after HP changes and expiry/failure exits, checking that no budget is renewed and no deadline moves. Safe multi-turn healing requires its own finite template
5. Maintain versioned unresolved resource entries and obtain defensible future-value/preference evidence before any formal-profile admission. More outcome rows alone cannot decide a user's death-risk tradeoff or identify an unobserved item's future value

No item prices, coefficients, runtime semantics or training authorization were changed by this analysis. Machine-readable disposition is in `configs/objective_calibration_acceptance.v1.json`.

## Earlier executed C# and synthetic checks

From the repository, after sourcing `../.dotnet-nosl-env.sh`:

```sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter FullyQualifiedName~ObjectiveTests
```

Result: **21 passed, 0 failed, 0 skipped**, exit 0. Three tests execute the actual C# simulator:

1. Immediate versus next-turn Fire Potion: both win, both consume the same potion, and the delayed branch loses exactly 5 additional HP; the source branch remains unchanged
2. Meat on the Bone plus Chosen Cheese settlement: final HP/max HP are 51/101 from 38/100, settlement is idempotent, and unknown permanent value remains unresolved; an explicitly test-only price checks absence of HP double counting
3. One-HP rescue: Fire Potion wins while ending the turn produces a true loss; the 1-HP difference permits the verified-rescue exception below 9

These scenarios validate terminal facts and accounting. They do not establish a real-world .8 bonus-success distribution, validate all resource interactions, or calibrate prices.

From `docs/spec/v4`:

```sh
python -B -m unittest discover -s tests -v
```

Result: **30 passed**, exit 0. The original contract implementation and tests were not changed. An earlier attempt from the repository root failed because `contracts` was outside Python's import path; using the documented package working directory resolved it.

From the repository:

```sh
python -B tools/calibrate_objective.py
```

Result: **10 boundary checks passed**, confirmed examples preserved, `calibrated=false`, formal labels blocked. Reproducible output: `configs/objective_calibration_report.json`. The tool imports the original synthetic contract; it does not simulate game rules or assert C# test execution.

## Sensitivity and remaining gates

The candidate assigns costs 8.21333 to fixed loss 8, 3.3 to the 90% loss-0 / 10% loss-30 mixture, and 3.03 to fixed loss 3. Many positive eta values preserve both inequalities; eta=0 loses the strict equal-mean preference and eta=12 reverses the lower-mean example. These examples do not identify a unique risk model.

The report also varies defeat cost and very small death probabilities. Resulting rankings change, demonstrating an unresolved preference rather than proving any choice is correct. No inferred micro-death-risk tradeoff is promoted to user preference.

Unresolved gates include versioned potion/permanent-future values, additional risk-preference evidence, non-max-HP permanent-change instrumentation outside the tested scope, independent client fidelity, and full content coverage. A later broader adapter must explicitly account for other permanent changes or mark its ledger incomplete before producing utility targets. Auxiliary HP/win targets can remain usable when resource utility alone is unresolved; invalid or incomplete outcomes must remain masked.

No training was executed by this M3 work.

## Later bounded plan integration

The opt-in finite TheHunt evaluator now supplies actual paired baseline/plan
terminal facts, fatal/extra CardReward/deadline observations, and fixed-N missing-mass
intervals. Twelve simulator integrations cover immediate and next-turn success,
exact observed5HP delay, an anchor after prior HP damage, missed deadline, safety
exit, death, truncation and source independence. This closes the earlier lack of
any real finite-template execution evidence, not general bonus-controller support
or unknown risk/item calibration. See M3_ANCHORED_HUNT.md.
