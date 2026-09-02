# STS2 v0.111.0 遗物与卡牌语义缺口收口计划（权威版）

更新时间：2026-08-30
状态：可交给其他 agent 执行；以本文件 §2 的当前基线为唯一数量口径
目标：在不破坏 M0-M2 NOSL 教师契约的前提下，收口当前单人战斗范围内的遗物和卡牌语义、模拟器 handler、真实 CLI 差分证据，并建立可进入 Reliable 的白名单。结构化覆盖、handler 可执行和行为级 Reliable 必须分开统计，禁止用前者替代后者。

## 1. 任务边界

### 1.1 必须完成

1. v0.111.0 单人战斗相关遗物的结构化、语义、模拟器执行和真实行为验证。
2. v0.111.0 单人战斗卡牌变体的语义映射、ActionCandidate、选择、随机分支和模拟器执行。
3. 真实 CLI 与影子模拟器的逐动作 ShadowDiff、重复运行和版本一致性证据。
4. 将每个对象明确分类为 Reliable、Estimated、Uncalculable 或 OutOfScope。

### 1.2 明确排除

- 地图、路线、事件、商店、奖励和完整牌局策略；
- 多人/盟友效果；
- reference/CombatSolver；
- RandomForeseer 运行时依赖；
- 使用真实 RNG、未来牌序或已实现的隐藏随机结果生成 NOSL 标签；
- 为通过门禁而修改 P0/P1 报告期望值或降低阈值。

完整 teacher snapshot 只允许用于 ShadowDiff、错误复现、RNG 审计和真实引擎验证。NOSL evaluator 只能接收公共观测派生的 NoslBeliefState。

## 2. 当前基线

开始前必须从当前文件重新生成基线，不使用旧交接文档中的数字。

### 2.1 遗物基线（2026-08-30 权威口径）

来源：`D:/STS2BestChoice/STS2BestChoice/data/relics/v0.111/relic-coverage.json`、`data/relic-card-gap-inventory.json`。开始任何批次前重新运行导出并把结果写入 baseline，禁止沿用旧交接文档中的 `20/161/21` 数字。

| 状态 | 数量 |
| --- | ---: |
| cataloged / structured / state captured | 299 |
| runtime-probed (structurally valid evidence references) | 100 |
| strict evidence-eligible probes | 23 |
| simulator-supported (handler status) | 98 |
| PartiallySupported semantic holds | 2 (`PARRYING_SHIELD`, `UNCEASING_TOP`) |
| handler-supported but evidence pending | 75 |
| no combat effect | 1 |
| UnverifiableByCli | 25 |
| Uncalculable | 56 |
| UnsupportedKnownEffect | 20（含 `GIRYA`） |
| OutOfScope（已确认非战斗） | 97 |
| Unknown | 0 |
| Reliable eligible（严格证据 + NoCombatEffect） | 24 |

200 个战斗相关遗物已经有终态：98 个已实现 handler 的 simulator-supported，当前仅 23 个 relic 通过严格报告证据，75 个 handler 条目因 Estimated/Uncalculable 报告保持 evidence-pending，2 个 `PartiallySupported` semantic hold，20 个 `UnsupportedKnownEffect`，25 个 `UnverifiableByCli`，56 个 `Uncalculable`。`PARRYING_SHIELD` 与 `UNCEASING_TOP` 虽已有模拟器 handler 和单元测试，但现有 CLI fixture 尚未分别消歧随机多目标与空手触发，必须保持 semantic hold，不得直接计入 Reliable 主标签。97 个非战斗遗物已显式转为 `OutOfScope`，未知数量为 0。

按 combat relevance 的当前缺口（`simulator_supported` 为 handler + 严格证据合格；`partially_supported` 不计入 Reliable）：

| relevance | 总数 | simulator_supported | partially_supported | unsupported | UnverifiableByCli | Uncalculable | OutOfScope | NoCombatEffect |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| turn_start | 72 | 42 | 0 | 1 | 15 | 14 | 0 | 0 |
| card_play | 28 | 11 | 1 | 2 | 8 | 6 | 0 | 0 |
| combat_passive | 16 | 12 | 0 | 0 | 0 | 4 | 0 | 0 |
| combat_start | 29 | 13 | 0 | 8 | 2 | 6 | 0 | 0 |
| combat_end | 12 | 6 | 0 | 0 | 0 | 5 | 0 | 1 |
| damage_trigger | 12 | 5 | 0 | 2 | 0 | 5 | 0 | 0 |
| hp_trigger | 3 | 0 | 0 | 3 | 0 | 0 | 0 | 0 |
| energy_trigger | 3 | 2 | 0 | 1 | 0 | 0 | 0 | 0 |
| orb_trigger | 2 | 0 | 0 | 0 | 0 | 2 | 0 | 0 |
| turn_end | 9 | 4 | 1 | 3 | 0 | 1 | 0 | 0 |
| non_combat | 113 | 3 | 0 | 0 | 0 | 13 | 97 | 0 |

`structured=True` 只表示字段可采集；`simulator_supported=True` 只表示存在 handler，二者都不等价于行为级 Reliable。

### 2.2 卡牌基线

来源：D:/STS2BestChoice/STS2BestChoice/data/cards/generated/0.111.0 和 data/card-semantic-verification.json。

| 状态 | 数量 |
| --- | ---: |
| 总变体 | 1176 |
| 基础卡 / 升级变体 | 607 / 569 |
| 全部完整结构化 | 1108 |
| 全部未结构化 | 68 |
| 单人战斗范围 | 1099 |
| 单人范围完整结构化 | 1099 |
| 单人范围 simulator executable | 1099 |
| 单人范围 runtime resolvable | 1099 |
| 单人范围 immediately executable | 458 |
| multiplayer-only | 77 |

immediately_executable=458 不等于其余卡牌缺失；其余卡牌通常需要选择、条件、触发、随机目标、动态数值或回合边界。simulator_executable=1099 也不等于 1099 张卡都已有真实 CLI/ShadowDiff 行为证据。

单人高风险类别统计。类别可重叠，不能相加：

| 类别 | 单人变体数 | 主要风险 |
| --- | ---: | --- |
| Exhaust | 204 | 随机/指定消耗、消耗触发、牌堆迁移 |
| Ethereal | 44 | 回合结束消耗、顺序和触发链 |
| Retain | 45 | 回合结束保留和临时保留 |
| Random | 97 | 随机目标、随机抽牌、随机生成 |
| Choice | 29 | choice_id、多卡选择和稳定实例 ID |
| Power/Relic trigger | 393 | 触发阶段、计数器、来源和重复结算 |

## 3. 与 M0-M2 的兼容规则

1. 不改变 NoslBeliefState 的禁止字段和 public-only 构造边界。
2. 不把 RNG state0..state3、未来牌序、隐藏弃牌顺序写入 NOSL 输入、Parquet 特征或教师主标签。
3. 不改变 TeacherWorker 的 NOSL_EXACT_OFFLINE / NOSL_BOUNDED 语义和标签降级规则。
4. 不改变稳定 card_instance_id、potion_instance_id、choice_id 和 action_id 的含义。
5. 不直接修改已通过的 P0/P1 fixture 期望值；Core 公共语义修改后必须跑完整 P0/P1 回归。
6. 不将 state_captured_only、simulator_declared、Unknown 或 UnsupportedKnownEffect 自动晋级为 Reliable。
7. 不把随机概率未知的对象强行按均匀分布处理。
8. Core/Mod 改动保留在 D:/STS2BestChoice/STS2BestChoice；模型仓库只放 training、schema、fixture、报告和数据。
9. 发现 Power 依赖变化时，先记录影响并重新跑 Power 回归，不得静默改动已验证集合。

## 4. Agent 工作目录和隔离

推荐隔离目录：

~~~
D:/STS2BestChoice/work/relic-card-completion/
  STS2SuperModel/                 # 模型仓库工作副本
  STS2BestChoice/
    STS2BestChoice.Core/           # 只复制 Core 源码和项目文件，不复制 bin/obj
    tests/                         # 需要时复制测试源码
  reports/
~~~

如果拆成多个 agent：

~~~
D:/STS2BestChoice/work/relic-completion/
D:/STS2BestChoice/work/card-completion/
D:/STS2BestChoice/work/semantic-qa/
~~~

各 agent 不共享可写 Core，不直接 push 远端 main，不直接覆盖主工作区。主 agent 逐批审查后再合并。

开始前阅读：

~~~
D:/STS2BestChoice/AGENTS.md
D:/STS2BestChoice/STS2BestChoice/AGENTS.md
D:/STS2BestChoice/STS2SuperModel/AGENTS.md
D:/STS2BestChoice/STS2SuperModel/PLAN_NOSL.md
D:/STS2BestChoice/STS2SuperModel/P1_SEMANTIC_COVERAGE_PLAN.md
D:/STS2BestChoice/STS2SuperModel/data/P0_VERIFICATION.md
D:/STS2BestChoice/STS2SuperModel/data/P1_POWER_VERIFICATION.md
D:/STS2BestChoice/STS2SuperModel/data/P1_RELIC_VERIFICATION.md
~~~

## 5. 阶段 0：基线盘点

### 5.1 命令

在模型仓库目录执行：

~~~powershell
$ErrorActionPreference = 'Stop'
python training/export_power_catalog.py
python training/run_p0_probes.py
python training/run_p1_power_probes.py --include-p0
python training/run_p1_relic_probes.py
python training/verify_repeat_runs.py
python -m pytest training -q --disable-warnings --ignore=training/test_replay_action.py
dotnet test D:/STS2BestChoice/STS2BestChoice/tests/STS2BestChoice.Tests.csproj -c Release --no-restore
python training/build_card_semantic_report.py --cards D:/STS2BestChoice/STS2BestChoice/data/cards/generated/0.111.0/cards.json --semantics D:/STS2BestChoice/STS2BestChoice/data/cards/generated/0.111.0/semantics.json --output data/card-semantic-verification.json
~~~

### 5.2 基线产物

~~~
data/relic-card-gap-baseline.json
data/relic-card-gap-baseline-test-output.txt
data/relic-card-gap-inventory.json
~~~

inventory 每项至少包含：

~~~
object_type
stable_id
display_name
game_version
scope
structured
simulator_supported
runtime_handler_resolvable
runtime_probed
support_status
evidence_level
blocking_reason
next_action
~~~

## 6. 工作线 R：遗物补齐

### R1 分类和优先级

按下列顺序处理 UnsupportedKnownEffect，再处理能够确定为战斗相关的 Unknown：

1. turn_start：能量、抽牌、费用、力量、敏捷、护盾和状态；
2. card_play：出牌计数、攻击/技能/能力触发、费用和伤害修正；
3. damage_trigger：受伤、未被格挡伤害、反击和 HP 阈值；
4. combat_start / combat_end：开战初始化和战斗内结算；
5. turn_end：格挡、弃牌、消耗和临时计数器清理；
6. 随机目标、随机生成、自动出牌和复杂计数器；
7. 纯地图、商店、奖励、事件和跨战斗遗物标记 OutOfScope。

### R2 每个遗物的实现要求

从 ModelDb、v0.111 IL、真实运行时对象和 CLI 行为确认：

- 原始 relic ID 和 canonical ID；
- owner；
- amount、counter、DynamicVars；
- 触发阶段和触发顺序；
- 一次性触发、重置和耗尽条件；
- 对 Player/Enemy/Card/Potion/Power 的影响；
- RNG stream 和 counter 消耗；
- public snapshot 与 teacher snapshot 的映射；
- handler ID、版本和 evidence level。

不得只凭 Wiki 描述晋级。Wiki 只能作为名称、文本和版本线索。

### R3 遗物 fixture 要求

每个晋级遗物至少有一个固定 seed fixture，必要时拆成多个动作报告，覆盖：

1. 遗物存在但条件未满足；
2. 条件刚好满足；
3. 触发后的 HP/Block/Energy/牌堆状态；
4. counter/DynamicVars 变化；
5. 回合边界和重置；
6. 与其他 Power/Relic/Card 的交互；
7. RNG before/after；
8. public/teacher 隔离。

文件命名：

~~~
training/fixtures/p1-relic-<slug>-commands.jsonl
data/p1-csharp-relic-<slug>-diff-report-*.json
~~~

### R4 遗物晋级条件

只有同时满足以下条件才更新 catalog：

~~~
真实 CLI fixture 存在
影子执行同一稳定 action_id
关键字段 mismatch_count == 0
Power/Relic/Card/计数器一致
RNG counter 一致
两次运行字节级一致
confidence == Reliable
版本锁完整
~~~

CLI 无法实例化的遗物必须记录 UnverifiableByCli 或 Uncalculable，并附阻塞原因；不得用空实现晋级。

## 7. 工作线 C：卡牌补齐

### C1 卡牌缺口清单

对每个单人变体检查：

1. 文本是否完整解析；
2. EffectSpec 是否保留数值、升级差异、条件、触发和来源；
3. target 是否正确；
4. card type、cost、X cost、upgrade 和 DynamicVars 是否正确；
5. Destination 是否正确：Hand/DrawPile/Discard/Exhaust/Remove；
6. Exhaust、Ethereal、Retain、Innate 和回合结束行为；
7. Power/Relic 触发链；
8. 随机目标、随机抽牌、随机弃牌/消耗和随机生成；
9. Choice、card_select、choice_id 和 selected_card_instance_ids；
10. 多敌人、重复攻击、自动出牌和跨回合效果。

### C2 卡牌处理分层

#### 层 1：结构化覆盖

- 单人范围 1099 个变体必须有完整语义记录；
- 68 个未结构化变体必须明确归入多人/盟友、非战斗或未解析原因；
- 77 个 multiplayer-only 变体保持 OutOfScope，不进入单人 Reliable 集。

#### 层 2：handler 覆盖

- 每个 semantic operation 必须映射到明确的模拟器 handler；
- 每个随机操作必须引用版本化 random operator；
- 每个生成卡模板必须可由稳定 ID 解析；
- 每个 Choice 必须生成稳定 choice_id，不能以当前手牌索引作为训练主键；
- 不能以 simulator_executable=true 代替实际 handler 测试。

#### 层 3：行为证据

每个独特语义模式必须有真实 CLI/ShadowDiff fixture。具有独立数值、随机池、动态变量或特殊触发的卡牌，每个变体都必须有独立映射或可审计的等价语义证明。

优先 fixture 类别：

~~~
消耗、虚无、保留、固有
指定弃牌、随机弃牌、随机消耗
单体、多目标、随机目标
多卡选择、card_select
生成卡、随机生成卡
X 费用、费用随能量变化
升级前后数值
Power/Relic 触发
回合开始/结束和自动出牌
~~~

### C3 卡牌测试要求

每个卡牌批次至少包含：

- 结构化编译测试；
- simulator transition 测试；
- 稳定 ActionCandidate 测试；
- 与真实 CLI 的 pre/post ShadowDiff；
- 需要随机时的概率质量测试；
- 重复运行 SHA-256 测试；
- 负向测试：缺少 target、choice、模板、概率或实例 ID 时必须降级。

卡牌 fixture 可按语义模式复用，但报告必须记录：

~~~
card_variant_id -> semantic_signature -> handler_id -> fixture/report
~~~

### C4 卡牌晋级条件

卡牌只有在语义、目标、费用、牌堆迁移、状态触发和必要的 RNG/choice 字段全部一致时，才能作为 Reliable 训练对象。仅有文本解析或静态 catalog 的卡牌保持 Estimated 或 Uncalculable。

## 8. 工作线 V：真实 CLI 与 ShadowDiff

每个遗物或卡牌 fixture 使用同一流程：

~~~
固定 v0.111 seed
启动 CLI
捕获 public observation
保存完整 teacher snapshot 到 sidecar
解析稳定合法 ActionCandidate
执行动作
捕获 post public/teacher snapshot
从公共输入构造 NOSL belief（仅用于教师标签）
从 teacher snapshot 重建 shadow（仅用于差分）
执行同一 action_id
比较真实和影子结果
保存差分报告
再次运行并比较报告 SHA-256
~~~

报告至少比较：

- Player HP/MaxHP/Block/Energy/MaxEnergy；
- Hand/Draw/Discard/Exhaust 及稳定实例 ID；
- Enemy HP/Block/Power/Intent；
- Power/Relic ID、owner、amount、DynamicVars、counter、trigger state；
- round/turn、死亡和胜利状态；
- action_id、target_id、choice_id；
- RNG counter 和 stream delta（只在 sidecar/审计报告中）。

NOSL 教师标签不能根据本次真实随机结果改写。真实结果只能用于验证某个可能分支是否与引擎一致。

## 9. Catalog、报告和状态更新

每个批次必须同时更新：

~~~
data/relics/v0.111/relic-catalog.json
data/relics/v0.111/relic-coverage.json
data/card-semantic-verification.json
data/relic-card-gap-inventory.json
data/relic-card-completion-report.md
~~~

每个对象保留：

~~~
stable_id
support_status
evidence_level
evidence_reference
verified_game_version
fixture_ids
report_ids
mismatch_count
repeat_sha256
blocking_reason
~~~

状态转换规则：

~~~
Unknown/UnsupportedKnownEffect
  -> PartiallySupported
  -> SimulatorSupported + LiveObserved
  -> Reliable eligible
~~~

只有真实差分、版本完整、双跑一致和无泄漏时才允许最后一步。

## 10. 每批验收门禁

### 10.1 回归

~~~powershell
$ErrorActionPreference = 'Stop'
dotnet test D:/STS2BestChoice/STS2BestChoice/tests/STS2BestChoice.Tests.csproj -c Release --no-restore
python -m pytest D:/STS2BestChoice/STS2SuperModel/training -q --disable-warnings --ignore=D:/STS2BestChoice/STS2SuperModel/training/test_replay_action.py
python D:/STS2BestChoice/STS2SuperModel/training/run_p1_power_probes.py --include-p0
python D:/STS2BestChoice/STS2SuperModel/training/run_p1_relic_probes.py
python D:/STS2BestChoice/STS2SuperModel/training/verify_repeat_runs.py
~~~

### 10.2 数据和隐私

~~~
public_leakage_count == 0
stable_id_missing == 0
version_mismatch == 0
malformed_rows == 0
probability_mass_error == 0
~~~

### 10.3 语义

~~~
Reliable 对象均有真实 CLI fixture
Reliable 对象 mismatch_count == 0
Reliable 对象双跑 SHA-256 一致
未知概率不进入 ExactWithKnownChance
未知语义不进入 Reliable
~~~

### 10.4 NOSL 不变性回归

至少保留：

1. public state 相同、RNG state 不同：belief signature、合法动作和标签相同；
2. 牌组构成相同、未来牌序不同：NOSL 输入和标签相同；
3. 真实引擎随机结果不同但公共观测相同：标签相同；
4. teacher snapshot 不出现在 evaluator 请求和 public Parquet 特征中。

## 11. 交付给主 agent 的内容

其他 agent 不能只回复“已完成”，必须提交：

1. 修改文件清单；
2. 新增/更新 fixture 清单；
3. 每个对象的 report 清单；
4. catalog before/after 数量；
5. Reliable、Estimated、Uncalculable、OutOfScope 数量；
6. 每个报告的 mismatch_count；
7. 双跑 SHA-256 结果；
8. Core、CLI、Training 测试完整输出；
9. 版本锁和程序集 hash；
10. 未完成对象及阻塞原因；
11. 是否修改 P0/M0-M2 文件；
12. 独立回滚测试结果；
13. 下一批建议。

必须提供短交接文件：

~~~
D:/STS2BestChoice/work/relic-card-completion/HANDOFF.md
~~~

## 12. 最终完成定义

本计划完成时必须满足：

1. 299 个遗物都有结构化状态；
2. 当前单人战斗相关遗物全部有明确 Reliable、Estimated、Uncalculable 或 OutOfScope 状态；
3. 1099 个单人卡牌变体都有完整语义和明确 handler 状态；
4. 77 个多人/盟友变体保持 OutOfScope，不进入单人训练主集；
5. 所有 Reliable 遗物/卡牌都有真实 CLI 与 ShadowDiff 证据；
6. 关键状态 mismatch_count 全部为 0；
7. 所有报告重复运行字节级一致；
8. public leakage 和 stable ID 缺失均为 0；
9. 未知概率和未验证语义没有被伪装成 Reliable；
10. M0-M2 NOSL 隐私、不变性、节点和内存门禁无回归；
11. 版本、语义数据库、scorer、schema 和 shard metadata 一致；
12. 主训练数据仍不包含 raw RNG、未来牌序、隐藏弃牌顺序或 teacher-only snapshot。

## 13. 给其他 agent 的直接任务说明

~~~
你负责 STS2 v0.111.0 单人战斗的遗物与卡牌完整语义收口。

工作目录使用：
D:/STS2BestChoice/work/relic-card-completion/
不得直接修改主工作区或远端 main；完成后提交 HANDOFF.md。

先重建 baseline inventory，再按 5-15 个遗物或一组同构卡牌批次推进。
每次修改必须有 Core 单测、CLI fixture、ShadowDiff 报告、双跑 SHA-256 和完整回归。
只有真实 CLI/ShadowDiff mismatch_count=0、版本完整、public leakage=0 且重复一致的对象才能晋级 Reliable。

严禁修改 P0/M0-M2 契约；严禁把未知概率当均匀分布；严禁把未验证对象标 Reliable；
严禁将 RNG/未来牌序写入 NOSL 输入；严禁使用 CombatSolver 或 RandomForeseer。

遗物优先处理 turn_start、card_play、damage_trigger、combat_start/end 和 turn_end；
卡牌优先处理 Exhaust、Random、Choice、生成卡、X 费用、Power/Relic trigger、
回合边界和自动出牌。多人/盟友及纯非战斗效果只做明确 OutOfScope 标注。
~~~

## 13.1 当前残余缺口的无返工执行批次

每个 agent 只领取一个批次目录，主工作区保持只读；批次结束后由主 agent 逐文件审查并合并。禁止多个 agent 同时写同一个 Core 文件、同一个 catalog 或同一个 fixture。

### 批次 R0：基线和清单锁定（先于所有实现）

1. 从当前 v0.111 catalog、inventory 和脚本重新生成 `data/relic-card-gap-baseline.json`。
2. 把每个遗物的 `stable_id → scope → support_status → blocking_reason → evidence_reference` 导出为待办清单。
3. 将当前 97 个经 hook/范围审计确认的 `non_combat` 遗物列为 `OutOfScope`；任何含战斗 hook 的对象（例如 `GIRYA`）不得进入该集合。
4. 验证 `PARRYING_SHIELD`、`UNCEASING_TOP` 是否仍处于 semantic hold。
5. 记录 baseline 的 game/commit/assembly/CLI/schema hash；后续批次不得修改 baseline 原始文件，只能新增 after 文件。

### 批次 R1：两个现有 semantic hold

**PARRYING_SHIELD**

- 使用多敌人 fixture 验证只命中一个合法目标；
- 覆盖已知 `CombatTargets` 和 masked/missing `CombatTargets`；
- 比较目标集合、概率、counter、HP 和 block；
- 目标规则字段尚未结构化时保持 hold，不得用“单敌场景通过”替代。

**UNCEASING_TOP**

- 构造打出最后一张手牌的 fixture；
- 确认卡牌先移动到最终目的地，再触发 `AfterHandEmptied`；
- 覆盖 draw pile 非空、draw pile 为空且 discard 可洗牌两种情况；
- 比较抽牌数量、牌堆迁移、Shuffle 概率和回合边界；
- 现有三次出牌 fixture 只能作为回归，不能作为空手触发证据。

### 批次 R2：战斗遗物实现与差分

按 `turn_start → card_play → damage_trigger → combat_start/end → turn_end → combat_passive` 分组，每批 5–15 个同构遗物。每个对象必须先完成：

```text
IL/运行时证据
→ handler/状态字段
→ 条件未满足 fixture
→ 条件满足 fixture
→ 触发后状态 fixture
→ ShadowDiff
→ 双跑
```

优先解除 56 个 `Uncalculable` 中对一回合战斗影响最大的对象；需要未建模 subsystem 的对象应直接保留 `Uncalculable`，同时写清楚解除阻塞所需的 subsystem，不做空实现。25 个 `UnverifiableByCli` 先判断是否能通过稳定 ID、trace 扩展或 teacher-sidecar 消歧；若仍受 public observation 限制，保持终态并停止重复尝试。

### 批次 R3：非战斗遗物分类

对当前 97 个经审计的 `non_combat` 遗物只做：

1. 依据继承的 runtime hook、IL/调用目标和触发阶段确认 `scope=non_combat`；
2. 仅在没有任何战斗 hook 时写入 `support_status=out_of_scope`；
3. 写入证据来源、方法名和排除原因；
4. 更新 catalog、coverage、inventory、baseline-after、manifest 和计数测试。

方法名字符串扫描不能单独决定范围。`AfterFlush`、`AfterShuffle`、
`AfterDiedToDoom`、`AfterDeath`、`ShouldFlush`、`ShouldDieLate`、
`ModifyWeakMultiplier`、`ModifyVulnerableMultiplier`、`AfterStarsSpent`，
以及 `AfterRoomEntered` 中针对 `CombatRoom`/Elite 的实现，必须判为战斗
hook；`GIRYA` 是该规则的回归哨兵（其 `AfterRoomEntered` 在战斗开始施加
Strength）。

不得为地图、商店、奖励、事件或纯跨战斗效果编写战斗 handler，也不得将
未满足严格证据门禁的对象标为 Reliable。

### 批次 C0：卡牌语义和 handler 一致性

对 1099 个单人变体执行机器化检查：

```text
semantic_card_signature
→ operations
→ EffectKind/handler
→ target/cost/destination
→ choice/action contract
→ restriction/quality
```

发现缺口时，按 semantic signature 聚合，不按单张卡牌重复修复。每个签名必须列出：涉及变体数、handler、随机算子、choice 规则、已有 fixture、缺失 fixture、负责人和下一步。

### 批次 C1：高风险卡牌行为证据

优先为以下类别建立真实 CLI/ShadowDiff fixture：

```text
Exhaust / Ethereal / Retain
指定弃牌 / 随机弃牌 / 随机消耗
随机目标 / 多目标 / 多段攻击
Choice / card_select
生成卡 / 随机生成卡
X 费用 / 动态伤害和格挡
Power/Relic trigger
回合开始/结束
自动出牌 / 牌堆顶底迁移
```

fixture 可以按等价语义模式复用，但报告必须保留：

```text
card_variant_id
semantic_signature
handler_id
fixture_id
report_id
```

具有独立升级数值、动态变量、随机池、触发条件或目标规则的变体必须单独映射或提供可审计等价证明。

### 批次 V0：统一验收和合并

每个批次交付前必须运行：

```powershell
$ErrorActionPreference = 'Stop'
dotnet test D:/STS2BestChoice/STS2BestChoice/tests/STS2BestChoice.Tests.csproj -c Release --no-restore
python -m pytest D:/STS2BestChoice/STS2SuperModel/training -q --disable-warnings --ignore=D:/STS2BestChoice/STS2SuperModel/training/test_replay_action.py
python D:/STS2BestChoice/STS2SuperModel/training/run_p0_probes.py
python D:/STS2BestChoice/STS2SuperModel/training/run_p1_power_probes.py --include-p0
python D:/STS2BestChoice/STS2SuperModel/training/run_p1_relic_probes.py
python D:/STS2BestChoice/STS2SuperModel/training/verify_repeat_runs.py
```

交付必须同时包含：

```text
修改文件清单
fixture/report 清单
catalog before/after
Reliable/Estimated/Uncalculable/OutOfScope 数量
每份报告 mismatch_count
双跑 SHA-256
Core/CLI/Training 原始输出
版本锁和 assembly hash
未完成对象及阻塞原因
独立回滚测试
HANDOFF.md
```

主 agent 的合并顺序固定为：

```text
R0 → R1 → R2/R3 与 C0/C1 并行 → V0 → 重新生成完整 catalog/manifest
```

未通过 V0 的批次不能更新 Reliable 白名单，也不能进入 NOSL 教师主训练集。

## 14. 完成后的下一阶段

本计划完成前，不生成大规模 Reliable NOSL 主训练集。完成后按以下顺序继续：

~~~
1k NOSL exact-label pilot
  -> 概率/标签/不变性门禁
  -> 10k 分层 pilot
  -> 100k 数据集
  -> PyTorch policy/value/risk training
  -> ONNX offline evaluation
  -> C# runtime integration
~~~
