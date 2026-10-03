# Public NewLeaf and LeadPaperweight proposals

These opt-in accelerators extend the existing independent native-word Rewards
and Map/Rewards priors. They read the complete typed initial Neow choice and its
immediate settlement, never a source seed, source recipe, private trace, or later
inventory inference. The declared prior and final full-public equality are unchanged.

## NewLeaf

The public child choice identifies one native unupgraded Silent starter card.
The complete candidate multiset must equal the initial transformable deck. Remove
the chosen public card once, then remove every unchanged card from the immediate
Neow settlement. Exactly one unupgraded replacement must remain. Duplicate
physical starter cards are retained in the public multiset; no hidden physical
index is inferred or conditioned.

`NewLeaf.AfterObtained` calls `CardCmd.TransformToRandom` with the run's Niche
stream. The default factory path chooses uniformly by native `NextItem` from the
original character's ordered single-player Common/Uncommon/Rare catalog, excluding
the original ModelId. This is one index word, without a rarity or upgrade word.
In particular, the native transform mass of a rare card is its index bucket mass,
not the much smaller regular-reward rare probability.

The added `LabelCardTransformScope` observes only the default factory overload.
Without a scope, the original native expression executes unchanged. With a scope,
the same candidate sequence, native index draw, clone and owner assignment run;
Core alone marks successful completion or stores the original native failure.
The proposal checks owned run/player, initial Neow, public original card, actual
ordered pool and Niche identity. It delegates the single forced raw word to the
owning tape's full-state freshness/replay guard. A native failure aborts only the
incomplete-word cleanup check, retains the original exception, and leaves the
proposal and tape failed. A missing completion marker, wrong pool/RNG/owner,
repeated callback or aliased cell cannot validate.

## LeadPaperweight

The complete two-card ordered offer is required even when the public policy takes
neither card. Its native `CardCreationOptions` uses the colorless pool, Other
source, regular base rarity odds and no suppression flags. Each of the two slots
consumes rarity, index, then upgrade words. All earlier selected ModelIds are
excluded before the next slot. The two upgrade words remain ordinary oracle draws.

The colorless catalog lacks common cards. Native `GetNextAllowedRarity` cycles
Common to Uncommon to Rare to Common until it finds a populated bucket. Thus a
displayed uncommon card can arise from both Common and Uncommon rolls. The
certificate and runtime checks retain every such latent rolled arm; they do not
replace fallback with a new rarity law. Base thresholds depend only on the public
A10 setting and do not inspect or change hidden rarity pity.

For each public identity, `NativeRewardIdentityMath` sums all matching native
float-rarity and double-index preimages, including duplicate positions if present.
It samples conditional words with their discarded low bits intact. The root-fixed
factor Z is the NewLeaf bucket mass or the product of LeadPaperweight's two slot
masses. The envelope is the same Z, so correction accepts without another draw.
This constant correction does not claim Z=1, eliminate ordinary upgrade cells,
or remove any remaining public constraint.

## Validation scope

Focused tests cover actual independent hypothetical Neow generation, complete
opening evidence, native common/uncommon/rare LeadPaperweight arms, untouched
upgrade words and pity state, missing unpicked offers and settlement, exact
continuation replay, both-prior routing, real full-state alias rejection, and
NewLeaf foreign/missing/repeated/failing native boundaries. A finite colorless
two-slot enumeration verifies forward fallback, ModelId exclusions and a later
predicate on an unconditioned upgrade word.

The retained Map fixtures 24104/24105 identify NewLeaf replacements Blur/Tracking;
24110 identifies the LeadPaperweight offer Shockwave/MindBlast. Fixed-recipe
integration checks isolate lifecycle and replay; independent opening tests exercise
the proposal law. Neither is a new posterior-throughput benchmark or admission gate.
