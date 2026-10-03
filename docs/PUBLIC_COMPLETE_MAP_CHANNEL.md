# Complete current-map public observation channel

`nosl.public-map-complete-graph.v1` is an explicit observation profile paired with
`nosl.public-run-evidence.v2` and the existing public run context. The v1 evidence
grammar remains closed to the new field. Omitted/raw and
`nosl.public-map-options.coordinate-order.v1` map profiles retain their original
slice-only bytes and continue using v1 evidence. There is no migration or seed-based
backfill of old observations, prior identities, source records, or corpora.

## Contract

Each v2 `map` payload retains `current`, `nodes`, `edges`, and `options`, then adds
required `currentMap`. The additional `PublicCurrentMapCapture` contains exactly:

- `status`: `complete` or `missing`
- `nodes`: semantic coordinates and displayed node types
- `edges`: ordinary directed path endpoints
- `startingNode`: the special starting coordinate, or null when missing
- `bossNodes`: the special boss coordinates

The enclosing map owner's existing `actIndex` identifies the current act. A
starting node's special role is independent of its displayed icon: native act
zero starts with `ancient`, not an invented `start` icon. `unknown` remains that
displayed icon; no possible room, encounter, or event is resolved behind it.

Nodes, boss coordinates, and offered options are strictly ordered by row then
column. Edges are ordered by source row/column then destination row/column.
Complete captures require unique coordinates/edges, valid endpoints, forward
paths, an included start, and an exact nonempty set of displayed boss nodes.
Every node must be reachable from the start and able to reach a boss. The choice
slice's node types and all edges between its included nodes must agree with the
complete capture. These checks detect malformed captures; they cannot prove that
a producer did not omit a whole otherwise valid path. Completeness is a producer
attestation, separate from structural validation and run-history completeness.

`missing` requires empty node/edge/boss arrays and a null starting coordinate.
It does not carry a truncated graph disguised as complete. Selecting the profile
without providing a capture produces this explicit missing value. New consumers
must abstain from direct reconstruction when it is missing; they cannot recover
it by consulting a source seed or a hidden map snapshot from another channel.
V2 rejects an absent or null `currentMap`; v1 rejects a present capture, and its
strict wire reader also rejects an explicitly present `currentMap:null` field.
Both the evidence version and the declared observation profile are validated.

## Native producer preconditions

`NativePublicCurrentMapCapture.Observe` implements a declared simulator public
view at the native map-choice callback. It accepts only current act zero,
`StandardActMap`, seven columns and sixteen grid rows, an Ancient starting point
at `(3,0)`, one Boss point at `(3,16)`, no second boss, supported displayed icons,
and a structurally complete semantic graph. A different act, map class, shape,
unsupported icon, edge to an unobserved point, or malformed graph yields
`missing`. The existing missing-current-node callback still records an explicit
observation gap instead of inventing a map choice.

The native projection reads grid points, the two special points, their semantic
coordinates, displayed `PointType`, and ordinary `Children` edges. The grid's
`GetAllMapPoints` deliberately excludes the starting and boss nodes, so both are
included explicitly. Projection sorts detached arrays; it never changes native
collections, legal choices, source policy order, or random state. Neither
`CanBeModified`, generation counts, native insertion order, quests, encounter or
event identities, nor random seed/state/trace is exported. No game asset is
copied into this implementation.

The reviewed native StandardActMap source generates the act-zero map before
players exist; Overgrowth and Underdocks use the same fifteen-room input shape.
This producer records the already available semantic graph. It does not expose
the map-generation recipe. A separate label-only reconstruction and prior-law
implementation must establish its own support and runtime-sufficiency checks;
this channel alone does not certify a posterior sampler.

This is not a universal live-client or no-fog assertion. Mega Crit's
[November 2024 Neowsletter](https://www.megacrit.com/news/2024-11-07-neowsletter-issue-4/)
documents map icons and paths but expressly describes its screenshots as work in
progress. It supports those kinds of public facts, not a guarantee that every
current or future client exposes the full act map. External/live integration
still needs an independently reviewed visibility adapter and completeness
attestation before it can produce this profile.

## Verification scope

`PublicCompleteMapObservationTests` covers both native act-zero map definitions,
explicit special nodes, unresolved Unknown icons, all directed paths, canonical
bytes under native insertion-order changes, private mutability independence,
unsupported-view absence, malformed/completeness claims, profile/version/field
rejection, exact legacy slice bytes, strict wire round-trips, and a two-root
native source comparison. The comparison checks the same run outcome, source
trace, and public decision bytes after explicitly projecting the new field away
for the test only. Production consumers do not strip it to fit a legacy model.

Focused command (SDK 9.0.303; use a task-local artifacts directory):

```sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release -m:1 -nr:false \
  -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter 'FullyQualifiedName~PublicCompleteMapObservationTests|FullyQualifiedName~PublicMapObservationProfileTests|FullyQualifiedName~PublicRunEvidenceTests'
```

V1/v4 student consumers remain frozen. The v2 evidence must use its separately
versioned student input/feature path; it is not admitted by the old v4 whitelist.
No population growth, fitting, frozen-corpus rewrite, remote write, or live-client
connection is included in this observation-channel change.
