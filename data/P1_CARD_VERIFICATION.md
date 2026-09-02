# P1 卡牌行为证据登记（v0.111.0，返工后）

更新时间：2026-08-30

## 版本锁

- Game `v0.111.0` / commit `41cef1ea`
- `sts2.dll` SHA-256 `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`
- CLI protocol `0.2.0`; trace schema `1`

## 证据口径

`data/card-semantic-signature-report.json` 将结构化、handler 可执行和行为证据分开统计。`data/card-semantic-evidence-manifest.json` 只接受显式的 14 个 `p1-card-*` fixture，并记录 variant、action ordinal、报告文件、版本、比较范围、概率质量和问题。遗物、Power、P0 fixture 不得作为卡牌证据。

当前统计：

- 单人变体 1099；语义签名 590；签名投影碰撞 0；机器映射缺口 0；
- 全变体严格行为验证签名 1；部分 fixture 7；fixture degraded 6；handler-only 21；fixture gap 554；
- 直接严格证据变体 9；无行为证据变体 1084；
- 29 个卡牌报告：12 strict、17 degraded；全部 `match=true`、`mismatch_count=0`。

只有 `confidence=Reliable`、`outcome_quality=Exact`、严格公共状态比较且版本/重复哈希通过的报告才能进入 Reliable。随机目标、随机消耗、未知洗牌、teacher draw order 条件化结果均为 Estimated 或 Uncalculable，不进入 NOSL 主标签。

## Fixture → 语义模式

| Fixture | 代表变体 | 覆盖 |
|---|---|---|
| p1-card-exhaust-self | MOLTEN_FIST | 自我消耗 |
| p1-card-ethereal | DEFILE | 虚无回合结束 |
| p1-card-retain | SNAKEBITE | 保留跨回合 |
| p1-card-innate | BACKSTAB | 固有开局 + 消耗 |
| p1-card-discard-select | SURVIVOR | 指定弃牌/choice |
| p1-card-random-exhaust | TRUE_GRIT | 随机消耗（Unknown） |
| p1-card-random-target | SWORD_BOOMERANG | 随机目标（Unknown） |
| p1-card-choice-copy | DUAL_WIELD | 多卡选择/复制 |
| p1-card-generate | ANGER | 自我复制生成 |
| p1-card-generate-shiv | LEADING_STRIKE、SHIV | 生成小刀 |
| p1-card-x-cost | WHIRLWIND | X 费 |
| p1-card-auto-play | HAVOC | 抽牌堆自动出牌 |
| p1-card-move-upgrade-base | HEADBUTT | 弃牌堆→抽牌堆顶 |
| p1-card-move-upgrade-up | HEADBUTT_UPGRADE | SMITH 升级 9→12 |

## 当前结论

1099 个变体已经有结构化语义和 simulator handler，但只有显式 manifest 中的少量签名有真实行为证据；其余必须继续补 fixture 或提供等价性证明。该文件不再声称“1099 张卡全部行为级 Reliable”。
