 # STS2 全局动态决策系统构想
 
 更新时间：2026-08-31
 状态：Idea / GUI 手动标注入口已实现；全局模型与生产教师尚未实施
 当前约束：不改变现役“战斗中单回合最优解”模型的范围。
 
 ## 1. 背景
 
 当前 STS2SuperModel 聚焦战斗中一个玩家回合的最优动作。未来可以在其上增加：
 
 1. 动态路线规划；
 2. 篝火休息或锻造，以及锻造卡牌选择；
 3. 问号事件选项；
 4. 先古之民选项；
 5. 商店、奖励和其他节点决策；
 6. 根据每次战斗结果重新规划后续路线。
 
 这会把系统从单回合战斗决策扩展为完整牌局的分层决策系统。
 
 ## 2. 总体原则
 
 不把地图、事件、篝火和战斗动作直接塞进一个巨型动作空间。
 
 推荐使用分层模块化架构：
 
 - GlobalDecisionOrchestrator：判断当前决策类型并调用对应模块；
 - RoutePlanner：规划和动态修改路线；
 - CampfirePolicy：休息、锻造及锻造卡牌选择；
 - EventPolicy：问号事件与先古之民选项；
 - ShopRewardPolicy：商店和奖励决策；
 - CombatPolicy：保留当前单回合战斗模型；
 - Expectimax / Shadow Simulator：对未知、高风险或低置信度决策兜底。
 
 各模块可以共享部分 RunState/Deck/Relic 编码器，但使用独立动作头和独立训练数据。
 
 ## 3. 动态路线规划
 
 ### 3.1 输入
 
 RoutePlanner 读取：
 
 - 完整可见地图图结构；
 - 当前节点和可选分支；
 - 剩余楼层；
 - 精英、火堆、商店、问号、宝箱和 Boss；
 - 当前 HP / Max HP；
 - 卡组质量、升级程度和关键能力；
 - 遗物、药水、金币；
 - 当前角色、难度、Act 和 Boss；
 - 战斗模型估计的胜率、HP 损失和风险；
 - 后续节点的期望收益与死亡风险。
 
 ### 3.2 路线价值
 
 路线不能简单按“精英越多、火堆越多越好”评价。
 
 路线价值应综合：
 
 - 精英奖励；
 - 火堆恢复或升级价值；
 - 商店购买机会；
 - 问号事件期望价值；
 - Boss 准备程度；
 - 普通战斗消耗；
 - HP 和药水消耗风险；
 - 金币机会成本；
 - 卡组未成型风险；
 - 路线死亡概率。
 
 可以采用带风险约束的目标：
 
 路线收益 = 奖励价值 + 成长价值 + Boss 准备价值 - HP 风险 - 死亡风险 - 资源机会成本。
 
 ### 3.3 动态重规划
 
 路线不是开局规划一次后固定执行。
 
 应该在以下时点重新规划：
 
 - 每次战斗结束；
 - 获得或失去关键卡牌；
 - 获得遗物或药水；
 - HP 大幅下降；
 - 金币达到商店阈值；
 - 路线岔路出现；
 - 精英或 Boss 风险估计变化。
 
 例如原计划经过精英，但战斗后 HP 很低、药水耗尽，则在下一个岔路改走火堆或安全路线。
 
 ### 3.4 初期算法
 
 初期优先使用：
 
 - MapGraph；
 - Beam Search / MCTS / 动态规划；
 - 学习到的 RouteValueModel；
 - 战斗模型提供的胜率和 HP 损失估计；
 - 风险约束。
 
 不要求一开始就训练端到端路线策略。
 
 ## 4. 篝火决策
 
 CampfirePolicy 的动作包括：
 
 - Rest；
 - UpgradeCard(card_instance_id)；
 - 其他角色或遗物提供的篝火动作。
 
 输入包括：
 
 - 当前 HP；
 - 当前路线；
 - 下一场精英或 Boss；
 - 完整卡组及升级状态；
 - 卡组缺少的伤害、格挡、抽牌、能量、AOE 和成长能力；
 - 遗物、药水和难度；
 - 休息与升级对未来战斗胜率的影响。
 
 锻造不能只看单张卡牌的即时数值，应评估升级对后续多场战斗、精英和 Boss 的长期价值。
 
 ## 5. 问号事件与先古之民
 
 EventPolicy 输入：
 
 - 事件 ID；
 - 选项 ID；
 - 结构化选项效果；
 - 当前 HP、金币、卡组、遗物和药水；
 - 当前路线和 Act；
 - 选项的立即收益、长期收益、随机结果和风险。
 
 输出：
 
 - ChooseEventOption(option_id)；
 - ChooseAncientOption(option_id)；
 - LeaveEvent；
 - Proceed。
 
 事件价值应计算：
 
 选项价值 = 立即收益 + 长期成长 + 路线价值变化 - HP 风险 - 金币机会成本 - 卡组污染风险。
 
 普通问号事件和先古之民可以共享基础编码器，但应保留不同的 DecisionType。
 
 ## 6. 分层数据契约
 
 建议按决策类型拆分数据：
 
 - combat_decision.jsonl；
 - route_decision.jsonl；
 - campfire_decision.jsonl；
 - event_decision.jsonl；
 - ancient_decision.jsonl；
 - shop_decision.jsonl；
 - reward_decision.jsonl。
 
 共享字段：
 
 - game/version metadata；
 - run_seed；
 - episode_id；
 - public_state_hash；
 - teacher_state_hash；
 - provenance；
 - confidence；
 - label_quality。
 
 不同模块保留独立的合法动作契约，不能混用卡牌索引、地图路径编号和事件选项编号作为跨进程主键。
 
 ## 7. 推荐模型结构
 
 推荐方案：
 
 共享 RunState / Deck / Relic Encoder
     ├── Route Head
     ├── Campfire Head
     ├── Event / Ancient Head
     ├── Shop / Reward Head
     └── Combat Head
 
 首版也可以完全分开训练：
 
 - CombatPolicy；
 - CampfirePolicy；
 - EventPolicy；
 - RouteValueModel。
 
 最后由 GlobalDecisionOrchestrator 统一调用。
 
 ## 8. 训练顺序
 
 建议分阶段：
 
 1. 保持现有 CombatPolicy 不变；
 2. 使用 sts2-cli 的 get_map 建立 MapGraph schema；
 3. 结构化篝火、事件和先古之民决策；
 4. 建立 CampfirePolicy；
 5. 建立 EventPolicy / AncientPolicy；
 6. 建立 RouteValueModel；
 7. 使用搜索加 RouteValueModel 动态重规划；
 8. 建立 GlobalDecisionOrchestrator；
 9. 分模块离线评测；
 10. 最后再考虑共享编码器或多任务联合训练。
 
 ## 9. 硬件影响
 
 模块化方案增加最多的是：
 
 - 数据量；
 - 轨迹长度；
 - CPU 并行模拟；
 - RAM 和 SSD 占用；
 - 长期价值标签生成成本。
 
 它不一定要求更大的单个神经网络。
 
 当前 9950X3D、32GB RAM、12GB GPU 可以完成：
 
 - 单回合战斗模型；
 - 篝火和事件小模型；
 - 路线价值模型原型；
 - 中小规模离线训练。
 
 开始大规模全局轨迹生成后，优先把 RAM 升级到 64GB。12GB GPU 仍可训练模块化小模型；只有使用大型统一 Transformer 时才会明显受显存限制。
 
 ## 10. 当前不做的事情
 
 在进入实施前暂不：
 
 - 扩大当前 CombatSnapshot 为全局牌局状态；
 - 把地图字段直接加入现有战斗 Policy；
 - 用一个模型输出所有类型动作；
 - 开始全局在线 RL；
 - 混合战斗、路线、事件标签；
 - 在 P1 语义覆盖完成前生成最终全局训练集。
 
 ## 11. 未来启动条件
 
 开始实施全局系统前至少满足：
 
 - P1 Power、遗物和卡牌语义覆盖达到目标；
 - 当前战斗模型拥有可用的胜率、HP 损失和风险估计；
 - CLI 地图、篝火、事件和先古之民状态可结构化导出；
 - 每类决策都有稳定 ID；
 - 数据版本和 public/teacher 隔离门禁可复用；
 - 有明确的全局评分函数和死亡风险约束。
 
 ## 12. 结论
 
 该构想可行，但应采用分层模块化系统，而不是单一巨型模型。
 
 建议最终结构：
 
 动态路线规划
     → 节点类型决策
     → 篝火 / 事件 / 商店模块
     → 战斗模块
     → Expectimax / Shadow Simulator 兜底
     → 根据新状态重新规划。
 
现阶段继续保持“单回合战斗高手模型”为主线，先结构化全局决策所需的数据契约，待 P1 完成后再单独立项。

 ## 13. 固定种子与全局状态复现

 “输入种子显示整局内容”只能在固定运行上下文下成立。全局复现键应定义为：

 ```text
 RunKey =
   game_version
   + game_commit
   + assembly_sha256
   + cli_protocol_version
   + character
   + player_count
   + ascension
   + game_mode
   + modifiers
   + unlock_profile_hash
   + run_seed
 ```

 运行中的状态还取决于此前的动作和各 RNG 流消费位置：

 ```text
 State(t) = F(RunKey, action_0, action_1, ..., action_(t-1))
 ```

 v0.111 使用多条由 run seed 派生的 RNG 流。地图、事件池、战斗遭遇、奖励、商店、变牌、抽牌和敌人行为并不共享一个简单的全局随机游标。存档需要保存每条流的 counter，GUI 也必须保存这些 counter 或等价的完整 checkpoint。

 因此应区分三类内容：

 1. **初始可重建内容**：Act、Boss、地图拓扑、节点坐标、节点类型和部分预生成的遭遇/事件池；
 2. **路径条件内容**：进入节点后生成的具体敌人、事件选项、卡牌奖励、商店库存、宝箱遗物和未知点结果；
 3. **状态条件内容**：受 HP、金币、卡组、遗物、药水、解锁状态、已访问事件和此前 RNG 消耗影响的选项与结果。

 相同 seed 不同路线会产生不同的后续状态。seed 只能作为复现元数据和数据切分键，不能直接作为学生模型特征。

 ## 14. Seed/Run Explorer GUI 定位

 建议 GUI 定位为：

 ```text
 固定上下文的 Seed/Run Explorer
 + 真实 CLI 轨迹采集器
 + 人工路线/事件/商店/奖励标注器
 + checkpoint 分支浏览器
 ```

 不做成“只输入 seed 就给出一张整局唯一答案表”。未进入节点的未来内容必须显示为 `conditional`、`predicted` 或 `unknown`，并记录来源和证据等级。

 GUI 应提供两个视图：

 - **Player View**：只显示当时玩家实际能看到的地图和决策信息，内容可作为 `PublicStateView`；
 - **Research/Teacher View**：显示通过分支运行得到的未来结果、隐藏牌堆和精确 RNG，仅用于审计、教师标注和错误分析。

 研究视图中的未来内容不得自动写入学生的 public observation，否则人工标注会带入未来信息。

 ## 15. 当前 CLI 能力与需要补齐的接口

 当前 `sts2-cli-v0111` 已经具备 GUI MVP 所需的主要命令：

 ```text
 start_run(character, ascension, seed)
 get_map
 action/select_map_node
 enter_room
 write_continue_save
 load_save
 get_combat_snapshot
 ```

 `get_map` 当前返回：

 - 地图行和节点；
 - `col`、`row` 坐标；
 - 节点类型；
 - 子节点连接；
 - `visited`、`current`；
 - Boss ID 和名称。

 当前 `get_map` 不会在地图预览阶段直接给出每个节点的全部未来内容。具体遭遇、事件、商店、奖励和未知点通常要等进入节点后由真实引擎生成。

 全局 GUI 后续可补充以下只读能力：

 - `get_run_context`：版本、角色、升华、模式、解锁和 seed；
 - `get_map_node_state`：指定节点的当前可见详情；
 - `preview_map_node`：从 checkpoint 克隆后预览一个节点，不污染主线；
 - `save_checkpoint`/`load_checkpoint`：保存和恢复完整 RunState 与 RNG counters；
 - `get_run_snapshot(view=public|teacher|audit)`：统一输出全局状态；
 - `get_decision_history`：输出稳定动作 ID、前后状态 hash 和 RNG delta。

 ## 16. GUI 工作流

 推荐工作流如下：

 1. 输入 seed、角色、Ascension、模式、修饰器和解锁配置；
 2. 启动固定版本的真实 CLI；
 3. 处理 Neow/初始事件；
 4. 调用 `get_map`，渲染全地图拓扑和节点类型；
 5. 在每个决策点自动捕获状态，人工选择路线、事件、篝火、商店或奖励动作；
 6. 执行动作后自动捕获 post-state、HP、金币、卡组、遗物、药水和 RNG counter；
 7. 在决策前保存 checkpoint，需要比较其他选项时从 checkpoint 建立独立分支；
 8. 将真实主线和反事实分支分别写入轨迹；
 9. 导出 JSONL，并由后处理器生成 Parquet 和 DatasetManifest。

 人工主要输入“选择了什么”和“为什么选择”。HP、金币、卡组、遗物、药水和战斗结果应优先从 CLI/实机快照自动采集；只有采集不到的字段才允许人工修正，并保留 `manual_override` 标记。

 ## 17. 全局决策数据契约

 全局数据和当前战斗数据分开保存，但共享版本门禁、稳定 ID 和 public/teacher 隔离原则。建议按决策类型拆分：

 ```text
 route_decision.jsonl
 campfire_decision.jsonl
 event_decision.jsonl
 ancient_decision.jsonl
 shop_decision.jsonl
 reward_decision.jsonl
 combat_decision.jsonl
 ```

 每条全局记录至少包含：

 ```text
 record_id
 schema_version
 game_version / game_commit / assembly_sha256
 cli_protocol_version
 run_seed
 episode_id / branch_id / parent_checkpoint_id
 character / ascension / act / floor
 node_id / node_type / decision_type
 public_state_hash / teacher_state_hash
 public_state
 teacher_state_reference
 legal_actions
 selected_action
 action_source
 pre_state_hash / post_state_hash
 rng_counters_before / rng_counters_after
 hp / max_hp / gold
 deck_diff / relic_diff / potion_diff
 realized_outcome
 predicted_outcome
 confidence / label_quality / provenance
 ```

 稳定动作 ID 示例：

 ```text
 map:act1:row7:col3
 event:EVENT_ID:option:OPTION_ID
 ancient:ANCIENT_ID:option:OPTION_ID
 campfire:rest
 campfire:upgrade:card:STRIKE_IRONCLAD:002
 shop:buy:offer:OFFER_ID
 reward:pick:card:CARD_INSTANCE_ID
 reward:skip
 ```

 `action_source` 应区分 `human_observed`、`cli_replayed`、`counterfactual_branch` 和 `teacher_generated`；`provenance` 应区分 `engine_observed`、`seed_reconstructed`、`branch_simulated` 和 `manual_entered`。

 ## 18. 分支、标签与数据泄漏

 完整路线的候选数量会快速增长，不应在输入 seed 时一次性穷举所有未来。采用懒加载和缓存：

 ```text
 cache_key = (checkpoint_hash, action_id, game_context_hash)
 ```

 只在用户点击节点、需要比较选项或教师需要标签时展开分支。分支结果必须保留父 checkpoint、状态 hash、RNG counter 和来源，不能覆盖真实主线。

 人工轨迹属于行为克隆/离线 RL 数据，不天然等于最优标签。应同时保留：

 - 人工实际动作；
 - 未选择但合法的动作；
 - 反事实分支结果；
 - 教师价值和 Top-K；
 - 最终生存、楼层、HP、金币和胜负结果。

 数据切分按 `run_seed`、`episode_id` 或 branch group 进行，同一种子的不同分支不得跨 train/validation/test。seed、未来节点内容、完整 RNG 和实际未来牌序只进入 metadata 或 teacher sidecar。

 ## 19. 对当前计划的关系

 该 GUI 是未来全局数据采集工具，不改变 `PLAN_NOSL.md` 的当前 M3 correctness closeout 和当前战斗模型范围：

 - 战斗模型继续使用 `PublicStateView + NoslBeliefState`；
 - 完整真实快照只作为 `AuditTeacherSnapshot`；
 - raw RNG、未来牌序和 realized outcome 不进入 NOSL 标签；
 - 全局轨迹应使用独立 schema，并通过 `episode_id` 与战斗子轨迹关联；
 - P1 语义、M3 belief/chance/policy/strict-diff 门禁完成前，不生成最终全局 Reliable 数据集。

 全局路线模型可以调用战斗模型提供的胜率、HP 损失和风险估计，但不应把地图、事件和商店动作直接加入当前单回合 CombatPolicy 的动作空间。

 ## 20. 可参考的公开项目

 以下项目对全局系统有参考价值，但都不作为 v0.111 真值来源：

 ### `zhiyue/sts2-rl-agent`

 [GitHub](https://github.com/zhiyue/sts2-rl-agent)

 可参考：全局 `run_env`、Gymnasium 接口、战斗/整局环境分层、真实游戏 Bridge 和训练入口。它适合参考模块边界和 full-run 数据组织，不应直接采用其版本化语义或 observation 数值。

 ### `Zamiell/slay-the-spire-2-emulator`

 [GitHub](https://github.com/Zamiell/slay-the-spire-2-emulator)

 可参考：C# RunState、地图路由、奖励、商店、事件、篝火、RNG 和 Gym wrapper 的整体分层。其 README 明确仍有精确 Neow、商店/奖励/事件概率、遗物覆盖和 trace parity 缺口，应作为架构参考和隔离 oracle。

 ### `ludvig-sandh/sts2-seed-tools`

 [GitHub](https://github.com/ludvig-sandh/sts2-seed-tools)

 可参考：seed 派生多条 RNG 流、分别维护 counter、按事件/遭遇/遗物池消费随机数的思路。它是研究工具，不是当前 v0.111 CLI 的完整实现。

 ### `MufanQiu/sts2-save-rebuild`

 [GitHub](https://github.com/MufanQiu/sts2-save-rebuild)

 可参考：从 seed、地图位置、HP、金币、卡组和遗物恢复运行上下文，以及说明“没有 RNG 状态时，后续商店/奖励/随机事件会进入另一条平行结果”的边界。

 ### `ptrlrd/spire-codex`

 [GitHub](https://github.com/ptrlrd/spire-codex)

 可参考：版本化卡牌、遗物、事件、Ancient、Act、遭遇和事件前置条件索引。它是静态语义数据库，不是 seed replay 引擎。

 ### `HIX4123/sts2-simulator`

 [GitHub](https://github.com/HIX4123/sts2-simulator)

 可参考：地图、事件、商店和 full-run 模块拆分。它是独立 Python 重实现，覆盖和 RNG 不能直接视为真实引擎一致。

 ### `tckmn/sts2-seed-search`

 [GitHub](https://github.com/tckmn/sts2-seed-search)

 可参考：按游戏版本做 seed 搜索和 RNG 派生实验。它的版本范围和功能有限，不作为当前 v0.111 规范。

 ## 21. 全局 GUI 的启动条件与阶段

 启动全局 GUI 实施前应满足：

 - `PLAN_NOSL.md` 的 M3a-M3e correctness closeout 已通过；
 - P1 Power、遗物、卡牌语义和战斗差分门禁稳定；
 - CLI 能输出全局 public/teacher/audit snapshot；
 - 地图节点、事件选项、商店 offer、奖励卡和篝火动作具有稳定 ID；
 - checkpoint 能保存和恢复全部相关 RNG counters；
 - 全局数据 schema、质量等级和 split 策略冻结。

 推荐分阶段实施：

 1. Act 1、单人、固定 v0.111 的只读地图浏览器；
 2. 人工路线/事件/奖励/商店标注和 JSONL 导出；
 3. checkpoint 和单节点反事实分支；
 4. 事件、商店、奖励和篝火状态结构化；
 5. RouteValueModel 和各决策模块独立训练；
 6. GlobalDecisionOrchestrator 联调；
 7. 多 Act、长轨迹和大规模离线 RL。

 第一阶段的验收是“状态和决策可重放”，不是“输入 seed 后显示所有未来内容”。

 ## 22. 使用 `.run` 历史文件提取全局行为数据

 ### 22.1 数据来源与定位

 正式游戏产生的 `.run` 文件可以作为全局决策行为数据的重要来源。该文件是 UTF-8 JSON 格式的历史存档，不是完整的逐帧 replay；它记录一局已经完成的路线和每层的聚合结果。

 `.run` 数据应标记为：

 ```text
 source = game_history_file
 provenance = engine_recorded
 label_source = human_observed
 ```

 它可以补充 CLI 难以大规模获得的真实玩家行为，尤其适合训练路线、事件、商店、篝火和奖励决策模块。

 ### 22.2 提取方法

 对每个 `.run` 文件执行以下步骤：

 1. 以只读方式加载 UTF-8 JSON；
 2. 保存原文件路径、大小、修改时间和 SHA-256；
 3. 读取顶层运行上下文：`build_id`、`seed`、`ascension`、`game_mode`、`acts`、`modifiers`、`start_time`、`run_time`、`win` 和 `was_abandoned`；
 4. 遍历 `map_point_history[act][floor]`，把每个访问过的地图点转换为一条规范化 floor record；
 5. 从 `rooms` 提取 `model_id`、`room_type`、`monster_ids` 和 `turns_taken`；
 6. 从 `player_stats` 提取 HP、金币、伤害、治疗、卡牌、遗物、药水、事件、先古之民和篝火选择；
 7. 保留原始 `player_stats`，同时生成规范化的 `state`、`decisions` 和 `changes` 字段；
 8. 输出 JSONL/JSON 作为完整结构，输出 CSV 作为逐层分析表；
 9. 将卡牌、遗物、事件和 Ancient ID 与对应版本的语义/本地化目录连接；
 10. 将结果按 `run_seed + episode_id` 分组，禁止同一局的楼层记录跨 train/validation/test。

 当前已验证的可复用工具为：

 ```text
 training/extract_run_history.py
 ```

 它接收一个 `.run` 路径，生成：

 ```text
 data/<run>.extracted.json
 data/<run>.floors.csv
 ```

 规范化输出必须包含 `schema_version`、源文件 SHA-256 和提取时间，以便原始存档更新后可以发现数据漂移。

 ### 22.3 `.run` 可以提供的训练字段

 每层通常可以获得：

 - Act 和 floor 顺序；
 - 已访问路线的地图点类型；
 - 房间/遭遇 ID 和敌人 ID；
 - 战斗持续回合数；
 - 当前 HP、最大 HP、受到伤害和治疗量；
 - 当前金币、获得/花费/损失/被窃金币；
 - 卡牌奖励候选及是否被选择；
 - 获得、移除、转化和升级的卡牌；
 - 遗物候选及实际拾取/购买的遗物；
 - 药水候选、使用和丢弃；
 - 普通事件选择；
 - 先古之民候选和被选择的选项；
 - 篝火动作和被升级的卡牌；
 - 最终卡组、遗物、药水和成就徽章。

 这些字段可以直接构成全局决策样本：

 ```text
 state_before_floor
 + node_type / room_id
 + legal_or_offered_choices
 + human_selected_action
 + hp/gold/deck/relic/potion delta
 + next_floor / terminal outcome
 ```

 ### 22.4 对全局模型的帮助

 `.run` 行为数据可用于：

 1. **路线模型**：学习玩家在不同 HP、金币、卡组和遗物条件下选择普通战斗、精英、商店、篝火、宝箱或问号的倾向；
 2. **奖励模型**：学习选择卡牌或跳过奖励的行为先验；
 3. **商店模型**：学习买卡、买遗物、买药水和移除卡牌的决策；
 4. **事件/Ancient 模型**：学习事件选项与先古之民选项选择；
 5. **篝火模型**：学习休息与锻造的选择，以及具体升级卡牌；
 6. **长期价值模型**：使用后续 HP、金币、卡组、楼层和胜负作为 return-to-go 或结果标签；
 7. **行为克隆/离线 RL**：把真实人工选择作为 `human_observed` 行为轨迹，再用 checkpoint 反事实分支或全局教师搜索生成价值差异。

 `.run` 数据不是天然的最优标签。建议同时保存：

 ```text
 human_action
 teacher_best_action（若可生成）
 teacher_top_k
 realized_outcome
 counterfactual_outcome（若已分支推演）
 ```

 ### 22.5 与 CLI/GUI 的组合方式

 推荐的数据汇合流程：

 ```text
 .run 历史文件
     ├── 提取真实访问路线和聚合资源变化
     └── 关联 seed / version / episode
          ↓
 CLI/GUI replay
     ├── 恢复地图拓扑和节点坐标
     ├── 补充稳定 action ID、pre/post state hash
     ├── 捕获完整 public/teacher/audit snapshot
     └── 记录 RNG counters 和逐动作 trace
          ↓
 反事实 checkpoint 分支 + 全局教师价值
          ↓
 route/event/shop/reward/campfire DatasetManifest
 ```

 `.run` 提供真实主线，seed + 同版本运行上下文 + CLI/GUI 补充可见地图结构，影子模拟器/全局搜索补充未选择动作的结果。三者必须使用不同的 `provenance` 标记，不得把推演结果伪装成实际发生结果。

 ### 22.6 已知限制

 `.run` 文件通常不包含：

 - 未访问节点的完整地图图结构和坐标；
 - 每一张战斗中具体打出的卡牌、顺序和目标；
 - 每回合敌人 Intent 变化；
 - 每一步 Power/遗物计数器；
 - 完整 RNG counters 和未来牌堆顺序；
 - 商店精确价格和购买顺序；
 - 每层完整的实时 public/teacher snapshot。

 因此：

 - `.run` 可以作为全局**行为轨迹和结果监督**；
 - CLI Trace 才是逐动作决策的主要来源；
 - `.run` 不能单独重建所有未来内容，也不能替代 NOSL belief 或真实引擎差分；
 - 未记录的字段必须标记为 `unknown`，不能根据 seed 或最终状态猜测并标记为 Reliable。

 ### 22.7 当前示例

 已对正式存档 `1788073020.run` 完成提取，输出位于：

 ```text
 data/1788073020.run.extracted.json
 data/1788073020.run.floors.csv
 data/1788073020.run.analysis.md
 ```

该样本包含 3 个 Act、49 个访问楼层、95 个卡牌奖励候选、15 次卡牌选择、35 条遗物选择记录、8 条事件选择记录和 9 条 Ancient 选择记录，可作为全局数据 schema 和 GUI 标注流程的验证样本，但不代表最终训练集分布。

 ## 23. 方案澄清与锁定决策（SL、`.run` 与战斗模型）

 ### 23.1 SL 的定义

 本计划中的 **SL** 专指玩家在游戏途中通过 Save/Load 反复读取存档，窥探原本不可知的信息，例如未来抽牌顺序、尚未发生的随机结果、未进入节点的奖励或事件结果。

 以下行为不属于 SL：

 ```text
 从历史 `.run` 读取 seed
 → 使用相同版本、角色、Ascension、模式、Modifier 和解锁配置
 → 重建玩家打开地图时本来就能看到的地图拓扑、坐标和节点类型
 ```

 以下行为属于 SL/seed leakage，禁止进入正式 NOSL 策略输入：

 - 读取未来抽牌顺序；
 - 提前查看未进入节点的商店库存或奖励；
 - 提前查看事件的隐藏随机结果；
 - 使用本局真实 RNG 状态直接决定动作标签；
 - 用 Save/Load 试探多个未来结果后把结果当作当时可见信息。

 seed 只能作为离线重建键、复现元数据和数据切分键，不能作为模型特征。

 ### 23.2 NOSL 教师与 SL 审计 Oracle 分离

 教师可以在审计阶段保存完整 RNG、未来牌序和实际随机结果，但正式 NOSL 标签必须遵循：

 ```text
 当前公共观测
 → 构造所有符合观测的隐藏状态/概率分布
 → 对隐藏状态求期望
 → 生成动作价值、风险和策略树
 ```

 不得把某一局真实隐藏状态直接当成唯一条件：

 ```text
 Q(action | public_state, actual_rng, future_order)
 ```

 应将两类产物分开：

 ```text
 NOSL_TeacherLabel：正式训练标签，隐藏信息已边缘化
 SL_AuditOracle：研究上界、差分和调试，不进入训练主损失
 ```

 ### 23.3 `.run` 只负责局外决策行为

 `.run` 数据在全局系统中的定位锁定为：

 ```text
 `.run` → 局外真实行为和结果
 当前 CombatPolicy → 战斗逐动作决策
 ```

 `.run` 用于学习：

 - 地图路线选择；
 - 战斗后卡牌奖励选择/跳过；
 - 商店买卡、买遗物、买药水和移除；
 - 篝火休息/锻造和升级卡牌；
 - 普通事件选项；
 - 先古之民选项；
 - 每层 HP、金币、卡组、遗物和药水变化；
 - 长期楼层进度和胜负结果。

 `.run` 不需要承担以下战斗数据采集：

 - 每张战斗卡牌的出牌顺序；
 - 每个战斗动作的目标；
 - 每回合敌人 Intent 变化；
 - 每个动作后的实时 Power 状态。

 这些内容继续由真实 CLI/Hook Trace 和当前战斗高手模型负责。

 ### 23.4 战斗模型向全局模型提供摘要

 全局模型不直接选择战斗中的卡牌动作。进入战斗节点时，由 CombatPolicy/Expectimax 提供摘要：

 ```text
 expected_win_probability
 expected_hp_loss
 death_probability
 expected_turns
 potion_consumption_probability
 reward/value estimate
 confidence
 ```

 RoutePlanner 使用该摘要比较不同路线，而不是重新展开完整的战斗动作树。战斗教师的完整穷举仍用于战斗模型本身，也可用于生成这些摘要的概率和风险。

 教师穷举得到的是反事实序列和价值，不是玩家实际的出牌记录；实际行为仍以 CLI/Hook trace 为准。

 ### 23.5 两套全局数据集

 锁定以下数据分层：

 ```text
 global_behavior.jsonl
   来源：`.run` + CLI/GUI 当时可见状态
   标签：human_observed
   用途：行为克隆、行为先验、长期结果监督

 global_teacher.jsonl
   来源：checkpoint + NOSL 全局反事实分支/搜索
   标签：nosl_expectimax
   用途：动作价值、Top-K、风险和离线 RL
 ```

 两者必须保留不同的 `label_source` 和 `provenance`，不能把人工选择直接升级为最优标签。

 ### 23.6 地图重建规则

 `.run` 中的 seed 不单独决定地图。地图重建必须绑定：

 ```text
 game_version
 game_commit
 assembly_sha256
 character
 player_count
 ascension
 game_mode
 modifiers
 unlock_profile_hash
 run_seed
 ```

 推荐从 `.run` 提取上下文后启动同版本 CLI 并调用 `get_map`，得到完整地图图结构和坐标，再用 `.run` 的 `map_point_history` 对齐真实访问路线。若重建出的节点类型、遭遇或 Boss 与历史不一致，记录 `map_reconstruction_failed`，不得静默使用。

 ### 23.7 硬件决策

 当前本地 `32GB RAM + 12GB GPU` 继续用于开发、调试、少量数据和模型基线。大规模全局数据生成可租用：

 ```text
 x86_64
 16–32 vCPU
 64–128GB RAM
 200GB–1TB NVMe
 GPU 可无
 ```

 数据生成优先增加 CPU/RAM 和分片吞吐；PyTorch 训练再单独租 24GB 以上 NVIDIA GPU。服务器不改变 NOSL 信息边界，也不能弥补错误语义或低质量教师标签。

 ### 23.8 这一方案与当前战斗主线的关系

 该方案不扩大当前单回合 CombatPolicy 的动作空间：

 ```text
 局外：Route/Event/Shop/Reward/Campfire 模块
 战斗内：现有 CombatPolicy + DeterministicSimulator + Expectimax
 编排：GlobalDecisionOrchestrator 动态重规划
 ```

 全局模型先从 `.run` 行为数据开始，随后加入 CLI 可见状态和 NOSL 反事实教师；战斗逐动作训练继续沿用现有数据管线。两类数据通过 `episode_id`、`floor`、`node_id` 和状态 hash 关联，但不混用动作契约。

数据标注 GUI 的具体实施计划见：[PLAN_GUI.md](./PLAN_GUI.md)。

基线模型完成后的自我对抗式策略迭代构想见：[SELF_PLAY_POLICY_ITERATION_IDEA.md](./SELF_PLAY_POLICY_ITERATION_IDEA.md)。
 
