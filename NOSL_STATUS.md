# NOSL implementation status

The first **5,000 globally unique effective decision points** and a bounded
**one-epoch, 497-step experimental pilot** are complete. M3–M6 engineering works
within its documented scope. Full acceptance and formal training readiness remain
blocked by resource calibration, posterior coverage and policy quality gates.

- Data: 2,513 accepted constructed source battles; 4,585 complete-utility,
  359 partial-value and 56 auxiliary-only roots. An additional 304 all-masked
  roots remain diagnostic. Every saved raw reference verifies
- Frozen split: 3,976 train / 530 validation / 494 test. Test targets remain unused
- Trial: CPU only, about 275 seconds active wall time, peak RSS 872 MiB.
  Checkpoint resume completed within the same one-epoch lifetime budget
- Validation: utility MSE improved from 72,959 to 39,845, while MAE worsened from
  91.76 to 99.79. Common sampled no-death cases worsened as large death-tail
  errors improved. Four-world labels and zero certified pairs limit interpretation
- Constructed closed-loop check: student and baseline both won all 16 battles.
  Student ended 2.81 HP higher on average but spent four extra potions. This does
  not establish better overall utility; two Swift Potions drew zero cards
- Verification: 902 integration tests, 4,479 updated Core tests with three existing
  opt-in skips, 165 Python student tests, 104 data tests, 51 generator tests and
  24 calibration evidence tests passed

The optional [experimental policy guard](docs/EXPERIMENTAL_POLICY_GUARD.md)
abstains on unsupported potion/resource decisions; it does not silently fall back.
All pilot weights remain experimental and unpromoted. No formal training has run.
More data is gated on correcting allocation bias and missing action supervision,
not merely reaching a larger count. Remote simulator repositories remain read-only.

See [first pilot results](docs/FIRST_BOUNDED_PILOT.md),
[machine-readable evidence](configs/m5_first_pilot_report.json),
[readiness](TRAINING_READINESS.json), and
[M3–M6 evidence and limits](docs/M3_M6_STATUS.md).
