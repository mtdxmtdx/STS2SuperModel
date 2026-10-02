# Native forced-event lifecycle fixtures

The five previously blocked owners now execute through the pinned native event
state machines: BattlewornDummy, DenseVegetation, PunchOff, FakeMerchant and
TheLanternKey. This is constructed-event execution and conservative whole-setup
rejection support. It does not certify a natural-run carry-in posterior, price
event rewards, authorize formal labels, or establish full training readiness.

## Declared setup and native entry

`Scenario.ForcedEvent` takes an event owner, act, explicit option keys, a declared
fixture floor, and optional merchant inventory/potion actions. For example:

```json
{
  "seed": "event-fixture",
  "deck": ["GrandFinale", "GrandFinale", "GrandFinale", "GrandFinale", "GrandFinale"],
  "hp": 20,
  "maxHp": 100,
  "forcedEvent": {
    "event": "DenseVegetation",
    "act": "Overgrowth",
    "optionKeys": ["REST", "FIGHT"],
    "fixtureFloor": 6
  }
}
```

The bridge validates the native act/shared event pool, live `IsAllowed` gate and
every currently offered option. EventRoom precreates combat-layout encounters;
its pending-combat factory preserves PunchOff HP reductions and DenseVegetation's
four named Wriggler slots. No ordinary room, copied event reward rule or monster
HP replacement stands in for an event.

| Owner | Native act | Combat path |
| --- | --- | --- |
| BattlewornDummy | Glory | SETTING_1, SETTING_2 or SETTING_3 |
| DenseVegetation | Overgrowth | REST, FIGHT |
| PunchOff | Underdocks | I_CAN_TAKE_THEM, FIGHT; native floor gate applies |
| FakeMerchant | Shared, legal from Hive onward | Empty option path, owned FoulPotion slot |
| TheLanternKey | Hive | KEEP_THE_KEY, FIGHT |

FakeMerchant can inspect inventory and buy declared native relic slots before
using the FoulPotion. Opened inventory is recorded as public event history;
unopened stock stays private. Offered future rewards are never a combat policy
input. Setup does not choose a pre-combat reward: a path reaching one is rejected.

The fixed HP, maximum HP, gold, potion and permanent-asset anchor is captured after
native event pre-effects, before combat setup. DenseVegetation rest healing and
the FakeMerchant FoulPotion/purchases therefore do not count as combat gains or
costs. The floor positioning is a declared fixture, not a fabricated history of
played rooms. `FixtureOrigin` reports `constructed-native-event`.

## Settlement and remaining event return

The bridge follows native RunDriver ordering. Forced extras are queued after
combat entry and populated only by CombatRoom after ordinary reward generation.
Terminal scoring stops before the first postcombat reward decision, with zero
reward selections. Opportunity facts contain reward type/count/source and offered
gold, without reading future card, potion or relic identities for policy input.

- BattlewornDummy generates no ordinary combat reward. Settlement automatically
  exits combat and resumes its owner. The native hook grants automatic upgrades
  or exposes its potion/relic offer. Timeout/escape produces no tier benefit,
  even though the native combat result is a win
- DenseVegetation, PunchOff, FakeMerchant and TheLanternKey first expose their
  actual combat rewards. Event return stays pending, matching the native driver
  order. The host-only return helper refuses to cross unresolved offers. After
  explicit reward decisions it exits, restores EventRoom and resumes any waiting
  owner exactly once
- Losses generate no offers and return immediately through native exit/resume

Terminal facts are frozen before reward decisions. Later host return cannot
rewrite the scored endpoint. The event reward-opportunity ledger records
Battleworn's custom offers, and the existing extra-opportunity ledger records
PunchOff/FakeMerchant/LanternKey extras. Unpriced upgrades and opportunities retain
the objective's unresolved-value mask.

## Branch and posterior scope

`ForkExact` rejects these contexts because a CombatRunStateSnapshot cannot retain
an independent event owner, inventory, reward queues and return lifecycle.
`ForkForContinuationAsync` independently recreates the accepted world's exact
native event setup and action transcript, preserving that world's seed. Each
branch owns its actual RunState, EventRoom, EventModel, inventory and rewards.

Teacher posterior proposals replace the source seed and rerun the entire declared
setup, including event choices and pre-effects. Acceptance conditions on every
public decision packet. This uses `whole-setup-rejection-v1`, not a natural-run
certificate or the exchangeable fast path. A rare observed setup, including
revealed merchant inventory, can exhaust the proposal budget; that remains
computationally inconclusive, never a game loss or a fabricated accepted world.

## Verification

`ForcedEventLifecycleTests` includes native RunDriver differential checks for all
five owners and all three Battleworn tiers. Test-only checks compare complete
reward identities and reward RNG counters to catch generation-order drift; those
values are never copied into combat inputs. Legal GrandFinale fixtures win with
native monster HP. Further cases cover independent replay, timeout, real loss,
rest and potion anchors, purchased-stock exclusion, immutable settlement,
illegal-path rejection and bounded T0/T1 teacher branches.

On the isolated branch based on `8fae1ad`, the 25 focused event tests and all
969 Nosl tests passed, with zero failures or skips. The full regression used
`dotnet test tests/Nosl.Tests/Nosl.Tests.csproj --artifacts-path artifacts/forced-event-build
-m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false`.
Public event history kinds require the coordinated explicit student-v2 adapter;
legacy v1 readers remain unsupported for these event packets.

The only vendor change for this bridge is test-assembly friend access to invoke
the existing internal RunDriver event lifecycle. Native event rules are unchanged.
Builds and test outputs use `artifacts/forced-event-build`; frozen default binaries,
corpora and trained bundles are untouched.
