# Linked public map history on the native initial map

`NativePublicMapHistoryCondition` extends the existing first-slice certificate
with every consecutively certifiable act-zero public Map owner. The observed
roots 11001–11008 contain respectively 1, 1, 1, 5, 1, 2, 4, 1 slices. Every slice
is detached public evidence: current coordinate and icon, displayed destinations
and icons, ordinary edges among displayed points, option enumeration in the
prior's raw or coordinate-order profile, and the selected coordinate.

Each added Map owner must contain exactly its synchronous observed/chosen/ended
sequence, with a complete top-level owner in act zero. Its current coordinate
must equal the preceding Map choice, its floor must equal current row plus one,
and its offered destinations must lie in the next row. The owner-ending assets
are recorded inside `NaturalSourceCollector.ChooseMapPointAsync` before returning
the choice to the driver, hence before entering the selected room. They give the
actual HP/max HP used by `NativeSourceMapChoice.Choose` and the pre-travel
WingedBoots counter. Unknown remains the displayed icon, without constraining its
later hidden room outcome.

The travel projection exactly follows `MapTravel.GetTravelablePointsFrom`: active
WingedBoots offers all points in the next row; ordinary travel, exhausted/melted
Boots, or an empty next row uses the current point's Children. `RunState` omits
melted relics from hook listeners and WingedBoots is the only concrete free-travel
override. Choice uses the existing source helper, including the strict
`2*HP < MaxHP` rest priority. Slice projection uses `NativePublicMapSlice.Observe`.
No generated collection is reordered or edited. Raw and canonical observation
profiles keep their distinct predicates. Tests compare travel against native
`MapTravel` with actual native players/relics, including the boss-row fallback.
The projection avoids constructing a new run/player inside an active oracle
scope, where unrelated setup could consume words.

A missing boundary, gap, unsupported owner/act, broken coordinate link, unknown
Boots counter, incomplete choice-time assets, noncanonical option order under the
canonical profile, or observed GoldenCompass context stops extension. All
previously certified slices remain usable, including the legacy first-slice
certificate when its owner-ending assets are unavailable. The first-slice check
receives only a detached initial prefix so malformed later option order cannot
erase it. The actual root/evidence is never changed: native replay still checks
every original public event, including the unsupported suffix. This accelerator
neither admits an inconsistent root nor declares unsupported history irrelevant.

## Why the initial act-zero map stays unchanged

The certificate is pinned to repository revision `be97e50c7ba6293164e30b46b2dd40b83219a3bd`
and vendored simulator tree `47efd6ba32fe9acedd33232aababbb2f470f55f1`.
It admits only the existing ordinary native Silent A10 source, reviewed public
policy, and outside-combat scripts `nosl-natural-public-script-v2` or
`nosl-natural-public-script-v3`. It does not apply to imported/transplanted runs,
arbitrary custom acts, extensions, or an unreviewed source revision.

The argument excludes transient acquisition; absence from the final inventory
would not suffice. Source audit established the following closure:

- `RunState` generates the initial `StandardActMap` before adding players. The
  native act-zero pool is Overgrowth/Underdocks, whose Ancient pool is Neow alone;
  `RunState.GenerateRooms` adds no shared Ancients in act zero. Their ordinary and
  shared event lists contain no Ancient events. `Tezcatara` is in Hive's pool.
- `GoldenCompass.AfterObtained` is the only gameplay caller of
  `RunState.RegenerateCurrentMap`. GoldenCompass is Ancient, and its sole direct
  grant is Tezcatara's explicit pool. Neow's complete candidate lists contain no
  Compass. `NeowsBones` draws from the current Ancient's `AllPossibleOptions`, so
  its act-zero nested grant remains inside Neow's list.
- The character starts with RingOfTheSnake. Player `RelicGrabBag` excludes Ancient
  rarity, and the exhaustive Shared/Silent relic-pool lists also exclude Compass.
  `RelicFactory` draws from that player bag, falling back to Circlet, and
  `RollRarity` returns only Common/Uncommon/Rare. Reward, merchant, generic event,
  CrystalSphere and relic-generated rewards all use those factories/bags or
  explicit named grant pools without Compass.
- The shared relic bag uses `includeAllRarities: true`; claiming every bag excludes
  Ancient would be wrong. Its pool still excludes Compass, and its gameplay
  TreasureRoom consumer selects a Common/Uncommon/Rare rarity using `RollRarity`.
- Direct dynamic event pools (TrashHeap, TeaMaster, DollRoom, FakeMerchant) are
  explicit closed lists. `RelicCmd.Replace` has only the SwordOfStone-to-SwordOfJade
  gameplay caller. RelicTrader excludes act zero and uses the same restricted
  factory. ToyBox's temporary wax grants also use that factory. Obtain/replace
  commands preserve the supplied type; startup, commands, cloning, and excluded
  transplant code exhaust direct inventory insertion sites. There is no arbitrary
  relic-ID or global relic-enumeration grant in source gameplay.
- The only other map replacement hook is SpoilsMap, whose replacement and quest
  annotations are guarded by its private index, initially one. Constructor
  generation/pruning/postprocessing contain all other graph/type/coordinate
  mutations. No reachable act-zero effect calls them again. WingedBoots changes
  travel options and its counter only; Unknown resolution returns a room type
  without changing the icon; FurCoat stores private coordinates without changing
  map nodes.

Relevant source modules are `Runs/RunState.cs`, `Runs/RelicGrabBag.cs`,
`Factories/RelicFactory.cs`, `Rooms/TreasureRoom.cs`, `Rewards/RelicReward.cs`,
`Commands/RelicCmd.cs`, `Models/Events/Ancients/{Neow,Tezcatara}.cs`,
`Models/RelicPools/{SharedRelicPool,SilentRelicPool}.cs`,
`Models/Relics/{GoldenCompass,NeowsBones,ToyBox,SwordOfStone}.cs`,
`Models/Cards/SpoilsMap.cs`, and `Map/{StandardActMap,MapTravel}.cs` under the
vendored Core source. Actual counterexamples outside the admitted producer are
`RunState.Transplant` (point restoration) and `RunTransplantImporter`
(regeneration). Their exclusion must remain part of the source certificate.

## Joint proposal law and integration

Only `NativeInitialPrefixCondition` changes its public-map branch: it keeps the
original `PublicMap` API and adds `PublicMapHistory`, then uses the whole certified
slice conjunction in `MatchesMap`. The optional opening-roster act constraint is
unchanged. Every attempt still draws a fresh uniform RunSeed and fresh oracle and
runs the unmodified native act/map generation. Wrong-act map cells integrate out;
a correct-act candidate completes all native map passes before testing slices.
No prior identity, source action, simulator rule, or native tape ownership changes.

For fixed public root R let E_R be the conjunction of the certified observed act
(if available) and every certified map-slice predicate. E_R is a fixed necessary
event of full public replay. It uses observed HP/counter values as constants; it
does not draw or hold future latent game state. If b is the clean-miss probability
under the whole independent RunSeed/oracle prefix law, fixed-K successful
subdensity is `p(x) 1[E_R(x)] sum(b^j, j=0..K-1)`. The reciprocal is a root-constant
correction/envelope and cancels as before. Native errors are not clean misses and
external cancellation remains unresolved. No numerical normalizer or acceptance
rate is inferred. Full replay retains every later constraint.

The predicate never conditions raw path-start columns to equal surviving first
row columns: native pruning and postprocessing make that inference unsound.

## Predeclared component diagnostic (prepared, not run)

`configs/public-map-history-work-v1.json` fixes existing source draws 11001–11008,
the original raw observation prior, oracle seed 880015, 256 complete joint
candidates per root, and an absolute maximum of 2,048 complete candidates. Input
must be the existing hybrid-v6 eight-root report, SHA256
`599e331603211f235ac1eb8d126161783e5dc62e3bd95a8bd3f64cf4fa646eca`.
Only its detached public evidence is used for the predicate; no source worlds
are generated and no target, source recipe, or hidden map is read by the harness.
No fitting, new root population, sealed evaluation, or remote writes occur.

Every candidate gets a fresh RunSeed/oracle; the diagnostic duplicates the
existing pure native prefix construction and early wrong-act rejection. Report
every candidate, act survival, consecutive per-slice survival, native words,
distinct cells, timings, single-trial exhaustion and whether each fixed 256 block
has no success. There is no success quota, survivor replacement, or extra draw
after the fixed work. Native errors stop and surface; they are not exhaustion.
This measures a necessary component event, not full posterior throughput or
production training/admission.

After review, run the built benchmark with:

```
dotnet <benchmark-dll> --map-history configs/public-map-history-work-v1.json <existing-hybrid-v6-eight-root-report>
```

The exact existing public roots are also replayed in tests using the original
predeclared source draw coordinates, with expected slice counts pinned above.
Additional tests cover both initial acts/profiles, active/exhausted Boots,
melted Boots with unused charges, HP-dependent rest selection, Unknown icons,
native trace replay, and all listed
fallback boundaries. Those test fixtures are not a population measurement.
