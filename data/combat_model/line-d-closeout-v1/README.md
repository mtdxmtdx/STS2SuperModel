# Line D joint closeout v1

## Result

The final candidate is the same-file, same-denominator Line D dataset at:

```text
D:\STS2BestChoice\work\line-d-joint-closeout\line-d-final-merged-dedup.jsonl
SHA-256 BD616CF875CD324DE278B7783CB05AA432E24A98642C5E666C4413AD0E8F8E21
```

It is a synthetic combat-root dataset. The new shard injects already-supported
potions and relics; it is not represented as a natural full-run distribution.

| Reliable denominator | With held potion | With >=2 nonstarter relics | Ironclad | Silent |
|---:|---:|---:|---:|---:|
| 53,370 | 11,169 (20.9275%) | 19,767 (37.0377%) | 28,671 (53.7212%) | 24,699 (46.2788%) |

Both exact integer checks pass: `5*P >= N` and `10*G >= 3*N`. The quality
gate separately passes with zero public leaks, missing stable IDs, malformed
rows, conflicting labels, duplicate warnings, and split violations.

The previous authoritative candidate remains an immutable historical input:

```text
D:\STS2BestChoice\work\line-d-d2-relics\d2-relic-merged-dedup.jsonl
SHA-256 25B50DCDB1FCBD253E124AD3329FCBE730613E13F1919B0AB85656591FB21796
N=49,414 P=7,213 (14.5971%) G=15,811 (31.9970%)
```

The new joint gate fails that old candidate specifically with
`potion_coverage_below_20_percent`; this is a current mutation-style detection
of the old data gap, not a claim about an historical gate run.

## Increment

The probe used 20 seeds and produced 163 unique rows, including 147 Reliable
rows that all contributed to both P and G. The formal shard used 520 seeds,
16 collector processes, 16 teacher workers, 14 turns, the two existing Line B
encounters, balanced character rotation, four supported potion pairs, and seven
supported relic pairs. It produced:

- 4,308 collected/materialized/labeled rows;
- 4,297 rows after batch deduplication;
- 3,956 Reliable, 341 Estimated, 0 Uncalculable;
- 11 identical duplicates removed, 0 conflicting public-state labels;
- 0 overlap with the previous 67,799-state candidate.

The exact configuration and every stage hash are in `generation-config.json`.

## Frozen holdouts

The immutable files and hashes are:

- core: `0BEB64C5C0A5E40073F1234356CD68A34716130D95699CFC68BF43281883A542`;
- potion: `F8F76551527BFD9A31C4E481CFA5D7018C7219EF46A365597CFDF43A007586C3`;
- relic: `B7018CB52EF95807403575B843BC0D234A717CEDAF0744186F900DBE7A4563C2`.

All frozen test/challenge episodes remain in their named split. No member of
any frozen test/challenge set occurs in train or validation. Specialist-set
overlap is reported rather than treated as an error.

## Acceptance evidence

- D1 effect mutation: the historical isolated FIRE_POTION `Damage + 1`
  injection exits 1 and reports the exact mismatch
  `enemy.enemy:SHRINKER_BEETLE:1.hp`; restoration returns a Reliable zero-diff
  result and restores the source SHA.
- OpportunityCost: the adapter uses the named `PotionOpportunityCost` entry,
  documented as a search-only prior pending empirical calibration. A new
  runtime test proves the value enters `PotionState` and accumulates into
  `PotionCostSpent` without consuming energy or card count. Bypassing that
  accumulation in an isolated Core worktree makes the test fail, and restoring
  the source makes it pass.
- PriorityHint: unsupported effects remain nullable. A new test invokes the
  real `CombatSearchSession.GenerateActions` consumer and distinguishes a known
  strong hint, unknown hint, and known zero hint. Mutating the old unknown-to-0
  path fails; restoring it passes. Unknown remains an ordering prior, not a
  Reliable promotion.
- Potion resource rules: Core tests cover no energy/card-play consumption and
  no Vigor use or consumption by potion damage.
- Injection: all 520 formal episodes have a public observation containing their
  injected potions and relics. Held potions are a dense list; an empty inventory
  is `[]`, while capacity is independently represented by
  `potion_slot_count` (observed value 3). The feature manifest hash remains
  `176416D08B9AE9381177F94940BB88EF6F93594EBDF0F8E4F39267AA403A74AB`.
- D2 mutation: the historical isolated Paper Phrog 1.75-to-2.0 mutation exits 1
  with `enemy.enemy:TERROR_EEL:1.hp`; restored behavior returns zero mismatch.
- Combination determinism: repeated CLI payload and public materialization are
  byte-identical. Teacher semantic hashes are identical with zero changed,
  missing, or added states; only the predeclared runtime metric fields differ.

## Remaining unsupported relics

These are intentionally not Reliable:

| Relic | Reason | Next action |
|---|---|---|
| STONE_CRACKER | Random draw-pile upgrade identities need a validated no-replacement chance operator. | Add and live-verify that chance operator. |
| GIRYA | Rest-site training count is not injectable or observable in the current CLI relic-state contract. | Expose a versioned public/runtime counter. |
| BOOKMARK | The retained-card cost reduction selects a hidden random card and lacks a verified chance operator. | Add the NOSL hidden-choice operator and live probe. |
| BIIIG_HUG | Shuffle-time Cinder generation needs an authoritative card template and stable generated identity. | Capture the template and stable identity contract. |
| GALACTIC_DUST | Star-spend event history is absent from the current shadow state and feature contract. | Defer until a compatible state contract exists. |
| HISTORY_COURSE | Previous-turn last Attack/Skill identity is absent from public history state. | Expose and verify the public history field. |

## Version and repository position

- Game: v0.111.0 / `41cef1ea`.
- `sts2.dll`: `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`.
- CLI protocol 0.2.0; trace schema 1; feature contract
  `combat-feature-v1` / row version `1`.
- Model repository baseline: `da18d66571c2674e6fea019bbc399c41e891b52a` on `main`.
- Core repository baseline: `e9b139561ab586febf2e8f12b0417beeae8a22ee` on `main`.
- No model training, feature/schema change, or Reliable promotion was made.
- Core has no configured remote. `shadow-simulator-core-e9b1395-line-d.zip`
  therefore carries the current Core and test source snapshot with this model
  repository. It excludes `Mod/`, game assemblies, and build outputs; its hash
  and restore command are recorded in `simulator-source-manifest.json`.

Machine-readable evidence is stored beside this document. Large JSONL files,
split shards, game DLLs, and model weights remain under the work directory and
are not repository artifacts.
