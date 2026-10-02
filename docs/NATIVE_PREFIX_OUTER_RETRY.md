# Bounded outer retries after clean prefix exhaustion

The Rewards-hybrid sampler implementation v4 changes one computational boundary.
A complete initial-prefix component that uses all K trials without matching its
fixed public predicate now consumes one outer attempt and permits the next fresh
outer recipe. It does not reset or extend the configured M outer attempts. Old
state-tape v7 behavior and all prior identities remain unchanged.

Only the preparation phase can use this rule: the component must be
`initial_prefix`, completed trials and RunSeed draws must both equal K, and no
selected prefix or replay world may exist. Native exceptions, callback/alias
failures, partial trials, cancellation and cleanup errors still terminate that
world call. A component's failed words remain discarded auxiliary work.

## Fixed-root law

The existing whole-prefix proposal redraws the RunSeed and its entire native
act/map oracle on every inner trial. Let b be its clean-miss probability and
`g_K = sum(b^j, j=0..K-1)`. Its successful subdensity is the original prefix
measure restricted to the fixed public event, multiplied by the root-constant
`g_K`; the all-clean-miss null outcome has mass `b^K`. The original prefix
normalizer and envelope still cancel. No estimate of the event probability is
inserted, and no downstream correction is removed.

Assume the preexisting corrected one-outer-attempt accepted-world submeasure is
`A(dx) = c * pi(dx)` for the declared posterior `pi` and root-constant c. If rho
is the probability of an outcome that permits another outer attempt, M complete
independent outer attempts emit

`A(dx) * sum(rho^j, j=0..M-1)`.

This adds only a root-constant multiplier. Adding clean component exhaustion to
rho changes completion probability without changing the conditional law of
returned worlds. Fatal errors are excluded from rho. Every next iteration draws
a whole independent auxiliary recipe; it never retains a latent seed or an
unsuccessful selected trace. Holding a latent context while retrying would in
general introduce a context-dependent normalizer and is not allowed.

This argument concerns the declared ideal independent-stream model. Domain-
separated deterministic generators implement it reproducibly; this is not a new
claim of exact independence under a finite seed posterior. It also presupposes
the existing kernels' correction and support assumptions and cannot repair an
incorrect component law or alias treatment.

## Work accounting and limits

Each null component remains a `component_budget_exhausted` audit row with its
outer index, auxiliary recipe, K, completed trials, word/cell totals, maximum
trial length and RunSeed draws. It contributes no selected tape cells and is
never a gameplay loss. If all M outer attempts fail, the call returns the usual
unresolved posterior-budget exception reporting M.

Fixed-root correctness does not establish unbiased accepted-by-deadline worlds:
a wall-clock cutoff can favor faster latent paths. Cancellation remains
unresolved. Likewise, success probability differs across public source roots;
keeping only successful roots changes the population distribution. Predeclared
source and evaluation denominators, failed roots and all incomplete mass stay
in reports. This change does not authorize production admission or a natural-
distribution claim.

Finite verification enumerates the eight-ticket alias fixture: K=2 produces
A=26/64, B=13/64 and null=25/64. Two outer attempts produce A=2314/4096,
B=1157/4096 and unresolved=625/4096, preserving the 2:1 posterior. A downstream
likelihood of1/2 for A and1 for B, proposed with its corresponding correction,
produces equal accepted masses5304/16384 and unresolved5776/16384. A future
observable distinguishing A/B therefore has posterior mean1/2. Native tests
also cover exact attempt accounting and retry of a previously observed clean
prefix-exhaustion case; existing native/partial/late-cancellation tests retain
their exception identity and stopping behavior.
