# `sts2-rl-agent` 只读源码审查：对 STS2SuperModel 的 NOSL 与全局模型价值

> 审查对象：`D:\STS2BestChoice\reference\sts2-rl-agent`  
> 审查基线：`main @ 1b7e7ce35e608722650763938c153ea8bc370333`（提交题目：`Guard CardSelectCmd selector coverage`，2026-05-22）  
> 上游地址：`https://github.com/zhiyue/sts2-rl-agent.git`  
> 审查方式：只读源码、文档、Git 元数据和静态 AST；未修改 `reference/sts2-rl-agent`，未安装依赖，未构建 C# Mod，也未把任何结果当作真实引擎验证。  
> 当前项目基线：STS2SuperModel 绑定 `v0.111.0 / beta / game_commit=41cef1ea / sts2.dll SHA-256=0861...DBE9`，见 `data/global_prototype/global-feature-manifest.json`。

## 1. 一句话结论

这个仓库的主体不是“已经交付、可直接替换当前模型的高手神经网络”，而是一个**纯 Python 顺序式无头游戏重实现 + Gymnasium 环境 + MaskablePPO 训练脚本 + AutoSlay/TCP 实机桥 + Python 自比对重放工具**；它最有价值的部分是大量语义实现、RNG/地图/事件代码和桥接流程设计，最不应直接采用的部分是其压缩观测、位置索引动作、统一全局 PPO、未经版本锁和实机差分晋级的语义，以及未附可重建证据的 92% 胜率/吞吐声明。

对当前项目的正确定位是：

- **NOSL：**把它作为独立语义参考、差分候选、随机场景生成器和 PPO 对照基线；不能作为 `Reliable` 教师，不能替代当前 C# `DeterministicSimulator + Expectimax + ShadowDiff`。
- **全局：**重点借鉴 `RunManager`、地图生成、事件/商店/篝火/奖励状态机和 AutoSlay 页面覆盖清单；不能采用其现有 `RunEnv` 观测/动作设计训练真正的全局策略，因为模型根本看不到候选卡、商店库存、事件选项或可见地图候选特征。

## 2. 证据分级

本文严格区分以下状态：

| 标记 | 含义 |
|---|---|
| **Implemented** | 当前提交的源码中存在可执行实现 |
| **Source-tested** | 有 Python 测试，但测试可能只验证同一 Python 实现或文本结构 |
| **Documented** | README/指南宣称，当前仓库没有足够运行产物支持 |
| **TODO / stale** | 文档明确为 TODO，或文档和当前源码已漂移 |
| **Live-unverified** | C#/真实游戏路径有代码，但没有当前版本的构建与实机闭环证据 |

静态盘点结果：

- `sts2_env/ + scripts/`：124 个 Python 文件；`bridge_mod/`：11 个 C# 文件；合计 135 个源文件。
- `tests/`：115 个 `test_*.py`，静态识别 2,774 个 `test_*` 函数。
- 共静态解析 241 个 Python 文件，AST 错误为 0。
- README 的“133 源文件、14 测试文件、408 tests”是早期统计，已明显过时（`README.md:32-46, 264`）。

## 3. 实际架构

### 3.1 Python 无头模拟器：已实现，但它是顺序执行器，不是搜索引擎

`CombatState` 是一个大型可变战斗对象，包含出牌、抽弃牌、伤害、Power/Relic hook、药水、敌人回合、选择题和多人状态等完整顺序流程。核心入口包括：

- 初始化与回合：`sts2_env/core/combat.py:106, 800, 835, 1506, 1639`
- 合法性与出牌：`sts2_env/core/combat.py:1129, 1186, 1301, 1345`
- 抽牌/洗牌：`sts2_env/core/combat.py:1718-1788`
- 药水：`sts2_env/core/combat.py:1803-1952`
- Power：`sts2_env/core/combat.py:2003-2063`
- 选择题：`sts2_env/core/combat.py:2436-2539`
- Power/Relic/Modifier hook 总线：`sts2_env/core/hooks.py:162-1112`

它具备大量内容实现和测试，但源码中没有战斗状态级 `clone/deepcopy/snapshot/restore/checkpoint` 接口；搜索只命中卡牌实例 `clone()` 和 RNG `fork()`，没有可供 Expectimax/MCTS 低成本分支的完整状态 fork。因此：

- 它适合**从起点向前滚动 episode**；
- 它不适合直接拿来替换当前 `MutableCombatState` 的分支搜索；
- 若用于 Expectimax，需要新增深拷贝/状态所有权/稳定 key/事件队列复制/RNG 快照等一整套能力，而这正是当前 C# 影子模拟器已经在做的事情。

### 3.2 RNG：有参考价值，但不能直接视为当前版本真值

RNG 实现包含：

- 游戏确定性字符串哈希：`sts2_env/core/rng.py:25-36`
- 兼容旧式 `System.Random(int)` 的内部状态：`sts2_env/core/rng.py:39-98`
- seed/name/counter、整数、浮点、洗牌、无放回 sample：`sts2_env/core/rng.py:101-188`
- 运行级命名流：`up_front`、`shuffle`、`unknown_map_point`、`combat_card_generation`、`combat_targets`、`monster_ai`、`rewards`、`shops` 等，见 `sts2_env/run/run_state.py:1370-1397`
- 对命名流 seed 和 C# 随机序列的测试：`tests/test_rng_parity.py:9-81`

这对当前 NOSL 的 `RandomOperatorRegistry`、RNG consumption vector、地图/奖励流划分很有帮助，但只能作为**待验证参考**：

1. 仓库没有绑定 v0.111 的 game version、branch、程序集 SHA。
2. 反编译程序集信息是 `0.1.0+cb602cefd883e8595b7f709863489a88c2a32765`（`decompiled/Properties/AssemblyInfo.cs:77-84`），与当前项目 `game_commit=41cef1ea` 不同。
3. `next_gaussian_int()` 直接消费底层随机数，却不增加公开 `counter`（`sts2_env/core/rng.py:197-209`）；相应地图测试甚至明确期望 Gaussian 后 counter 不变（`tests/test_map_generation_layout_parity.py:211-230`）。因此这里的 `counter` 不是所有内部随机消费的完整可恢复快照，不能直接替代当前的 RNG 状态/消费向量契约。

### 3.3 运行级模拟：实现丰富，是全局 Catalog 的重要参考

`RunManager` 已实现顺序式完整 run 状态机：

- 初始化角色、牌组、遗物和整张地图：`sts2_env/run/run_manager.py:227-295`
- 按 phase 提供动作：`sts2_env/run/run_manager.py:328-355`
- map/combat/reward/boss relic/shop/rest/event/treasure 分发：`sts2_env/run/run_manager.py:339-354`
- 候选动作生成：`sts2_env/run/run_manager.py:853-1188`

`RunState` 从 seed 初始化完整地图和命名 RNG 流：

- 运行初始化：`sts2_env/run/run_state.py:1497-1510`
- 生成完整地图并让牌、遗物、modifier 修改地图：`sts2_env/run/run_state.py:1578-1606`
- 可达下一坐标：`sts2_env/run/run_state.py:1673-1690`
- `?` 节点在进入时用独立流解析：`sts2_env/run/run_state.py:1692-1709`
- 地图生成器返回“所有房间类型已经分配”的完整 ActMap：`sts2_env/map/generator.py:562-580`

这部分非常适合：

- 为当前 `GlobalRunStatePublic`/Catalog 做**字段清单和边界用例**；
- 生成 Route/Reward/Shop/Campfire/Event/Ancient 的 synthetic challenge；
- 用完整 seed 地图作为 `SEED_ORACLE` 审计上界。

但完整地图是 privileged information。当前全局计划要求线上/主教师只读 `visible_map_graph`，因此不能把 `RunState.map` 原样写入公开特征。

## 4. RL 模型、观测、动作和奖励

### 4.1 已实现算法

训练脚本实际只实现一种算法：`sb3-contrib` 的 `MaskablePPO`：

- combat：`scripts/train_combat.py:33-125`
- full run：`scripts/train_full_run.py:62-152`
- 依赖：Gymnasium、NumPy；训练 extra 为 SB3、sb3-contrib、PyTorch（`pyproject.toml:1-20`）
- lockfile 当前解析到 Gymnasium 1.2.3、NumPy 2.4.3、SB3/sb3-contrib 2.7.1、Torch 2.10.0（`uv.lock:330-331, 628-629, 1066-1067, 1096-1097, 1211-1212`）。

没有实现：

- MCTS / Expectimax / Beam teacher；
- public belief / hidden-state marginalization；
- offline RL（IQL/AWR）；
- teacher distillation；
- dataset manifest / JSONL→Parquet；
- Reliable/Estimated/Uncalculable 质量分级；
- ONNX 或当前项目的 C# 模型部署链路。

它默认也**不是 SL/种子预知策略**：combat 与 run policy 的输入向量都没有 seed、原始 RNG 或未来牌序（`sts2_env/gym_env/observation.py:61-127`，`sts2_env/gym_env/run_env.py:681-735`）。训练时每条 rollout 只是在模拟器内部消费一个具体隐藏 seed；若 seed/场景采样分布正确，PPO 可用 Monte-Carlo/TD 在统计意义上逼近公共观测下的期望。因此它可以被定义为 **NOSL-compatible sampled RL baseline**。但它没有显式 belief、Chance 节点、概率质量、隐藏状态不变性测试、所有根动作价值和质量分级，所以不能定义为 Exact/Reliable NOSL 教师。

### 4.2 Combat observation：131 维、信息压缩非常强

`encode_observation()` 固定输出 131 维（`sts2_env/gym_env/observation.py:1-12, 51-58`）：

- 玩家：HP、格挡、能量、最大能量；
- 玩家 Power：只保留 Strength/Dexterity/Vulnerable/Weak/Frail/Artifact 六个；
- 手牌 10 槽，每张只有 `card_id ordinal scalar / cost / base_damage / base_block / is_attack`；
- 牌堆只有 draw/discard/exhaust 数量，另三维固定为 0；
- 最多 5 个敌人，每个只保留 HP/block、首个 intent 和 Vulnerable/Weak/Strength。

具体实现：`sts2_env/gym_env/observation.py:61-127`。

重要缺失：

- 大多数 Power、Power 内部计数器、owner/applier、DynamicVars；
- 遗物、遗物内部状态；
- 手牌 instance ID、升级以外的 enchantment/combat vars/dynamic effects；
- 药水身份（只有动作可用性，没有观测特征）；
- 完整牌堆多重集合；
- 多 intent、敌人 AI 状态、回合历史；
- 不确定性、版本、provenance。

卡牌 ID 还是 Enum 顺序归一化后的**单一连续标量**（`observation.py:22-25, 82`），会向 MLP 注入不存在的“相邻 ID 语义距离”。这不适合当前高手模型，应继续使用结构化字段/embedding/候选条件编码。

### 4.3 Combat action：115 个位置动作，而不是稳定动作 ID

当前源码动作空间是 115：

- 0：EndTurn；
- 1–10：手牌槽位的非目标动作；
- 11–60：`hand_index × enemy_index`；
- 61–114：9 个药水槽 ×（无目标 + 5 敌人目标）。

证据：`sts2_env/core/constants.py:3, 26, 31, 36-43`，编码和 mask 见 `sts2_env/gym_env/action_space.py:26-124`。

这些 ID 只在当前状态/当前进程有效，不能作为跨进程 trace 标签。当前项目的 `PlayCard + card_instance_id + target_id`、`UsePotion + potion_instance_id + target_id`、`Choice`、`EndTurn` 稳定契约应保留。

### 4.4 Combat reward：源码只有稀疏胜负，没有 README 所称 HP penalty

`compute_reward()` 的 `prev_hp` 参数没有被使用；非终局恒为 0，胜利 +1，失败 -1（`sts2_env/gym_env/reward.py:8-15`）。

因此 README 的“HP loss small negative penalty”（`README.md:309-315`）是文档漂移，而不是当前实现。该 reward 最多用于 PPO baseline，不能替代当前 Expectimax scorer 和多目标 risk/value 标签。

### 4.5 Full-run observation：151 维，但完全没有候选语义

`STS2RunEnv` 在 131 维 combat observation 后只加 20 维：act/floor、HP、gold、deck size、relic count、potion count、phase one-hot、ascension、当前房间 elite/boss（`sts2_env/gym_env/run_env.py:29-41, 681-735`）。

它**没有编码**：

- 当前可达地图节点及 node type；
- 卡牌奖励中的候选牌；
- 商店商品/价格；
- 篝火可升级牌；
- 事件/Ancient option 文本或结构化后果；
- Boss relic 候选；
- 完整 deck/relic/potion 结构。

动作 mask 只告诉模型“位置 120/121/… 是否可按”，无法告诉它这些位置分别是什么牌、什么遗物、什么事件选项。由此，单一 full-run PPO 不能真正学习候选条件决策；它最多学习 phase + 槽位位置偏好。这正是当前模块化 `CandidateEncoder + score(state,candidate,offer_set)` 比它合理的根本原因。

### 4.6 Full-run action：157 个统一槽位，文档多处仍写 100

当前源码是 `Discrete(157)`：combat 0–114、map 115–119、card reward 120–126、boss relic 127–129、shop 130–139、rest 140–144、event 145–148、treasure/reroll 149、player-select 150–156（`sts2_env/gym_env/run_env.py:8-27, 110-160`）。

README 仍在架构图和说明中写 `61/100` 或 `Discrete(100)`（`README.md:13-17, 241-246, 302-307`）；Training Guide 也仍写 100（`docs/TRAINING_GUIDE.md:196-201`）。应以源码 115/157 为准。

### 4.7 Full-run reward 与训练入口当前不一致

`STS2RunEnv` 当前实现只有 run win +1、death/timeout -1（`run_env.py:43-49, 223-227, 343-357`），没有 floor/act shaping。

更严重的是 `scripts/train_full_run.py` 当前不可按脚本接口构造环境：

- 脚本每次传 `act_count`、`reward_shaping`（`scripts/train_full_run.py:20-30, 36-57, 155-208`）；
- `STS2RunEnv.__init__` 只接受 `character_id, ascension_level, max_steps, max_combat_turns, render_mode`（`sts2_env/gym_env/run_env.py:255-277`）。

静态签名审计结果：

```text
STS2RunEnv.__init__ params = self, character_id, ascension_level,
                             max_steps, max_combat_turns, render_mode
train_full_run kwargs       = act_count, max_steps, reward_shaping
unsupported                 = act_count, reward_shaping
```

因此 README/Training Guide 所给 `train_full_run.py --act-count ... --reward-shaping` 在当前提交会触发 `TypeError`；“reward shaping 可用”也是 stale claim。`docs/TRAINING_GUIDE.md:167-176` 还明确写着 curriculum model loading 未实现。

### 4.8 训练可重复性不足

combat 的 `make_masked_env(seed)` 接受 seed，却没有在 `_init()` 内 reset/传递 seed（`scripts/train_combat.py:62-67`）；full-run 的同名工厂也没有使用 seed（`scripts/train_full_run.py:36-59`）。两个 `MaskablePPO(...)` 都没有传 model seed（`train_combat.py:78-92`，`train_full_run.py:102-119`）。

这意味着当前脚本没有当前项目要求的固定 seed、配置哈希、重复训练摘要、版本 manifest 和字节复现门禁。

## 5. 真实游戏桥

### 5.1 实际工作方式：AutoSlay + TCP，而不是截图识别

这是有用的设计：

1. Harmony 将 release 检查改为非 release，从而开启游戏内 AutoSlay；同时尝试缩短等待/动画（`bridge_mod/MainFile.cs:3-5, 28-78, 125-170`）。
2. `RlAutoSlayer` 复用游戏内 AutoSlay 的主菜单、房间循环和 overlay draining，替换主要选择 handler（`bridge_mod/RlAutoSlayer.cs:46-52, 111-145, 213-339`）。
3. C# 本地 TCP server 使用 newline-delimited JSON（`bridge_mod/BridgeServer.cs:1-10`）。
4. 每个状态附递增 `request_id`，服务端丢弃陈旧响应（`BridgeServer.cs:125-158, 315-379`）。
5. Python runner 在 combat 调 MaskablePPO，非 combat 调硬编码 heuristic（`sts2_env/bridge/agent_runner.py:192-299, 311-471`）。

这证明全局数据采集/执行完全可以不依赖截图 OCR；它可以作为当前 CLI 之外的第二条真实 UI/AutoSlay 采集思路。

### 5.2 Bridge 不是当前项目所需的完整状态契约

Combat bridge 只发：玩家 HP/block/energy、Power 的 ID+amount、手牌浅字段、敌人浅字段、药水、三个牌堆计数、round/floor/act（`bridge_mod/RlCombatHandler.cs:416-496`）。

缺失：

- card instance ID、Power owner/applier/internal vars、Relic state；
- 完整合法 action candidate；
- RNG stream/counter/snapshot；
- public/teacher provenance、schema 和版本；
- trace pre/post hash。

因此不能替代当前 `LiveCombatSnapshotAdapter`/CLI Trace/ShadowDiff 契约。

### 5.3 Bridge 观测存在一个明确的训练-实机错位

C# `SerializeCard()` 只发 `id/cost/type/target/playable/upgraded`（`bridge_mod/RlCombatHandler.cs:505-531`），不发 `base_damage` 或 `base_block`；Python adapter 却读取这两个字段并把缺失值当 0（`sts2_env/bridge/state_adapter.py:148-164`）。模拟器训练时这两维通常非零（`sts2_env/gym_env/observation.py:78-87`）。

静态检查结果：

```text
adapter reads base_damage/base_block = true/true
C# SerializeCard writes them          = false/false
```

所以 README 所称“exact same encoding”并不成立；至少手牌伤害/格挡段存在明确 distribution shift。

### 5.4 Bridge action mask 重新猜合法性，而不是采用真实合法性

C# 已序列化 `playable = card.CanPlay(...)`（`RlCombatHandler.cs:517-526`），但 Python `StateAdapter.compute_action_mask()`完全不读取 `card["playable"]`，而是用 cost、energy、target type 自己推断（`state_adapter.py:246-282`）。这会漏掉 Normality、Power 限制、特殊卡牌自定义合法性等真实 `CanPlay` 规则。

实际 C# 执行时又会再次调用 `card.CanPlay()` 并拒绝错误动作（`RlCombatHandler.cs:175-200`）。这不是训练用稳定合法集；当前项目“合法性由真实输入提供，模型不自行推断”的规则必须保留。

### 5.5 非战斗 bridge payload 过浅，文档所说 heuristic 实际拿不到关键状态

- Map handler 只发送下一节点的 index/type/row/col、floor、act，没有 HP/deck/gold（`bridge_mod/RlMapHandler.cs:82-102`）。因此 Python 的低 HP 路线逻辑通常读不到 HP，只会走 healthy 分支（`agent_runner.py:338-353, 514-539`）。
- Rest handler 先过滤掉 disabled option，只发 enabled options + floor/act，没有 HP 或 deck（`RlNonCombatRoomHandlers.cs:82-99, 123-169`）。Python 因读不到 HP，通常优先 smith，而不是文档声称的低 HP rest。
- Card reward 只发候选牌和 `can_skip`，没有 deck/current run state（`RlCardRewardScreenHandler.cs:52-77`）。Python 的“deck > 30 时 skip”条件无法成立（`agent_runner.py:367-379, 505-511`）。
- Shop 只发已经 `IsStocked && EnoughGold` 的槽位（`RlNonCombatRoomHandlers.cs:424-434`），所以无法保留未买得起的完整候选和 legal mask；多个卡牌商品的 `id` 都只是 `buy_card`（`RlNonCombatRoomHandlers.cs:533-570`），不是稳定 offer ID。
- Event 在发送前过滤 locked 选项（`RlNonCombatRoomHandlers.cs:651-659`），并把所有 option 的 `id` 都写成 `event_choice`（`RlNonCombatRoomHandlers.cs:703-720`），没有稳定 `event:{event_id}:option:{option_id}`。

这些恰好反证了当前全局契约的必要性：完整候选集合、显式 legal、稳定 ID、结构化 run state、`Skip/Leave/Proceed/Cancel` 作为普通候选都不能退回到位置索引协议。

### 5.6 Bridge runtime 尚未被仓库自己证明

仓库自己的 `PARITY_GAPS.md` 明确写明：

- exact parity 尚未覆盖广泛卡/遗物交互和 live-game bridge smoke（`docs/PARITY_GAPS.md:211-223`）；
- 当前 bridge 是 AutoSlay 自动路径，不等于正常手动 UI 全流程（`PARITY_GAPS.md:229-247`）；
- 当时 C# Mod 未能在本机编译，直到构建和 live smoke 前只能算 implemented/Python-tested，不算 field-verified（`PARITY_GAPS.md:249-254`）。

`tests/test_bridge_autoslay_coverage.py` 的核心测试是读取 C# 源码字符串并断言 handler 名称存在（例如 `tests/test_bridge_autoslay_coverage.py:44-95`），不是编译或启动游戏。

## 6. Replay/差分工具：设计值得借鉴，证据强度不足

`sts2_env/parity/bridge_replay.py` 实现了：

- `initial_state + action/resulting_state[]` 的 trace（`bridge_replay.py:101-142`）；
- 包装 client 自动录制（`bridge_replay.py:153-219`）；
- combat、map、reward、bundle、rest、shop、event、treasure、boss relic 等状态归一化（`bridge_replay.py:328-418`）；
- Python CombatState/RunManager 到 bridge shape 的序列化（`bridge_replay.py:440-612`）；
- replay action 后逐字段比较，遇到首个错误停止（`bridge_replay.py:628-740, 820-864`）。

对当前项目的价值：

- `request_id` + state/action/state 关联；
- phase-aware normalizer；
- trace recorder 包装器；
- exact path diff 文本。

但它不能替代当前 ShadowDiff：

- normalized Power 只有 ID+amount，没有 owner/applier/internal counters/DynamicVars（`bridge_replay.py:232-240`）；
- combat snapshot 不含 Relic、RNG、稳定 instance ID、牌堆内容，只含计数（`bridge_replay.py:328-349`）；
- noncombat option normalizer丢掉 id/label/description，只比较 index/action/enabled（`bridge_replay.py:305-313, 397-410`）；
- `_diff_values()` 对 dict 只遍历 `expected` 的键，不报告 `actual` 多出的键（`bridge_replay.py:628-638`），实质是 expected-subset comparison；它适合向后兼容的浅比较，却不满足当前 ShadowDiff “状态契约字段全集一致”的严格门禁；
- 回放需要人工提供 `scenario_factory`，不能从真实 root snapshot 通用重建（`bridge_replay.py:698-715, 820-837`）；
- 现有主要 tests 的 expected trace 和 actual 都由同一个 Python simulator 生成，例如简单 combat 在 `tests/test_bridge_replay_harness.py:417-435`，并不是实机 golden trace。

所以可借鉴 recorder/normalizer 结构，但当前 `pre_state_hash/post_state_hash + stable action ID + Power/Relic/RNG + version + confidence` 的 ShadowDiff 范围更正确。

## 7. 文档声明与源码现实

| 项目 | 文档声明 | 源码/仓库证据 | 判定 |
|---|---|---|---|
| 内容覆盖 | 577 cards、260 powers、121 monsters、290 relics、63 potions 全 100%（`README.md:278-290`） | 大量实现和 2,774 个测试存在；仓库自己仍说 exact parity 未覆盖（`PARITY_GAPS.md:211-223`） | **Implemented broadly；不等于 exact** |
| Combat PPO 92% | `README.md:45-46`、`TRAINING_GUIDE.md:80-88` | 仓库无 `.zip/.pt/.pth/.onnx` 模型、无 TensorBoard log、无数据 manifest、无逐 seed eval 结果 | **Documented，当前不可重建** |
| 1,200 combats/s、28k steps/s | `README.md:45, 73-89` | 有随机 action benchmark 脚本（`scripts/benchmark.py:8-47`），但无保存的机器信息/原始输出/commit+deps manifest | **Documented benchmark claim** |
| HP loss reward | `README.md:309-315` | `compute_reward` 只返回终局 ±1（`reward.py:8-15`） | **Stale** |
| Full-run shaping | `KNOWN_ISSUES.md:85-97`、`TRAINING_GUIDE.md:154-160` | RunEnv 没有 shaping；构造器不接受 `reward_shaping` | **Stale/broken** |
| Full-run PPO | README 给出可执行命令 | `train_full_run.py` 与 `STS2RunEnv.__init__` 参数不兼容 | **入口当前 broken** |
| Full-run 0% | `KNOWN_ISSUES.md:89-97`、`TRAINING_GUIDE.md:180-201` | 无日志/模型；但架构缺候选特征和稀疏 reward，结论与源码一致 | **Documented, plausible, not reproducible here** |
| Bridge 可连接真实游戏 | 有完整 C#/Python 代码 | 仓库自己说明未构建/未 full live smoke（`PARITY_GAPS.md:229-254`） | **Live-unverified** |
| 版本兼容 | 未明确 | csproj 直接引用本机当前 `sts2.dll`，BaseLib/Analyzer 使用 `Version="*"`（`bridge_mod/STS2BridgeMod.csproj:67-82`）；无程序集 SHA gate | **缺失** |

## 8. 对当前 NOSL 模型的具体帮助

### 8.1 高价值、可立即只读利用

1. **P1 语义交叉参考**  
   对当前 mismatch/Uncalculable 卡、Power、Relic，可定位本仓库对应实现和 decompiled-backed test，生成“第三方实现假设”。它只能帮助发现缺口，最终仍必须通过当前 v0.111 CLI↔ShadowDiff。

2. **RNG 流和边界审计清单**  
   将 `RunRngSet` 的命名流与当前 `RandomOperatorRegistry` 做离线集合 diff，特别检查 `up_front/unknown_map_point/niche/rewards/shops/transformations/treasure_room`。不要直接复制 sequence/counter，实现必须在当前程序集上验证。

3. **复杂测试场景来源**  
   2,774 个 test 函数覆盖选择题、Power hook、Relic、事件、多人 owner semantics、地图、奖励等，可抽取最小场景名称和断言，转换为当前 CLI fixture/ShadowDiff challenge。

4. **Bridge replay 结构**  
   借鉴 `request_id` 和 state-action-state recorder，为当前 trace 增加 stale-action 防护和 phase-aware error path；不要采用其浅 normalizer。

5. **独立差分实现**  
   对同一简单 fixture，可增加“CLI vs 当前 C# Shadow vs Python sts2_env”三方比较。Python 分支只能标记 `ExternalReference`，它与 CLI 一致时提高排查信心，与两者不一致时帮助定位，但不改变 Reliable。

6. **PPO baseline**  
   在当前 public feature + stable candidate mask 上训练一个小 MaskablePPO/在线 rollout baseline，作为监督/Expectimax 模型的挑战对照；不要用它产生教师标签。

### 8.2 不应采用

- 不要把 131 维 ordinal-card observation 换进当前模型；
- 不要把 hand index/target index 当训练主键；
- 不要让 Python adapter自己推断合法性；
- 不要用 Python模拟胜利当 `Reliable` ground truth；
- 不要把 simulator 内部 seed/完整牌序作为线上输入；
- 不要用统一 PPO 取代 NOSL Expectimax teacher；
- 不要在缺少状态 fork/key/RNG snapshot 时把它改成搜索器。

## 9. 对当前全局模型的具体帮助

### 9.1 高价值部分

1. **Map/Run synthetic generator**  
   `RunState.generate_map()`、`get_available_next_coords()`、`resolve_room_type()` 可以产生地图拓扑、可达分支和 `?` 分布挑战，支持当前 RoutePlanner 的动态重规划测试。公开数据只能导出当时可见图；完整 seed map 放 audit sidecar。

2. **Catalog 与候选类型覆盖**  
   `RunManager.get_available_actions()` 已覆盖 map/combat/card reward/boss relic/shop/rest/event/treasure，可作为 GlobalActionCandidate 的类型盘点和缺口检查。

3. **全局语义回归场景**  
   shop price、reward generation、rest options、event tree、map modifiers、Relic hook 的源码和测试，可以用来生成 synthetic scenario 和 Unknown/Uncalculable 挑战。

4. **真实 UI 自动化清单**  
   AutoSlay handler 已涉及 map、reward screen、card reward、bundle、Crystal Sphere、rest、shop、event、treasure、boss relic、nested card select，可作为未来 GUI/CLI global protocol 的页面覆盖 checklist。

5. **为什么模块化是正确方向的反例**  
   本仓库单一 157-action PPO 把所有 phase 放进一个策略，但不编码候选语义，文档报告 1M steps 后 run win 仍 0%。当前 `RewardPolicy/RoutePlanner/ShopPolicy/CampfirePolicy/EventPolicy/AncientPolicy + Orchestrator` 应保持模块化。

### 9.2 需要转换后才能使用

- `RunManager._actions_shop()` 只返回买得起的商品（`run_manager.py:1047-1122`），当前系统必须改为完整 offers + legal mask。
- `RunManager._actions_rest_site()` 和 `_actions_event()`过滤 disabled 候选（`run_manager.py:1124-1182`），当前系统必须保留非法候选及原因。
- map action 是临时 coord/index，不是 `route:map:{act}:{row}:{col}`；reward/shop/event 也缺 offer snapshot hash 和 stable ID。
- Python RunEnv observation 只可用于性能/随机 baseline，不可作为当前 GlobalPolicy 输入。

## 10. 版本、证据和许可风险

### 10.1 版本不兼容是硬门禁

当前参考仓库：

- decompiled assembly commit：`cb602cefd883...`；
- 无 `game_version/game_branch/assembly_sha256/semantic_catalog_version`；
- bridge csproj 引用安装目录里的任意当前 `sts2.dll`，依赖版本还使用 `*`。

当前项目：

- `v0.111.0 / beta / 41cef1ea / assembly SHA 0861...DBE9`；
- exact-match compatibility policy。

因此任何卡/Power/Relic/RNG/地图实现都必须先标记 `ExternalReference: sts2-rl-agent@1b7e7ce / decompiled cb602ce`，经过当前版本真实差分后才能进入语义 Catalog。

### 10.2 测试很多，但多数不是真实引擎证据

测试价值很高，但证据等级要拆开：

- direct decompiled-backed unit assertion：可作为强假设；
- Python simulator 自己生成 expected 再 replay 自己：只证明内部一致；
- C# 源码文本 guard：只证明 handler wiring 字符串存在；
- live-game CLI/bridge diff：仓库当前没有完整证据。

### 10.3 没有明确开源许可证

仓库没有 tracked `LICENSE/COPYING/NOTICE`。README 只写“for research and educational purposes”（`README.md:339-341`），这不是标准开源授权条款；同时 `decompiled/` 是游戏程序集反编译源码。

结论：

- 可以研究架构、写独立实现、记录行为假设；
- 在没有单独确认授权前，不应直接复制 Python/C# 代码进当前 GitHub 仓库；
- 若将任何实现迁移，必须做 clean-room 重写并保存来源/NOTICE/差异证据。

## 11. 建议的非冲突落地顺序

这些工作可与当前 NOSL/P1 并行，不修改核心语义：

### A. 外部语义索引（优先级高）

只新增报告生成器：把当前 Unknown/mismatch ID 映射到 `sts2-rl-agent` 实现文件和对应 tests，输出：

```text
model_id
reference_commit
reference_source_path
reference_test_paths
current_v0111_status
verification_required=true
```

不导入实现，不改变 Reliable。

### B. RNG stream 对照审计（优先级高）

将本仓库命名流、当前 CLI 暴露流和 `RandomOperatorRegistry` 做集合/调用点 diff；输出缺失流和需实机验证项。尤其不要把 Gaussian 的 counter 语义当通用恢复依据。

### C. Global challenge 生成器（优先级中）

用该仓库 map/run 代码离线生成：

- 同一 visible graph 对应不同隐藏完整地图；
- 高/低 HP、金币、deck health 的 route/shop/rest 候选；
- unknown/event/Ancient/nested choice 场景。

导出时经过当前 `GlobalRunStatePublic` allowlist，完整 map/seed 只写 audit sidecar，标签固定 `ExternalReference` 或 `EstimatedByHeuristic`。

### D. 三方 ShadowDiff 诊断（优先级中）

对少量固定场景运行：

```text
real CLI v0.111
vs current C# shadow
vs sts2-rl-agent Python simulator
```

唯一晋级条件仍是 real CLI ↔ current shadow；第三方结果只做定位。

### E. PPO 对照实验（优先级低）

等当前 Reliable 数据和 P1 稳定后，再用当前 schema/stable candidates 实现 MaskablePPO baseline。评估应与 Expectimax/监督模型在相同 challenge set 比较 regret、死亡风险和 mask violation，而不是复用本仓库 131/151 维编码。

## 12. 最终判断矩阵

| 模块 | 对 NOSL | 对全局 | 采用方式 | 证据上限 |
|---|---:|---:|---|---|
| Python CombatState/内容语义 | 高 | 中 | 外部交叉参考、fixture 来源 | 未经当前 CLI 验证不得 Reliable |
| RNG/命名流 | 高 | 高 | 调用点审计、当前版本重验 | ExternalReference |
| Map generator/RunManager | 低 | 高 | synthetic/challenge/privileged oracle | 不进入 Public future fields |
| Gym CombatEnv | 中 | 低 | PPO baseline 思路 | 不能当 teacher |
| Gym RunEnv | 低 | 低 | 反例/随机 baseline | 当前训练入口 broken、候选特征缺失 |
| MaskablePPO 脚本 | 中 | 低 | 对照实验框架 | 无可重建模型/结果 |
| C# AutoSlay bridge | 中 | 高 | 页面覆盖、无需 OCR、request correlation | 尚未 field-verified |
| Bridge state adapter | 低 | 低 | 仅参考协议 | 已发现观测/mask 错位 |
| Bridge replay harness | 高 | 高 | recorder/diff 结构 | 比当前 ShadowDiff 浅 |
| README 的 92%/100% | 无 | 无 | 仅背景 | 文档声明，不是训练证据 |

## 13. 结论

最值得吸收的不是“把它的 PPO 模型拿来用”，因为仓库里没有可验证模型；而是：

1. 用其大规模 Python 语义和测试加速 P1 缺口定位；
2. 用其 RNG/地图/RunManager 作为当前版本差分的外部参考；
3. 用其 AutoSlay bridge 证明全局状态/动作可以从真实 UI/引擎采集，无需截图；
4. 用其 replay recorder 和 request correlation 改进 trace 工程性；
5. 把它的 full-run 0% 和缺候选特征问题当成证据，继续坚持当前“两脑分层 + 模块化候选排序 + NOSL Expectimax 教师 + Reliable 门禁”。

不应改变当前路线：真实 CLI 是事实源，C# 影子模拟器是教师执行器，Expectimax 对公共观测下隐藏分布求期望，PyTorch 模型负责蒸馏/排序，全局层只消费版本化 CombatSummary；任何来自 `sts2-rl-agent` 的语义都先是 `ExternalReference`，通过 v0.111 实机差分后再晋级。
