# Slay the Spire 2 v0.111.0 Power 覆盖率与目录报告

- **游戏版本**: `0.111.0` (commit `41cef1ea`)
- **程序集 SHA-256**: `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`
- **CLI 协议**: `0.2.0`
- **生成时间 (UTC)**: `2026-08-26T00:00:00Z`

## 汇总统计

| 指标 | 数量 | 占比 |
| :--- | :--- | :--- |
| 游戏目录 Power 总数 | **283** | 100.0% |
| 已结构化数量 | **283** | 100.0% |
| 已捕获状态数量 | **283** | 100.0% |
| 已检查 IL 语义数量 | **0** | 0.0% |
| 已完成运行时探针数量 | **9** | 3.2% |
| 模拟器声明映射数量（未由本目录验证行为完整性） | **64** | 22.6% |
| 模拟器行为验证完整支持数量 | **9** | 3.2% |
| 仅状态捕获数量 | **210** | 74.2% |
| 无战斗动作影响数量 | **0** | 0% |
| 未知证据 Power 数量 | **30** | 10.6% |

## 模拟器已声明映射 Power（仍需行为差分）

| Stable ID | 中文名 | 英文名 | 触发阶段 | 证据等级 | 状态映射 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `ACCELERANT` | 触媒 | Accelerant | Passive | `Unknown` | `ACCELERANT` |
| `ACCURACY` | 精准 | Accuracy | DamageReceived | `HeuristicInferred` | `ACCURACY` |
| `AFTERIMAGE` | 余像 | Afterimage | CardPlay | `LiveObserved` | `AFTERIMAGE` |
| `ARTIFACT` | 人工制品 | Artifact | Passive | `HeuristicInferred` | `ARTIFACT` |
| `AUTOMATION` | 自动化 | Automation | CardPlay | `HeuristicInferred` | `AUTOMATION` |
| `BARRICADE` | 壁垒 | Barricade | BlockGain | `LiveObserved` | `BARRICADE` |
| `BORROWED_TIME` | 预借时间 | Borrowed Time | TurnEnd | `HeuristicInferred` | `BORROWED_TIME` |
| `BUFFER` | 缓冲 | Buffer | DamageReceived | `HeuristicInferred` | `BUFFER` |
| `BURST` | 爆发 | Burst | TurnEnd, CardPlay | `HeuristicInferred` | `BURST` |
| `COLOSSUS` | 巨像 | Colossus | TurnEnd, DamageReceived | `HeuristicInferred` | `COLOSSUS` |
| `CORROSIVE_WAVE` | 腐蚀波 | Corrosive Wave | TurnEnd, CardPlay | `HeuristicInferred` | `CORROSIVE_WAVE` |
| `CORRUPTION` | 腐化 | Corruption | CardPlay | `HeuristicInferred` | `CORRUPTION` |
| `CRIMSON_MANTLE` | 绯红披风 | Crimson Mantle | CardPlay, DamageReceived | `HeuristicInferred` | `CRIMSON_MANTLE` |
| `DANSE_MACABRE` | 死亡之舞 | Danse Macabre | CardPlay | `HeuristicInferred` | `DANSE_MACABRE` |
| `DEMON_FORM` | 恶魔形态 | Demon Form | TurnStart | `LiveObserved` | `DEMON_FORM` |
| `DEXTERITY` | 敏捷 | Dexterity | BlockGain | `LiveObserved` | `DEXTERITY` |
| `DOOM` | 灾厄 | Doom | TurnEnd | `HeuristicInferred` | `DOOM` |
| `DOUBLE_DAMAGE` | 双倍伤害 | Double Damage | TurnEnd, DamageReceived | `HeuristicInferred` | `DOUBLE_DAMAGE` |
| `DUPLICATION` | 复制 | Duplication | TurnEnd, CardPlay | `HeuristicInferred` | `DUPLICATION` |
| `ECHO_FORM` | 回响形态 | Echo Form | TurnStart, CardPlay | `HeuristicInferred` | `ECHO_FORM` |
| `ENVENOM` | 涂毒 | Envenom | DamageReceived | `HeuristicInferred` | `ENVENOM` |
| `FAN_OF_KNIVES` | 刀扇 | Fan of Knives | Passive | `Unknown` | `FAN_OF_KNIVES` |
| `FASTEN` | 勒紧 | Fasten | BlockGain | `HeuristicInferred` | `FASTEN` |
| `FERAL` | 野性 | Feral | TurnStart, CardPlay | `HeuristicInferred` | `FERAL` |
| `FLAME_BARRIER` | 火焰屏障 | Flame Barrier | TurnEnd, DamageReceived | `HeuristicInferred` | `FLAME_BARRIER` |
| `FOCUS` | 集中 | Focus | Passive | `HeuristicInferred` | `FOCUS` |
| `HAUNT` | 纠缠 | Haunt | CardPlay | `HeuristicInferred` | `HAUNT` |
| `HELLRAISER` | 地狱狂徒 | Hellraiser | TurnEnd, CardPlay | `HeuristicInferred` | `HELLRAISER` |
| `INFERNO` | 狱火 | Inferno | CardPlay, DamageReceived | `HeuristicInferred` | `INFERNO` |
| `INFINITE_BLADES` | 无尽刀刃 | Infinite Blades | Passive | `HeuristicInferred` | `INFINITE_BLADES` |
| `ITERATION` | 迭代 | Iteration | CardPlay | `HeuristicInferred` | `ITERATION` |
| `JUGGERNAUT` | 势不可当 | Juggernaut | BlockGain | `HeuristicInferred` | `JUGGERNAUT` |
| `JUGGLING` | 杂耍 | Juggling | TurnEnd, CardPlay | `HeuristicInferred` | `JUGGLING` |
| `LETHALITY` | 致死性 | Lethality | DamageReceived | `HeuristicInferred` | `LETHALITY` |
| `MIND_ROT` | 心灵腐化 | Mind Rot | Passive | `HeuristicInferred` | `MIND_ROT` |
| `MONARCHS_GAZE` | 王之凝视 | Monarch's Gaze | DamageReceived | `HeuristicInferred` | `MONARCHS_GAZE` |
| `MONOLOGUE` | 独白 | Monologue | TurnEnd, CardPlay | `HeuristicInferred` | `MONOLOGUE` |
| `NEUROSURGE` | 精神过载 | Neurosurge | TurnStart | `HeuristicInferred` | `NEUROSURGE` |
| `NOSTALGIA` | 怀旧 | Nostalgia | CardPlay | `HeuristicInferred` | `NOSTALGIA` |
| `NO_BLOCK` | 不可格挡 | No Block | TurnEnd, BlockGain | `HeuristicInferred` | `NO_BLOCK` |
| `ONE_FOR_ALL` | ONE_FOR_ALL | ONE_FOR_ALL | DamageReceived | `HeuristicInferred` | `ONE_FOR_ALL` |
| `ONE_TWO_PUNCH` | 连环拳 | One-Two Punch | TurnEnd, CardPlay | `HeuristicInferred` | `ONE_TWO_PUNCH` |
| `ORBIT` | 环绕轨道 | Orbit | Passive | `HeuristicInferred` | `ORBIT` |
| `PAGESTORM` | 书页风暴 | Pagestorm | CardPlay | `HeuristicInferred` | `PAGESTORM` |
| `PALE_BLUE_DOT` | 暗淡蓝点 | Pale Blue Dot | TurnEnd, CardPlay | `HeuristicInferred` | `PALE_BLUE_DOT` |
| `PANACHE` | 神气制胜 | Panache | TurnEnd, CardPlay | `HeuristicInferred` | `PANACHE` |
| `PHANTOM_BLADES` | 幻影之刃 | Phantom Blades | CardPlay, DamageReceived | `HeuristicInferred` | `PHANTOM_BLADES` |
| `PLATING` | 覆甲 | Plating | TurnStart, TurnEnd | `HeuristicInferred` | `PLATING` |
| `POISON` | 中毒 | Poison | TurnStart, DamageReceived | `HeuristicInferred` | `POISON` |
| `PREP_TIME` | 准备时间 | Prep Time | TurnStart | `HeuristicInferred` | `PREP_TIME` |
| `RAGE` | 狂怒 | Rage | TurnEnd, CardPlay | `HeuristicInferred` | `RAGE` |
| `REAPER_FORM` | 死神形态 | Reaper Form | DamageReceived | `HeuristicInferred` | `REAPER_FORM` |
| `RUPTURE` | 撕裂 | Rupture | CardPlay, DamageReceived | `LiveObserved` | `RUPTURE` |
| `SERPENT_FORM` | 群蛇形态 | Serpent Form | CardPlay | `HeuristicInferred` | `SERPENT_FORM` |
| `SHADOWMELD` | 融入暗影 | Shadowmeld | TurnEnd, BlockGain | `HeuristicInferred` | `SHADOWMELD` |
| `SHADOW_STEP` | 暗影步 | Shadow Step | TurnStart | `HeuristicInferred` | `SHADOW_STEP` |
| `SIGNAL_BOOST` | 信号增强 | Signal Boost | CardPlay | `HeuristicInferred` | `SIGNAL_BOOST` |
| `SLOTH` | 懒惰 | Sloth | TurnStart, CardPlay | `HeuristicInferred` | `SLOTH` |
| `SPEEDSTER` | 速行者 | Speedster | CardPlay | `HeuristicInferred` | `SPEEDSTER` |
| `SPIRIT_OF_ASH` | 灰烬之灵 | Spirit of Ash | CardPlay | `HeuristicInferred` | `SPIRIT_OF_ASH` |
| `STAMPEDE` | 惊逃 | Stampede | CardPlay | `HeuristicInferred` | `STAMPEDE` |
| `STORM` | 雷暴 | Storm | CardPlay | `HeuristicInferred` | `STORM` |
| `STRENGTH` | 力量 | Strength | DamageReceived | `LiveObserved` | `STRENGTH` |
| `SUBROUTINE` | 子程序 | Subroutine | CardPlay | `HeuristicInferred` | `SUBROUTINE` |
| `THE_GAMBIT` | 孤注一掷 | The Gambit | DamageReceived | `HeuristicInferred` | `THE_GAMBIT` |
| `THORNS` | 荆棘 | Thorns | DamageReceived | `HeuristicInferred` | `THORNS` |
| `TRACKING` | 跟踪 | Tracking | DamageReceived | `HeuristicInferred` | `TRACKING` |
| `UNMOVABLE` | 不动 | Unmovable | BlockGain | `HeuristicInferred` | `UNMOVABLE` |
| `VICIOUS` | 凶恶 | Vicious | Passive | `HeuristicInferred` | `VICIOUS` |
| `VIGOR` | 活力 | Vigor | DamageReceived | `LiveObserved` | `VIGOR` |
| `VULNERABLE` | 易伤 | Vulnerable | TurnEnd, DamageReceived | `LiveObserved` | `VULNERABLE` |
| `WASTE_AWAY` | 衰朽 | Waste Away | Passive | `HeuristicInferred` | `WASTE_AWAY` |
| `WEAK` | 虚弱 | Weak | TurnEnd, DamageReceived | `LiveObserved` | `WEAK` |

