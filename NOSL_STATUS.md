# NOSL implementation status

Current machine-readable status: [TRAINING_READINESS.json](TRAINING_READINESS.json).

M3–M6 engineering is implemented and tested for explicitly described scopes;
full formal readiness remains blocked by general native posterior coverage,
uncalibrated risk/resource values and broader bonus-controller support.

- Runtime: 902 integration cases passed; build zero warnings/errors
- Updated vendored Core: 4,479 passed, three existing opt-in skips
- Python student: 136 cases; data/report/identity: 97; generator recovery: 32
- Stable phase gate: 203 usable roots and 8 retained diagnostic-only roots;
  98 later-turn, 56 post-HP-loss, and 10 pending-choice roots
- The same frozen pilot corpus is growing toward 5,000 roots; bounded experimental
  fit is pending, with no optimizer steps or promoted model at this checkpoint

See [M3–M6 evidence and limits](docs/M3_M6_STATUS.md),
[calibration disposition](configs/objective_calibration_acceptance.v1.json),
[phase, recovery and split integrity](docs/M5_DATA_REPORTING.md), and
[offline pilot evaluation](docs/PILOT_POLICY_EVALUATION.md).

Only draft publication to STS2SuperModel is authorized. Both remote sts2-sim
repositories remain read-only; local vendored changes carry regression evidence.
