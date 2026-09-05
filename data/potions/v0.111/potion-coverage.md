# v0.111 药水覆盖率审计

> 分母来自 `MegaCrit.Sts2.Core.Models.ModelDb.AllPotions`；本报告只审计，不实现药水语义。

## 汇总

| 指标 | 数量 |
|---|---:|
| 游戏目录药水总数 | 66 |
| 已结构化 | 66 |
| 已捕获状态 | 66 |
| 已检查 IL | 65 |
| 已完成真实引擎观测 | 30 |
| 严格证据合格 | 28 |
| 模拟器声明支持 | 28 |
| 已知不支持 | 36 |
| OutOfScope | 2 |
| Unknown | 0 |

## 逐项

| ID | 名称 | 稀有度 | IL | 探针 | 模拟器 | 严格证据 | 状态 | 下一步 |
|---|---|---|---:|---:|---:|---:|---|---|
| `AMBERGRIS` | Ambergris | Event | ✓ | ✓ | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `ASHWATER` | 灰水 | Uncommon | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `ATTACK_POTION` | 攻击药水 | Common | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `BEETLE_JUICE` | 甲虫汁 | Rare | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `BLESSING_OF_THE_FORGE` | 熔炉的祝福 | Uncommon | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `BLOCK_POTION` | 格挡药水 | Common | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `BLOOD_POTION` | 鲜血药水 | Common | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `BONE_BREW` | 骨头酿 | Uncommon | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `BOTTLED_POTENTIAL` | 瓶装潜能 | Rare | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `CLARITY` | 明晰提取物 | Uncommon | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `COLORLESS_POTION` | 无色药水 | Common | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `COSMIC_CONCOCTION` | 宇宙药剂 | Rare | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `CUNNING_POTION` | 狡诈药水 | Uncommon | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `CURE_ALL` | 痊愈药水 | Uncommon | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `DEPRECATED_POTION` | 废弃药水 | None | — | — | — | — | OutOfScope | None |
| `DEXTERITY_POTION` | 敏捷药水 | Common | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `DISTILLED_CHAOS` | 精炼混沌 | Rare | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `DROPLET_OF_PRECOGNITION` | 预知之滴 | Rare | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `DUPLICATOR` | 复制药水 | Uncommon | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `ENERGY_POTION` | 能量药水 | Common | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `ENTROPIC_BREW` | 混沌药水 | Rare | ✓ | ✓ | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `ESSENCE_OF_DARKNESS` | 黑暗精华 | Rare | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `EXPLOSIVE_AMPOULE` | 爆炸安瓿 | Common | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `FAIRY_IN_A_BOTTLE` | 瓶中精灵 | Rare | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `FIRE_POTION` | 火焰药水 | Common | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `FLEX_POTION` | 肌肉药水 | Common | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `FOCUS_POTION` | 集中药水 | Common | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `FORTIFIER` | 固化药水 | Uncommon | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `FOUL_POTION` | 污浊药水 | Event | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `FRUIT_JUICE` | 果汁 | Rare | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `FYSH_OIL` | 异鱼之油 | Uncommon | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `GAMBLERS_BREW` | 赌徒特酿 | Uncommon | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `GHOST_IN_A_JAR` | 罐装幽灵 | Rare | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `GIGANTIFICATION_POTION` | 超巨化药水 | Rare | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `GLOWWATER_POTION` | 发光水 | Event | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `HEART_OF_IRON` | 铁心药水 | Uncommon | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `KINGS_COURAGE` | 王之勇气 | Uncommon | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `LIQUID_BRONZE` | 流动铜液 | Uncommon | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `LIQUID_MEMORIES` | 液态记忆 | Rare | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `LUCKY_TONIC` | 幸运补剂 | Rare | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `MAZALETHS_GIFT` | 马萨雷斯的赠礼 | Rare | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `MOCK_DISCARD_AND_ADD_SHIVS_POTION` | Mock Discard And Add Shivs Potion | None | ✓ | — | — | — | OutOfScope | None |
| `OROBIC_ACID` | 欧洛巴斯之酸 | Rare | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `POISON_POTION` | 毒药水 | Common | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `POTION_OF_BINDING` | 缚魂药水 | Uncommon | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `POTION_OF_CAPACITY` | 扩容药水 | Uncommon | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `POTION_OF_DOOM` | 灾厄药水 | Common | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `POTION_SHAPED_ROCK` | 药水形状的石头 | Token | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `POT_OF_GHOULS` | 尸鬼瓮 | Rare | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `POWDERED_DEMISE` | 消亡粉末 | Uncommon | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `POWER_POTION` | 能力药水 | Common | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `RADIANT_TINCTURE` | 明耀酊剂 | Uncommon | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `REGEN_POTION` | 再生药水 | Uncommon | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `SHACKLING_POTION` | 镣铐药水 | Rare | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `SHIP_IN_A_BOTTLE` | 瓶中船 | Rare | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `SKILL_POTION` | 技能药水 | Common | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `SNECKO_OIL` | 异蛇之油 | Rare | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `SOLDIERS_STEW` | 士兵炖汤 | Rare | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `SPEED_POTION` | 速度药水 | Common | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `STABLE_SERUM` | 稳定血清 | Uncommon | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `STAR_POTION` | 星星药水 | Common | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `STRENGTH_POTION` | 力量药水 | Common | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `SWIFT_POTION` | 迅捷药水 | Common | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `TOUCH_OF_INSANITY` | 癫狂之触 | Uncommon | ✓ | — | — | — | UnsupportedKnownEffect | Implement simulator handler, then add version-locked CLI ShadowDiff fixture |
| `VULNERABLE_POTION` | 易伤药水 | Common | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |
| `WEAK_POTION` | 虚弱药水 | Common | ✓ | ✓ | ✓ | ✓ | SimulatorSupported | None |

## 来源与边界

- 权威分母：游戏程序集注册表 `ModelDb.AllPotions`。
- 名称与说明：v0.111 本地化文件，仅作可读交叉引用，不决定语义或数值。
- IL：目录记录每个声明方法的 IL SHA-256；无实现体的废弃条目不计为已检查。
- 严格证据：版本锁匹配、`strict_public_state`、`Reliable`、`match=true`、`mismatch_count=0`。
- `structured` 表示已有逐药水审计结构；`reliable_eligible` 表示可进入 Reliable NOSL 数据，两者不得混用。
