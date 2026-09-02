# STS2 v0.111.0 遗物与卡牌语义返工报告

更新时间：2026-08-30

本报告替代此前把“结构化/handler 存在”当作“行为级 Reliable”的收口结论。返工只接受当前可复现的 CLI trace、ShadowDiff 质量元数据和显式证据清单。

## 版本锁

- Game: `v0.111.0` / commit `41cef1ea`
- `sts2.dll` SHA-256: `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`
- CLI protocol: `0.2.0`; trace schema: `1`

## 遗物状态（当前 catalog）

| 状态 | 数量 |
|---|---:|
| SimulatorSupported（handler 已实现） | 98 |
| PartiallySupported（PARRYING_SHIELD、UNCEASING_TOP） | 2 |
| UnsupportedKnownEffect（战斗 hook 未实现） | 20 |
| UnverifiableByCli | 25 |
| Uncalculable | 56 |
| OutOfScope（非战斗） | 97 |
| NoCombatEffect | 1 |
| Unknown | 0 |

`GIRYA`、`BOOKMARK`、`DATA_DISK` 等具有实际战斗 callback 的遗物不再被错误归类为 OutOfScope。PARRYING_SHIELD 仍缺多敌随机目标消歧，UNCEASING_TOP 仍缺空手触发证据，因此保持 PartiallySupported。

严格证据统计来自 `data/relics/v0.111/relic-coverage.json`：runtime evidence structurally valid=100，evidence eligible=24（其中 1 个 NoCombatEffect），其余 handler 报告因随机/teacher-conditioned/计数级比较降级，不能进入 Reliable NOSL 主标签。

## ShadowDiff 矩阵

`data/shadowdiff-rework-verification.json` 与 `data/p1-repeat-verification.json` 均由 212 个已注册 trace action 生成并双跑：

- 总报告：212（P0 31 / P1 Power 26 / P1 Relic 126 / P1 Card 29）
- `mismatch_count=0`：212
- 质量：Reliable 96、Estimated 67、Uncalculable 49
- 双跑：`different=0`、`missing=0`、`added=0`

质量规则：

- `Reliable` 仅允许 `outcome_quality=Exact` 且 `comparison_scope` 为严格公共状态或 terminal summary；
- 未知 RNG、placeholder/count-only、teacher draw order 条件化结果均标 `Unknown` 并降级；
- 原始 RNG words 不进入报告或 NOSL 输入，只保留计数向量。

## 卡牌语义证据

`data/card-semantic-signature-report.json` 当前为：

- 单人变体：1099；语义签名：590；签名投影碰撞：0；机器映射缺口：0；
- 直接 fixture 全变体签名：1；部分 fixture：7；fixture degraded：6；handler-only：21；fixture gap：554；
- 直接严格证据变体：9；无行为证据变体：1084；行为报告 12 strict / 17 degraded；
- `data/card-semantic-evidence-manifest.json` 明确记录每个 fixture 的 variant、action ordinal、报告、版本、质量和问题；禁止使用遗物/Power fixture 冒充卡牌证据。

因此，1099 个变体的结构化与 simulator handler 覆盖不等于 1099 个行为级 Reliable。当前只有通过严格 fixture/等价证明的签名可进入 Reliable；其余保持 fixture_gap、fixture_degraded 或 handler_only。

## 关键返工内容

1. `ShadowDiff/Program.cs` 增加 chance quality 元数据，`rngState=0`，未知/聚合/teacher-conditioned replay 不再伪装 Reliable。
2. 卡牌签名保留 `op.id`、dynamic flags、repeat/X 参数，使用显式 card fixture map 和 evidence manifest。
3. 遗物 catalog 使用精确 combat-hook 判定，加入 GIRYA，分离 handler 状态与 strict evidence eligibility。
4. `refresh_shadowdiff_reports.py` 可从已锁定 CLI traces 双跑重建报告；`verify_repeat_runs.py` 记录质量分布。

## 当前结论

遗物/卡牌“缺口”在结构化层已盘点，但行为证据与 NOSL 训练资格尚未全部收口。不得把旧的“212 全 Reliable”“1099 全行为覆盖”或“100 个遗物均 Reliable”结论用于训练。下一步应补齐真实多敌/空手 fixture、继续覆盖 fixture_gap 签名，然后再生成 M4 NOSL 教师数据。
