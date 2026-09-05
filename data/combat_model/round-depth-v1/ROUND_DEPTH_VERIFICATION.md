# Line B 回合深度扩展验证

版本锁：Game `v0.111.0` / commit `41cef1ea`，`sts2.dll` SHA-256 `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`，CLI protocol `0.2.0`，trace schema `1`。

## B1 成本探针

统一配置：每档 50 个种子、16 个 CLI/教师进程、每状态最多 10,000 expanded nodes、`--node-budget-only`、Ironclad/Silent 轮转、相同六个 encounter。

| `--turns` | 状态 | 采集秒/种子 | 标注秒/状态 | expanded p50 / p90 / max | Reliable | search complete | BudgetBound |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 3 | 200 | 0.132246 | 0.061187 | 182 / 542 / 1010 | 151 / 200 = 75.50% | 200 / 200 | 0 |
| 6 | 313 | 0.137159 | 0.057545 | 176 / 450 / 1622 | 210 / 313 = 67.09% | 313 / 313 | 0 |
| 10 | 365 | 0.131511 | 0.056893 | 129 / 416 / 1622 | 254 / 365 = 69.59% | 365 / 365 | 0 |
| 14 | 363 | 0.134249 | 0.061160 | 130 / 506 / 1622 | 258 / 363 = 71.07% | 363 / 363 | 0 |

选择 `--turns 14`：它是实测 `Reliable ≥ 70%` 的最大档位，且 `search_complete=100%`、`BudgetBound=0`。当前探针没有发现搜索预算上限。

## B2 正式批次

- 7,000 seeds，16 workers，`--turns 14`。
- 采集 50,657 个原始观察，墙钟 714.829 秒（0.102118 秒/种子）。
- 筛出 `round ≥ 8` 后 7,079 行；去重后 7,070 行，冲突 0。
- 深回合去重集：Reliable 5,490，Estimated 1,580，Uncalculable 0；Reliable 比例 77.65%。
- 深回合 expanded nodes：p50 86、p90 182、max 542；`search_complete=7,070/7,070`，`BudgetBound=0`。

## B3 合并与冻结

- 基础集：26,971 行；深回合集：7,070 行；合并后 34,041 个唯一公开状态。
- 冲突状态 0；质量门禁 `pass`，0 failures / 0 public leaks / 0 malformed。
- 角色分布：Ironclad 18,249（53.61%），Silent 15,792（46.39%）。
- Reliable：26,422；其中 `round ≥ 8` 为 5,490，占 **20.778%**，达到 20% 门槛。
- 合并数据 SHA-256：`CD0729EE5DDE63317C9902FB7CE8C5BBD073B13C5F773778C34DE504D921AE2C`。
- `holdout-round-deep-v1` 已冻结；两次生成 SHA-256 均为 `4027D6949648DFBBDF717DBEC9A4D4CD20ABBB71676DB869F50812A2CD51F46D`。
- holdout test 908 行、challenge 476 行；两者均为 Ironclad/Silent 50%/50%，round 范围 8–10。

## 证据文件

- `cost-probe.json`
- `collection-timing.json`
- `label-summary.json`
- `merge-report.json`
- `dataset-manifest.json`
- `coverage-profile.json`
- `quality-gate.json`
- `split-manifest.json`
- `../holdouts/holdout-round-deep-v1.json`
- `../holdouts/holdout-round-deep-v1-repeat-verification.json`
