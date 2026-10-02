# Native Quest card admission

The native `CardType` enum includes `Quest`. `DowsingRod.AfterObtained` adds a
`Dowsing` card with that type, cost -1, and `Unplayable`. The Python student
previously accepted only Attack, Skill, Power, Status and Curse, so a genuine
public decision packet failed with `UNSUPPORTED: card.type: unsupported value
'Quest'` before model encoding.

`CARD_TYPES` now supplies the same six-value list to the strict public schema
and the type embedding. The engine's `None` sentinel and unknown types remain
unsupported. No simulator rules or candidate legality are reimplemented.
`Dowsing.publicState.unknownRoomsEntered` was already explicitly allowlisted;
the public-state fields, types and privacy boundary are unchanged.

## Genuine public fixture

`tests/python/fixtures/public-v2-quest.jsonl` contains one complete public input,
including its full eight-event history and all five native legal candidates.
It was exported on 2026-10-02 from the existing Release/net9.0 worker DLL of
the clean frozen checkout at `5d41f3c90c883654725ade96b2f16b8f5e1fab6a`.
The export ran in a separate process with `DOTNET_PROCESSOR_COUNT=1`; no C# build
or change to the frozen generator checkout was performed.

DLL SHA-256:
`9f8232685cdfb027d0b3b44c1a6934049f8be6e4e426a6a7bc7b92ee7dac9644`

The exact reset command was:

```json
{"op":"reset","scenario":{"seed":"nosl-m5-pilot-5000-v4:source:639","deck":["WraithForm","StrikeSilent","StrikeSilent","DefendSilent"],"enemy":"TwigSlimeM","enemyHp":18,"hp":53,"relics":["DowsingRod"]}}
```

This is a declared constructed scenario, not evidence of a natural run. Its
seed is reproduction provenance only and is absent from the fixture/model input.
The command returned `status=player_decision`. Its `observation` and `actions`
were copied unchanged into the same public envelope used by `TeacherDataset`:

```python
public = {
    "schema_version": "nosl.student.public.v1",
    "observation": packet["observation"],
    "history_complete": True,
    "controller_context": {"status": "inactive"},
    "candidate_actions": packet["actions"],
    "legal_mask": [True] * len(packet["actions"]),
}
# Fixture bytes: json.dumps(public, separators=(",", ":")) + "\n"
```

No teacher evaluation, targets, audit fields, seeds or private state were added.
Dowsing is at hand slot 2 and in its real draw event. Its public quest counter is
the native string `"0"`. Native play slots are 0, 1, 3 and 4, followed by end turn;
there is no illegal Dowsing play candidate.

Fixture SHA-256:
`b262ff9eae5605255a828738660eb50ca9480e7bdf2fa01b7faceb188cbac441`

Raw reset response SHA-256 (before wrapping):
`f780370e51bd292e109c0fd936cdb43721fb85adc8d8502749032f4cf0ada1b9`

## Verification and compatibility

The regression uses the actual public fixture for full schema validation,
forward/backward through every output head, finite gradients, a nonzero Quest
embedding gradient, and unchanged parameter values. Its scalar test objective
is an engineering gradient check, not synthetic supervision or training.
It also checks native Quest play omission, untrained inference behavior,
None/unknown rejection in both hand and JSON draw history, private field
rejection, and strict rejection of the old five-type embedding shape.

With the unchanged pilot config, parameters increase from 976,710 to 976,838
(+128): the entity embedding grows from 2768 x 128 to 2769 x 128. Adding a sorted
vocabulary token also shifts later type indices, so old weights must not be
silently reused. Existing learned-bundle source fingerprints bind both
`schema.py` and `model.py` and reject the change before weights load; strict
state-dict loading additionally rejects the old embedding shape. No migration
or compatibility bypass is introduced. There are no trained weights in this
patch and no optimizer steps were taken.

Reproduce Python verification with the existing CPU environment:

```sh
PYTHONPATH=python OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 \
  ../.venv-nosl/bin/python -B -m unittest discover -s tests/python -v
```

The complete Python suite passed: 141 tests in 15.007 seconds, including the
five Quest regressions and existing learned-bundle source provenance guards.
The optional missing-NumPy PyTorch warning did not affect verification.

Generator, configuration, corpus and public identity code are unchanged. Apply
this isolated patch only after a generation run bound to the prior source
fingerprint has stopped.
