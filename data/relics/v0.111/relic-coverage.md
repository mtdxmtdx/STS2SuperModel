# Slay the Spire 2 v0.111.0 遗物覆盖率报告

- **游戏版本**: `0.111.0` (commit `41cef1ea`)
- **程序集 SHA-256**: `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`
- **CLI 协议**: `0.2.0`
- **生成时间 (UTC)**: `2026-08-30T00:00:00Z`

## 汇总统计

| 指标 | 数量 | 占比 |
| :--- | :--- | :--- |
| 游戏目录遗物总数 | **299** | 100.0% |
| 已结构化数量 | **299** | 100.0% |
| 已捕获状态数量 | **299** | 100.0% |
| 已检查 IL 语义数量 | **0** | 0.0% |
| 已完成真实引擎差分探针数量 | **100** | 33.4% |
| 版本/结构字段有效的探针数量 | **100** | 33.4% |
| 严格证据合格探针数量（Reliable eligible） | **24** | 8% |
| 模拟器声明支持数量（不含证据待补的 PartiallySupported） | **99** | 33.1% |
| 语义证据待补数量 | **1** | 0.3% |
| 明确不影响当前回合数量 | **1** | 0.3% |
| 能够产生 Reliable 教师标签数量 | **25** | 8.4% |
| 已知不支持数量 (存在未支持战斗钩子) | **20** | 6.7% |
| 未知遗物数量 | **0** | 0% |
| 明确 OutOfScope 非战斗遗物数量 | **97** | 32.4% |

## P1 已验证支持遗物列表

| Relic ID | 中文名 | 英文名 | 稀有度 | 效果概览 | 证据等级 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `AKABEKO` | 赤牛 | Akabeko | Uncommon | 在每场战斗开始时，获得[blue]{VigorPower}[/blue]点[gold]活力[/gold]。 | `LiveObserved` |
| `ANCHOR` | 锚 | Anchor | Common | 每场战斗开始时获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `LiveObserved` |
| `ART_OF_WAR` | 孙子兵法 | Art of War | Rare | 如果你在本回合中没有打出过攻击牌，则在下一回合额外获得1点{Energy:energyIcons()}。 | `HeuristicInferred` |
| `BAG_OF_MARBLES` | 弹珠袋 | Bag of Marbles | Common | 在每场战斗开始时，给予所有敌人[blue]{VulnerablePower}[/blue]层[gold]易伤[/gold]。 | `LiveObserved` |
| `BAG_OF_PREPARATION` | 准备背包 | Bag of Preparation | Common | 在每场战斗开始时，额外抽[blue]{Cards}[/blue]张牌。 | `LiveObserved` |
| `BEATING_REMNANT` | 律动残余 | Beating Remnant | Rare | 你在一回合内失去的生命值不会超过[blue]20[/blue]点。 | `HeuristicInferred` |
| `BELT_BUCKLE` | 腰带扣 | Belt Buckle | Shop | 当你没有药水时，你额外拥有[blue]{DexterityPower}[/blue]点[gold]敏捷[/gold]。 | `HeuristicInferred` |
| `BIG_MUSHROOM` | 大蘑菇 | Big Mushroom | Event | 拾起时，将你的最大生命值提升[blue]{MaxHp}[/blue]。在每场战斗开始时，少抽[blue]{Cards}[/blue]张牌。 | `HeuristicInferred` |
| `BLACK_BLOOD` | 黑暗之血 | Black Blood | Starter | 在战斗结束时，回复[green]{Heal}[/green]点生命。 | `LiveObserved` |
| `BLOOD_SOAKED_ROSE` | 血染玫瑰 | Blood-Soaked Rose | Ancient | 拾起时，将[blue]1[/blue]张[red]执迷[/red]加入你的[gold]牌组[/gold]。在回合开始时获得{Energy:energyIcons()}。 | `HeuristicInferred` |
| `BLOOD_VIAL` | 小血瓶 | Blood Vial | Common | 在每场战斗开始时，回复[green]{Heal}[/green]点生命。 | `LiveObserved` |
| `BOOMING_CONCH` | 轰鸣海螺 | Booming Conch | Ancient | 在[gold]精英[/gold]战的战斗开始时，额外抽[blue]{Cards}[/blue]张牌并获得{Energy:energyIcons()}。 | `HeuristicInferred` |
| `BREAD` | 面包 | Bread | Shop | 在你的第一个回合开始时，失去{LoseEnergy:energyIcons()}。在其余的回合开始时，获得{GainEnergy:energyIcons()}。 | `HeuristicInferred` |
| `BRILLIANT_SCARF` | 艳丽围巾 | Brilliant Scarf | Ancient | 你每回合从你的手牌打出的第[blue]5[/blue]张牌可以被免费打出。 | `HeuristicInferred` |
| `BRIMSTONE` | 硫磺 | Brimstone | Shop | 在你的每个回合开始时，你获得[blue]{SelfStrength}[/blue]点[gold]力量[/gold]，所有敌人获得[blue]{EnemyStrength}[/blue]点[gold]力量[/gold]。 | `HeuristicInferred` |
| `BRONZE_SCALES` | 铜质鳞片 | Bronze Scales | Common | 在每场战斗开始时，获得[blue]{ThornsPower}[/blue]点[gold]荆棘[/gold]。 | `Unknown` |
| `CANDELABRA` | 烛台 | Candelabra | Uncommon | 在你的[blue]第2[/blue]回合开始时，获得{Energy:energyIcons()}。 | `HeuristicInferred` |
| `CAPTAINS_WHEEL` | 舵盘 | Captain's Wheel | Rare | 在你的[blue]第三[/blue]回合开始时，获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `HeuristicInferred` |
| `CENTENNIAL_PUZZLE` | 百年积木 | Centennial Puzzle | Common | 你在每场战斗中第一次损失生命值时，抽[blue]{Cards}[/blue]张牌。 | `HeuristicInferred` |
| `CHANDELIER` | 吊灯 | Chandelier | Rare | 在你的[blue]第三[/blue]回合开始时，获得{Energy:energyIcons()}。 | `HeuristicInferred` |
| `CLOAK_CLASP` | 斗篷扣 | Cloak Clasp | Rare | 在你的回合结束时，每有一张[gold]手牌[/gold]，就获得[blue]{Block}[/blue]点[gold]格挡[/gold] | `HeuristicInferred` |
| `DAUGHTER_OF_THE_WIND` | 风的女儿 | Daughter of the Wind | Event | 每当你打出一张攻击牌时，获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `HeuristicInferred` |
| `DELICATE_FROND` | 娇嫩蕨草 | Delicate Frond | Ancient | 在每场战斗开始时，用随机药水将你的空药水栏位填满。 | `HeuristicInferred` |
| `ECTOPLASM` | 灵体外质 | Ectoplasm | Ancient | 你不能再获得任何[gold]金币[/gold]。在回合开始时获得{Energy:energyIcons()} | `HeuristicInferred` |
| `FAKE_ANCHOR` | 锚？？？ | Anchor??? | Event | 在每场战斗开始时，获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `HeuristicInferred` |
| `FAKE_BLOOD_VIAL` | 小血瓶？？？ | Blood Vial??? | Event | 在每场战斗开始时，回复[green]{Heal}[/green]点生命。 | `HeuristicInferred` |
| `FAKE_HAPPY_FLOWER` | 开心小花？？？ | Happy Flower??? | Event | 每[blue]{Turns}[/blue]个回合，获得{Energy:energyIcons()}。 | `HeuristicInferred` |
| `FAKE_ORICHALCUM` | 奥利哈钢？？？ | Orichalcum??? | Event | 如果你在回合结束时没有任何[gold]格挡[/gold]，获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `HeuristicInferred` |
| `FAKE_SNECKO_EYE` | 异蛇之眼？？？ | Snecko Eye??? | Event | 每场战斗开始时获得[red]混乱[/red]效果。 | `HeuristicInferred` |
| `FAKE_STRIKE_DUMMY` | 打击木偶？？？ | Strike Dummy??? | Event | 名字中有“打击”的卡牌造成[blue]{ExtraDamage}[/blue]点额外伤害。 | `HeuristicInferred` |
| `FAKE_VENERABLE_TEA_SET` | 古茶具套装？？？ | Venerable Tea Set??? | Event | 到达[gold]休息处[/gold]后的下一场战斗开始时额外获得{Energy:energyIcons()}。 | `HeuristicInferred` |
| `FESTIVE_POPPER` | 节日拉炮 | Festive Popper | Common | 在每场战斗开始时，对所有敌人造成[blue]{Damage}[/blue]点伤害。 | `HeuristicInferred` |
| `FIDDLE` | 小提琴 | Fiddle | Ancient | 在每个回合开始时，额外抽[blue]{Cards}[/blue]张牌。你在回合进行中不再能抽任何牌。 | `HeuristicInferred` |
| `FISHING_ROD` | 钓鱼竿 | Fishing Rod | Ancient | 每[blue]{Combats}[/blue]场普通战斗，随机[gold]升级[/gold]你[gold]牌组[/gold]中的一张牌。 | `HeuristicInferred` |
| `GAME_PIECE` | 棋子 | Game Piece | Rare | 每当你打出能力牌时，抽[blue]{Cards}[/blue]张牌。 | `LiveObserved` |
| `HAND_DRILL` | 手钻 | Hand Drill | Event | 每当你突破敌人的[gold]格挡[/gold]时，给予其[blue]{VulnerablePower}[/blue]层[gold]易伤[/gold]。 | `LiveObserved` |
| `HAPPY_FLOWER` | 开心小花 | Happy Flower | Common | 每[blue]{Turns}[/blue]个回合，获得{Energy:energyIcons()}。 | `HeuristicInferred` |
| `HORN_CLEAT` | 船夹板 | Horn Cleat | Uncommon | 在你的[blue]第二[/blue]回合开始时，获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `HeuristicInferred` |
| `ICE_CREAM` | 冰淇淋 | Ice Cream | Rare | 多余的能量可以留到下一回合。 | `HeuristicInferred` |
| `INTIMIDATING_HELMET` | 骇人头盔 | Intimidating Helmet | Rare | 每当你打出一张耗能大于等于{Energy:energyIcons()}的牌，获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `LiveObserved` |
| `IRON_CLUB` | 铁棒 | Iron Club | Ancient | 你每打出[blue]{Cards}[/blue]张牌，就抽[blue]1[/blue]张牌。 | `LiveObserved` |
| `IVORY_TILE` | 象牙麻将牌 | Ivory Tile | Rare | 每当你打出一张耗能大于等于{EnergyThreshold:energyIcons()}的牌时，获得{Energy:energyIcons()}。 | `LiveObserved` |
| `JOSS_PAPER` | 金纸 | Joss Paper | Uncommon | 你每[gold]消耗[/gold][blue]{ExhaustAmount}[/blue]张牌，就抽[blue]{Cards}[/blue]张牌。 | `LiveObserved` |
| `KUNAI` | 苦无 | Kunai | Rare | 你每在同一回合内打出[blue]{Cards}[/blue]张攻击牌，就获得[blue]{DexterityPower}[/blue]点[gold]敏捷[/gold]。 | `HeuristicInferred` |
| `KUSARIGAMA` | 锁镰 | Kusarigama | Uncommon | 你每在同一回合内打出[blue]{Cards}[/blue]张攻击牌，就随机对一名敌人造成[blue]{Damage}[/blue]点伤害。 | `HeuristicInferred` |
| `LANTERN` | 灯笼 | Lantern | Common | 在每场战斗的第一回合获得{Energy:energyIcons()}。 | `LiveObserved` |
| `LETTER_OPENER` | 开信刀 | Letter Opener | Uncommon | 你每在同一回合内打出[blue]{Cards}[/blue]张技能牌，就对所有敌人造成[blue]{Damage}[/blue]点伤害。 | `HeuristicInferred` |
| `LOST_WISP` | 迷失鬼火 | Lost Wisp | Event | 你每打出一张能力牌，就对所有敌人造成[blue]{Damage}[/blue]点伤害。 | `LiveObserved` |
| `MEAT_ON_THE_BONE` | 带骨肉 | Meat on the Bone | Rare | 如果你在战斗结束时生命值等于或低于[blue]{HpThreshold}%[/blue]，回复[green]{Heal}[/green]点生命。 | `LiveObserved` |
| `MERCURY_HOURGLASS` | 水银沙漏 | Mercury Hourglass | Uncommon | 在你的回合开始时，对所有敌人造成[blue]{Damage}[/blue]点伤害。 | `HeuristicInferred` |
| `MR_STRUGGLES` | 抱抱先生 | Mr. Struggles | Event | 在你的回合开始时，对所有敌人造成等量于当前回合数的伤害。 | `HeuristicInferred` |
| `NINJA_SCROLL` | 忍术卷轴 | Ninja Scroll | Shop | 每场战斗开始时，将[blue]{Shivs}[/blue]张[gold]小刀[/gold]加入你的[gold]手牌[/gold]。 | `HeuristicInferred` |
| `NUNCHAKU` | 双截棍 | Nunchaku | Uncommon | 你每打出[blue]{Cards}[/blue]张攻击牌，获得{Energy:energyIcons()}。 | `LiveObserved` |
| `ODDLY_SMOOTH_STONE` | 意外光滑的石头 | Oddly Smooth Stone | Common | 在每场战斗开始时，获得[blue]{DexterityPower}[/blue]点[gold]敏捷[/gold]。 | `LiveObserved` |
| `ORICHALCUM` | 奥利哈钢 | Orichalcum | Uncommon | 如果你在回合结束时没有任何[gold]格挡[/gold]，获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `HeuristicInferred` |
| `ORNAMENTAL_FAN` | 精致折扇 | Ornamental Fan | Uncommon | 你每在同一回合内打出[blue]{Cards}[/blue]张攻击牌，就获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `HeuristicInferred` |
| `PAELS_BLOOD` | 佩尔之血 | Pael's Blood | Ancient | 在你的回合开始时，额外抽[blue]{Cards}[/blue]张牌 | `HeuristicInferred` |
| `PAELS_FLESH` | 佩尔之肉 | Pael's Flesh | Ancient | 从你的第[blue]3[/blue]回合开始，在回合开始时额外获得{Energy:energyIcons()}。 | `HeuristicInferred` |
| `PAELS_TEARS` | 佩尔之泪 | Pael's Tears | Ancient | 如果你在拥有未花费的{energyPrefix:energyIcons(1)}情况下结束回合，则下个回合额外获得{Energy:energyIcons()}。 | `HeuristicInferred` |
| `PANTOGRAPH` | 缩放仪 | Pantograph | Uncommon | 在[gold]Boss[/gold]战开始时，回复[green]{Heal}[/green]点生命值。 | `HeuristicInferred` |
| `PENDULUM` | 摆动球 | Pendulum | Common | 每[blue]{Turns}[/blue]个回合，抽[blue]{Cards}[/blue]张牌 | `HeuristicInferred` |
| `PEN_NIB` | 钢笔尖 | Pen Nib | Uncommon | 你每打出的第[blue]10[/blue]张攻击牌将会造成双倍伤害。 | `LiveObserved` |
| `PERMAFROST` | 永冻冰晶 | Permafrost | Uncommon | 当你在战斗中第一次打出能力牌时，获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `LiveObserved` |
| `PHILOSOPHERS_STONE` | 贤者之石 | Philosopher's Stone | Ancient | 在每回合开始时获得{Energy:energyIcons()}。所有敌人初始获得[blue]{StrengthPower}[/blue]点[gold]力量[/gold]。 | `HeuristicInferred` |
| `POCKETWATCH` | 怀表 | Pocketwatch | Rare | 当你在本回合打出的牌少于等于[blue]{CardThreshold}[/blue]张时，则在你的下个回合开始时额外抽[blue]{Cards}[/blue]张牌。 | `HeuristicInferred` |
| `PRISMATIC_GEM` | 棱彩宝石 | Prismatic Gem | Ancient | 在每个回合开始时获得{Energy:energyIcons()}。卡牌奖励现在会包含其他颜色的卡牌。 | `HeuristicInferred` |
| `PUMPKIN_CANDLE` | 南瓜蜡烛 | Pumpkin Candle | Ancient | 在每个回合开始时获得{Energy:energyIcons()}。这件遗物会在[blue]{CombatCount}[/blue]场战斗后熄灭。可以在[gold]休息处[/gold]为其[gold]添火[/gold]。 | `HeuristicInferred` |
| `RAINBOW_RING` | 彩虹戒指 | Rainbow Ring | Rare | 每回合，你第一次打出攻击牌、技能牌和能力牌各一张时，获得[blue]{StrengthPower}[/blue]点[gold]力量[/gold]和[blue]{DexterityPower}[/blue]点[gold]敏捷[/gold]。 | `LiveObserved` |
| `RED_MASK` | 红面具 | Red Mask | Common | 在每场战斗开始时，给于所有敌人[blue]{WeakPower}[/blue]层[gold]虚弱[/gold]。 | `HeuristicInferred` |
| `RING_OF_THE_DRAKE` | 长蛇戒指 | Ring of the Drake | Starter | 在战斗开始时的前[blue]{Turns}[/blue]个回合，你额外抽[blue]{Cards}[/blue]张牌。 | `HeuristicInferred` |
| `RING_OF_THE_SNAKE` | 蛇之戒指 | Ring of the Snake | Starter | 在每场战斗开始时，额外抽[blue]{Cards}[/blue]张牌。 | `LiveObserved` |
| `RIPPLE_BASIN` | 波纹水盆 | Ripple Basin | Uncommon | 如果你在本回合中没有打出过攻击牌，则获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `HeuristicInferred` |
| `ROYAL_POISON` | 王室猛毒 | Royal Poison | Event | 在每场战斗开始时，失去[blue]{Damage}[/blue]点生命。 | `HeuristicInferred` |
| `SAI` | 钗 | Sai | Ancient | 在你的回合开始时，获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `HeuristicInferred` |
| `SCREAMING_FLAGON` | 尖叫酒壶 | Screaming Flagon | Shop | 如果你在回合结束时没有任何[gold]手牌[/gold]，则对所有敌人造成[blue]{Damage}[/blue]点伤害。 | `HeuristicInferred` |
| `SEAL_OF_GOLD` | 黄金印 | Seal of Gold | Ancient | 在你的回合开始时，花费[blue]{Gold}[/blue][gold]金币[/gold]来获得{Energy:energyIcons()}。 | `HeuristicInferred` |
| `SELF_FORMING_CLAY` | 自成型黏土 | Self-Forming Clay | Uncommon | 每当你在战斗中失去生命，就在下回合获得[blue]{BlockNextTurn}[/blue]点[gold]格挡[/gold]。 | `HeuristicInferred` |
| `SHURIKEN` | 手里剑 | Shuriken | Rare | 你每在同一回合内打出[blue]{Cards}[/blue]张攻击牌，获得[blue]{StrengthPower}[/blue]点[gold]力量[/gold]。 | `HeuristicInferred` |
| `SNECKO_EYE` | 异蛇之眼 | Snecko Eye | Ancient | 每回合多抽[blue]{Cards}[/blue]张牌。每场战斗开始时获得[red]混乱[/red]效果。 | `HeuristicInferred` |
| `SOZU` | 添水 | Sozu | Ancient | 你无法再获得药水。在每回合开始时获得{Energy:energyIcons()}。 | `HeuristicInferred` |
| `SPARKLING_ROUGE` | 闪亮口红 | Sparkling Rouge | Uncommon | 在你的第[blue]3[/blue]回合开始时，获得[blue]{StrengthPower}[/blue]点[gold]力量[/gold]和[blue]{DexterityPower}[/blue]点[gold]敏捷[/gold]。 | `HeuristicInferred` |
| `SPIKED_GAUNTLETS` | 带刺手甲 | Spiked Gauntlets | Ancient | 在每回合开始时获得{Energy:energyIcons()}。能力牌的耗能增加[blue]1[/blue]{energyPrefix:energyIcons(1)}。 | `HeuristicInferred` |
| `STONE_CALENDAR` | 历石 | Stone Calendar | Rare | 在第[blue]{DamageTurn}[/blue]回合结束时，对所有敌人造成[blue]{Damage}[/blue]点伤害。 | `HeuristicInferred` |
| `STRIKE_DUMMY` | 打击木偶 | Strike Dummy | Common | 名字中有“打击”的卡牌造成[blue]{ExtraDamage}[/blue]点额外伤害。 | `HeuristicInferred` |
| `SWORD_OF_STONE` | 石之剑 | Sword of Stone | Event | 在击败[blue]{Elites}[/blue]名[gold]精英[/gold]敌人之后将变化为一件强力[gold]遗物[/gold]。 | `HeuristicInferred` |
| `THE_BOOT` | 发条靴 | The Boot | Event | 每当你造成小于等于[blue]{DamageThreshold}[/blue]点未被格挡的攻击伤害时，将伤害提升为[blue]{DamageMinimum}[/blue]。 | `HeuristicInferred` |
| `TOUGH_BANDAGES` | 结实绷带 | Tough Bandages | Rare | 你每在你的回合丢弃一张牌，就获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `LiveObserved` |
| `TOY_BOX` | 玩具盒 | Toy Box | Ancient | 拾起时，获得[blue]{Relics}[/blue]件[gold]蜡制遗物[/gold]。每经过[blue]{Combats}[/blue]场战斗，你最左侧的[gold]蜡制遗物[/gold]将会融化。 | `HeuristicInferred` |
| `TUNGSTEN_ROD` | 钨合金棍 | Tungsten Rod | Rare | 你每次失去生命时，减少失去的生命值[blue]{HpLossReduction}[/blue]点。 | `LiveObserved` |
| `TUNING_FORK` | 音叉 | Tuning Fork | Uncommon | 你每打出[blue]{Cards}[/blue]张技能牌，获得[blue]{Block}[/blue]点[gold]格挡[/gold]。 | `HeuristicInferred` |
| `TWISTED_FUNNEL` | 扭曲漏斗 | Twisted Funnel | Uncommon | 在每场战斗开始时，给予所有敌人[blue]{PoisonPower}[/blue]层[gold]中毒[/gold]。 | `HeuristicInferred` |
| `UNCEASING_TOP` | 不休陀螺 | Unceasing Top | Rare | 在你的回合，当你没有[gold]手牌[/gold]时，抽一张牌。 | `LiveObserved` |
| `VAJRA` | 金刚杵 | Vajra | Common | 在每场战斗开始时，获得[blue]{StrengthPower}[/blue]点[gold]力量[/gold]。 | `Unknown` |
| `VAMBRACE` | 臂甲 | Vambrace | Uncommon | 每场战斗中，你第一次从卡牌中获得的[gold]格挡[/gold]值翻倍。 | `HeuristicInferred` |
| `VELVET_CHOKER` | 天鹅绒颈圈 | Velvet Choker | Ancient | 在每回合开始时获得{Energy:energyIcons()}。你每回合不能打出超过[blue]{Cards}[/blue]张牌。 | `HeuristicInferred` |
| `VENERABLE_TEA_SET` | 古茶具套装 | Venerable Tea Set | Common | 到达[gold]休息处[/gold]后的下一场战斗开始时额外获得{Energy:energyIcons()}。 | `HeuristicInferred` |
| `VERY_HOT_COCOA` | 烫嘴可可 | Very Hot Cocoa | Ancient | 在每场战斗的第一回合额外获得[blue]{Energy:energyIcons()}[/blue]。 | `HeuristicInferred` |
| `WHISPERING_EARRING` | 低语耳环 | Whispering Earring | Ancient | 在每个回合开始时，获得{Energy:energyIcons()}。[red]瓦库将接管你的第一回合。[/red] | `HeuristicInferred` |
| `WONGOS_MYSTERY_TICKET` | 旺购神秘券 | Wongo's Mystery Ticket | Event | 在[blue]{RemainingCombats}[/blue]场战斗后，获得随机[blue]{Repeat}[/blue]件[gold]遗物[/gold]。 | `HeuristicInferred` |

