# Recovery checkpoints

The execution workspace was rolled back twice on 2026-10-03. The durable source
baseline for this recovery is published commit
`81127dcd96ebec2d7e0238a7adbbd351bb0ee3f6`. Later source is being reconstructed
from retained authored patches and independently checked. Historical test and
diagnostic results do not certify this reconstructed checkout.

## Complete public map channel

Restored the separate version-two public evidence contract, explicit complete or
missing current-map captures, strict version/profile validation, canonical graph
projection, and recorder wiring. The frozen version-one channel is retained.

Fresh validation: `PublicCompleteMapObservationTests`, 10 passed, 0 failed,
0 skipped. The build emitted no warnings or errors. Run with:

```sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~PublicCompleteMapObservationTests'
```

This checkpoint does not yet restore the later map-prior reconstruction,
conditional sampler composition, version-five student pipeline, or its native
benchmark. No new production data generation or model optimization was run.
