# P1 Power 语义验证（返工后）

版本锁：v0.111.0 / commit `41cef1ea` / `sts2.dll` SHA-256 `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9` / CLI 0.2.0 / trace schema 1。

## 当前口径

11 个 P1 Power fixture、26 个报告均由真实 CLI trace 和 ShadowDiff 生成，`match=true`、`mismatch_count=0`。返工后的质量门禁将报告分为：19 Reliable（Exact + strict_public_state）、Estimated 或 Uncalculable 7。任何未知 RNG、placeholder/count-only 或 teacher-conditioned draw order 都不能作为 Reliable NOSL 证据。

已覆盖的 Power：THORNS、ACCURACY、PLATING、POISON、PANACHE、RAGE、FLAME_BARRIER、CORRUPTION、INFINITE_BLADES、ENVENOM、BUFFER。catalog 中 `simulator_supported` 表示 handler 已实现；进入训练仍须逐动作通过 chance-quality 和 action dependency 门禁。

## 报告质量字段

每份报告包含 `chance_present`、`random_operator`、`probability_known`、`outcome_quality`、`probability_mass_covered`、`effective_sample_size`、置信区间、`rng_consumption_vector`、`branch_enumerated`、`comparison_scope` 和 `identity_comparison`。原始 RNG state words 永不写入 NOSL 输入。

- `Reliable + Exact + strict_public_state`：可作为严格行为证据；
- `Unknown + aggregate_count_only`：只比较总量/计数，保留作诊断；
- `Unknown + observed_conditioned`：依赖 teacher snapshot 的有序牌堆，不能泛化到 NOSL；
- `Uncalculable`：影子无法在公开信息下重建，排除训练主损失。

## 可复现命令

```powershell
python training/refresh_shadowdiff_reports.py
python training/verify_semantic_rework.py
```

当前全矩阵 212 报告双跑 `different=0`；P1 Power 报告的具体质量和 hash 见 `data/p1-repeat-verification.json` 与 `data/shadowdiff-rework-verification.json`。本文件不再声称 26 个 Power 报告全部 Reliable。
