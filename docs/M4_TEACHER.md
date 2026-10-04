# M4: public-information full-combat teacher

This implementation is an offline development teacher, not an optimality proof or a deployed policy. Rules and terminal settlement remain in the pinned simulator.

## Information boundary

`IPublicContinuationPolicy.Choose(DecisionPacket)` receives only public DTOs. T0 uses `nosl-public-rules-v1`. T1 explores a bounded public-information UCT tree keyed by the entire public observation, knowledge/history and candidate tokens; it does not key nodes by private states. The tree is frozen before final evaluation. Equal public information therefore selects the same frozen action across hypothetical worlds; subsequent observed draws can legitimately change decisions.

One belief world is independently sampled and cloned for each legal root candidate. Every candidate is retained. UCT terminal values are backed up only for completed, scored trajectories; unresolved resources and engineering errors are never zero-valued rewards. Unvisited tree actions are not silently claimed optimal. Leaf continuations run to actual settled terminal states, subject to an explicit computation budget.

## Independent labels and statistical limits

Exploration and final-evaluation sampler seeds are unique and disjoint. Final sample count is fixed before looking at labels. All assigned worlds appear in the accounting, including `ComputeTruncated`, `EngineError` and nontermination if ever proven. There is currently no general semantic nontermination certificate or macro accelerator; hitting a budget is truncation, not a proof of nontermination or game defeat.

Point mean-utility/HP/probability targets use all allocated worlds only when all relevant trajectories are valid and complete. Utility additionally requires complete ledgers and resolved values. A candidate can retain HP/win supervision while its resource-sensitive utility stays null. Sample means are estimates, not calibrated certainty.

`TeacherRanking` offers fixed-N paired Hoeffding bounds with a Bonferroni correction across candidate pairs, only when a finite utility-support certificate is available. The limited certificate uses proven maximum-HP growth mechanisms and bounded objective adjustments. Broader content without such evidence has no strong pairwise targets. Overlapping intervals never imply true equivalence. In small pilots, complete value regression can train the same ranking score even when conservative pairwise targets are empty. No probability-of-superiority replacement objective is used.

## Remaining boundaries

- Candidate K/eta and unresolved potion/permanent-future values are not user-confirmed calibrated prices
- General finite bonus plans, stable policy quality, macros, full client fidelity and pure-student safety are separate gates
- T1 is a finite, budgeted policy improvement experiment; it can be worse than T0
- Dataset audit stores teacher version, frozen continuation digest, objective version, sampler seeds and costs separately from model input
- Sampling or source-prior limitations must remain explicit rather than reverting to a private exact world
- Whole-combat resource and permanent-change capture must be revalidated when integrating the broader adapter

The tests include actual simulator rollouts, hidden-order/RNG replacement invariance of final outcomes under T0/T1, clone source preservation, settled healing, budget accounting and explicit production gates. Synthetic ranking-bound tests are separately identified and do not prove simulator fidelity.
