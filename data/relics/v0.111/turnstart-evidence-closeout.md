# C1 TurnStart 遗物证据收口

> 范围是交接时的 35 个 TurnStart 证据修复候选：RING_OF_THE_DRAKE 加当前仍 pending 的 34 个。

## 汇总

| 指标 | 数量 |
|---|---:|
| 处理对象 | 35 |
| 本批转为 Reliable | 1 |
| 仍 PendingWithReason | 34 |
| 全目录 reliable_eligible | 24 |
| 实测本批前基线 | 23 |
| 本批净增 | 1 |

交接文档写的是 24 起步；当前文件历史事实是 Ring 晋升前 23，晋升后 24。此处按生成前后目录证据记录，不回退已通过空手 fixture 的 `UNCEASING_TOP`。

## 逐项终态

| ID | 终态 | 支持状态 | 证据等级 | 原因/证据 |
|---|---|---|---|---|
| `BEATING_REMNANT` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-beating-remnant-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-beating-remnant-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `BIG_MUSHROOM` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-tea-carried-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-tea-carried-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `BOOMING_CONCH` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-booming-conch-diff-report.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `BREAD` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-bread-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-bread-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `BRILLIANT_SCARF` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-brilliant-scarf-diff-report-5.json: confidence='Estimated'; comparison_scope='aggregate_count_only' |
| `BRIMSTONE` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-brimstone-diff-report-1.json: confidence='Estimated'; comparison_scope='aggregate_count_only' |
| `CANDELABRA` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-candelabra-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-candelabra-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `CHANDELIER` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-chandelier-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-chandelier-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `FAKE_BLOOD_VIAL` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-combat-start-carried-diff-report-1.json: confidence='Estimated'; comparison_scope='aggregate_count_only' |
| `FAKE_HAPPY_FLOWER` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-fake-happy-flower-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-fake-happy-flower-diff-report-4.json: confidence='Estimated'; comparison_scope='aggregate_count_only' |
| `FAKE_ORICHALCUM` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-fake-orichalcum-diff-report.json: confidence='Estimated'; comparison_scope='aggregate_count_only' |
| `FESTIVE_POPPER` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-festive-popper-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-festive-popper-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `FIDDLE` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-fiddle-diff-report-0.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-fiddle-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `HAPPY_FLOWER` | PendingWithReason | SimulatorSupported | HeuristicInferred | p0-csharp-happy-flower-diff-report.json: confidence='Estimated'; comparison_scope='observed_conditioned' |
| `KUNAI` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-attack-counters-diff-report-3.json: confidence='Estimated'; comparison_scope='aggregate_count_only' |
| `MERCURY_HOURGLASS` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-mercury-hourglass-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-mercury-hourglass-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `MR_STRUGGLES` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-mr-struggles-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-mr-struggles-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `NINJA_SCROLL` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-ninja-scroll-diff-report.json: confidence='Estimated'; comparison_scope='aggregate_count_only' |
| `ORICHALCUM` | PendingWithReason | SimulatorSupported | HeuristicInferred | p0-csharp-orichalcum-diff-report.json: confidence='Estimated'; comparison_scope='observed_conditioned' |
| `ORNAMENTAL_FAN` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-attack-counters-diff-report-3.json: confidence='Estimated'; comparison_scope='aggregate_count_only' |
| `PAELS_BLOOD` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-per-turn-draw-diff-report-0.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-per-turn-draw-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `PAELS_TEARS` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-paels-tears-diff-report-1.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-paels-tears-diff-report-2.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `PENDULUM` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-pendulum-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-pendulum-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `POCKETWATCH` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-pocketwatch-diff-report-0.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-pocketwatch-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `RED_MASK` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-red-mask-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-red-mask-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `RING_OF_THE_DRAKE` | Reliable | SimulatorSupported | LiveObserved | all referenced reports satisfy strict evidence |
| `RIPPLE_BASIN` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-turn-end-block-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-turn-end-block-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `ROYAL_POISON` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-royal-poison-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-royal-poison-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `SAI` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-sai-diff-report.json: confidence='Estimated'; comparison_scope='aggregate_count_only' |
| `SEAL_OF_GOLD` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-seal-of-gold-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-seal-of-gold-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `SHURIKEN` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-attack-counters-diff-report-3.json: confidence='Estimated'; comparison_scope='aggregate_count_only' |
| `STONE_CALENDAR` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-stone-calendar-diff-report-6.json: confidence='Estimated'; comparison_scope='aggregate_count_only' |
| `TWISTED_FUNNEL` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-twisted-funnel-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-twisted-funnel-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `VELVET_CHOKER` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-velvet-choker-diff-report-0.json: confidence='Estimated'; comparison_scope='aggregate_count_only' \| p1-csharp-relic-velvet-choker-diff-report-1.json: confidence='Uncalculable'; comparison_scope='aggregate_count_only' |
| `VERY_HOT_COCOA` | PendingWithReason | SimulatorSupported | HeuristicInferred | p1-csharp-relic-combat-start-carried-diff-report-1.json: confidence='Estimated'; comparison_scope='aggregate_count_only' |

## Semantic hold

- `PARRYING_SHIELD`：继续保持 `PartiallySupported`，随机多目标身份在 NOSL 公共观测下不能消歧。
- `UNCEASING_TOP`：当前已由空手触发 fixture 严格验证并保持 Reliable；交接中称其仍为 hold 的信息已过期。
