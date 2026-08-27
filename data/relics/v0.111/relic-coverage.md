# Slay the Spire 2 v0.111.0 遗物覆盖率报告

- **游戏版本**: `0.111.0` (commit `41cef1ea`)
- **程序集 SHA-256**: `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`
- **CLI 协议**: `0.2.0`
- **生成时间 (UTC)**: `2026-08-26T00:00:00Z`

## 汇总统计

| 指标 | 数量 | 占比 |
| :--- | :--- | :--- |
| 游戏目录遗物总数 | **299** | 100.0% |
| 已结构化数量 | **299** | 100.0% |
| 已捕获状态数量 | **299** | 100.0% |
| 已检查 IL 语义数量 | **0** | 0.0% |
| 已完成真实引擎差分探针数量 | **17** | 5.7% |
| 模拟器声明支持数量（其中仅探针项可视为 LiveObserved） | **17** | 5.7% |
| 明确不影响当前回合数量 | **1** | 0.3% |
| 能够产生 Reliable 教师标签数量 | **18** | 6% |
| 已知不支持数量 (存在未支持战斗钩子) | **163** | 54.5% |
| 未知遗物数量 | **118** | 39.5% |

## P1 已验证支持遗物列表

| Relic ID | 中文名 | 英文名 | 稀有度 | 效果概览 | 证据等级 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `AKABEKO` | 赤牛 | Akabeko | Uncommon | 在每场战斗开始时，获得[blue]{VigorPower}[/blue]点[gold]活力[/gold]。 | `LiveObserved` |
| `ANCHOR` | 锚 | Anchor | Common | 每场战斗开始时获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `LiveObserved` |
| `ART_OF_WAR` | 孙子兵法 | Art of War | Rare | 如果你在本回合中没有打出过攻击牌，则在下一回合额外获得1点{Energy:energyIcons()}。 | `LiveObserved` |
| `BAG_OF_MARBLES` | 弹珠袋 | Bag of Marbles | Common | 在每场战斗开始时，给予所有敌人[blue]{VulnerablePower}[/blue]层[gold]易伤[/gold]。 | `LiveObserved` |
| `BAG_OF_PREPARATION` | 准备背包 | Bag of Preparation | Common | 在每场战斗开始时，额外抽[blue]{Cards}[/blue]张牌。 | `LiveObserved` |
| `CENTENNIAL_PUZZLE` | 百年积木 | Centennial Puzzle | Common | 你在每场战斗中第一次损失生命值时，抽[blue]{Cards}[/blue]张牌。 | `LiveObserved` |
| `HAPPY_FLOWER` | 开心小花 | Happy Flower | Common | 每[blue]{Turns}[/blue]个回合，获得{Energy:energyIcons()}。 | `LiveObserved` |
| `LANTERN` | 灯笼 | Lantern | Common | 在每场战斗的第一回合获得{Energy:energyIcons()}。 | `LiveObserved` |
| `NUNCHAKU` | 双截棍 | Nunchaku | Uncommon | 你每打出[blue]{Cards}[/blue]张攻击牌，获得{Energy:energyIcons()}。 | `LiveObserved` |
| `ODDLY_SMOOTH_STONE` | 意外光滑的石头 | Oddly Smooth Stone | Common | 在每场战斗开始时，获得[blue]{DexterityPower}[/blue]点[gold]敏捷[/gold]。 | `LiveObserved` |
| `ORICHALCUM` | 奥利哈钢 | Orichalcum | Uncommon | 如果你在回合结束时没有任何[gold]格挡[/gold]，获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `LiveObserved` |
| `PEN_NIB` | 钢笔尖 | Pen Nib | Uncommon | 你每打出的第[blue]10[/blue]张攻击牌将会造成双倍伤害。 | `LiveObserved` |
| `RING_OF_THE_SNAKE` | 蛇之戒指 | Ring of the Snake | Starter | 在每场战斗开始时，额外抽[blue]{Cards}[/blue]张牌。 | `LiveObserved` |
| `TOUGH_BANDAGES` | 结实绷带 | Tough Bandages | Rare | 你每在你的回合丢弃一张牌，就获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `LiveObserved` |
| `TUNGSTEN_ROD` | 钨合金棍 | Tungsten Rod | Rare | 你每次失去生命时，减少失去的生命值[blue]{HpLossReduction}[/blue]点。 | `LiveObserved` |
| `UNCEASING_TOP` | 不休陀螺 | Unceasing Top | Rare | 在你的回合，当你没有[gold]手牌[/gold]时，抽一张牌。 | `LiveObserved` |
| `VAJRA` | 金刚杵 | Vajra | Common | 在每场战斗开始时，获得[blue]{StrengthPower}[/blue]点[gold]力量[/gold]。 | `LiveObserved` |

