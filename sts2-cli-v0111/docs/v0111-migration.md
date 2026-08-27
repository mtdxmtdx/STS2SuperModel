# v0.111 Migration

## Target

- Game version: `v0.111.0`
- Game commit: `41cef1ea`
- Unmodified `sts2.dll` SHA-256: `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`
- CLI protocol version: `0.2.0`

## API changes applied

- `RunManager.SetUpSavedSinglePlayer` became asynchronous `SetUpSavedSingleplayer`.
- `CreatureCmd.Damage` now accepts `DamageVar` plus the `CardPlay` context for card-sourced damage.

## Runtime gate

`Program` emits the `ready` record with `game_version`, `game_commit`,
`assembly_sha256`, and `compatible`. It exits before constructing the game
simulator when any target value differs. This prevents unsupported assemblies
from generating training trajectories.

## Verification

```powershell
dotnet build .\src\Sts2Headless\Sts2Headless.csproj --no-restore
$env:STS2_GAME_DIR='D:\steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64'
pytest -q .\tests\test_v0111_consistency.py
```

The consistency suite checks the version gate, assembly identity, and equal
fixed-seed startup/map responses across two independent headless processes.
