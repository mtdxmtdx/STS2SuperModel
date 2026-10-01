# Line B + C 完成交付

版本锁：Game `v0.111.0` / commit `41cef1ea`，`sts2.dll` SHA-256 `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`，CLI protocol `0.2.0`，trace schema `1`。

## 1. B1 四档成本探针

| `--turns` | 状态 | 采集秒/种子 | 标注秒/状态 | expanded p50 / p90 / max | Reliable | search complete | BudgetBound |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 3 | 200 | 0.132246 | 0.061187 | 182 / 542 / 1010 | 75.50% | 100% | 0 |
| 6 | 313 | 0.137159 | 0.057545 | 176 / 450 / 1622 | 67.09% | 100% | 0 |
| 10 | 365 | 0.131511 | 0.056893 | 129 / 416 / 1622 | 69.59% | 100% | 0 |
| 14 | 363 | 0.134249 | 0.061160 | 130 / 506 / 1622 | 71.07% | 100% | 0 |

## 2. B2 选择

选择 `--turns 14`。这是探针中 `Reliable ≥ 70%` 的最大值，同时 `search_complete=100%`、`BudgetBound=0`。7,000 seeds 正式批次产生 7,070 个 `round ≥ 8` 唯一状态，其中 5,490 Reliable（77.65%）。

## 3. 合并覆盖率

- 唯一公开状态：34,041；Reliable 26,422；Estimated 7,603；Uncalculable 16。
- `round ≥ 8` Reliable：5,490 / 26,422 = **20.778%**。
- 角色：Ironclad 53.61%，Silent 46.39%。
- 质量门禁：`pass`；0 failures / 0 public leaks / 0 malformed / 0 conflicting states。
- 合并集 SHA-256：`CD0729EE5DDE63317C9902FB7CE8C5BBD073B13C5F773778C34DE504D921AE2C`。
- `holdout-round-deep-v1` 已冻结并双跑字节一致，SHA-256 `4027D6949648DFBBDF717DBEC9A4D4CD20ABBB71676DB869F50812A2CD51F46D`。

## 4. C1 遗物证据

- `RING_OF_THE_DRAKE`：同种子、同角色、同 40 张卡牌、同 encounter 的 paired-control 真实引擎探针，三回合公开手牌数差均为 +2；两轮报告字节一致；晋升 Reliable。
- 交接范围 35 个 TurnStart 候选全部有终态：1 Reliable，34 `PendingWithReason`，逐报告记录 confidence/scope/mismatch 原因。
- 当前全目录 `reliable_eligible=24`。当前文件显示 Ring 晋升前为 23、晋升后为 24；交接中“从 24 起”的数字已过期。
- 严格证据规则未修改。
- `PARRYING_SHIELD` 保持 `PartiallySupported` semantic hold。
- `UNCEASING_TOP` 已由既有空手触发 fixture 严格验证并保持 Reliable；交接中把它列为未解除 hold 的信息已过期。

## 5. C2 药水覆盖率

权威分母来自游戏程序集 `ModelDb.AllPotions`，不是 wiki。

| 指标 | 数量 |
|---|---:|
| 游戏目录药水 | 66 |
| 已结构化 | 66 |
| 已捕获状态 | 66 |
| 已检查 IL | 65 |
| 真实引擎运行时观测 | 4 |
| 严格证据合格 | 2 |
| 模拟器声明支持 | 4 |
| 已知不支持 | 60 |
| OutOfScope | 2 |
| Unknown | 0 |

每个药水均有明确 `next_action`。本线只建立目录、IL/运行时证据索引和覆盖率报告，没有实现任何新药水语义。

## 6. 搜索预算结论

本次没有发现深回合超出当前 10,000-node 预算：四档 `BudgetBound=0`，选定的 turns=14 探针与正式批次均 `search_complete=100%`。当前不需要因回合深度降低上限或提高搜索预算。

## 门禁

- `python -m pytest training -q --disable-warnings --ignore=training/test_replay_action.py`：143 passed / 1 skipped。
- `python training/verify_repeat_runs.py`：`verdict=pass`，218 reports，different/missing/added/unexpected 均为 0。
- 定向 .NET 导出测试：2 passed（遗物目录 + 药水权威注册表）。
- `DeterministicSimulator.cs`：无改动。

## 主要产物

- `data/combat_model/round-depth-v1/`
- `data/combat_model/holdouts/holdout-round-deep-v1.json`
- `data/relics/v0.111/turnstart-evidence-closeout.json`
- `data/relics/v0.111/turnstart-evidence-closeout.md`
- `data/potions/v0.111/potion-runtime-catalog.json`
- `data/potions/v0.111/potion-coverage.json`
- `data/potions/v0.111/potion-coverage.md`
