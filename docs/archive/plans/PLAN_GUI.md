# STS2 全局决策人工数据标注 GUI 设计计划

更新时间：2026-08-31
状态：GUI 计划范围已完成（G0–G6b）；外部影子模拟器概率实现属于 Core/Simulator 仓库，不在本目录实现
关联 Idea：GLOBAL_DECISION_ARCHITECTURE_IDEA.md
关联主计划：PLAN_NOSL.md

当前实现目录：`sts2-seed-gui/`（`app.py`、`models.py`、`cli_session.py`、`storage.py`、`static/`）。
地图交互增量已实现：创建会话默认启动 CLI 并自动读取首个 Act 地图；地图使用 SVG 节点/连线；左键通过服务端路线门禁记录节点；右键面板录入事件、奖励、商店、篝火、涅奥和战斗摘要；路线状态持久化并支持羽翼之靴 3 次“仅下一层、不跨 Act、不回头”跳转。
来源 ID 用于标识被标注的局/批次（例如 `.run` 文件名或 `seed+局号`）；标注者 ID 用于标识实际录入或核对者（例如用户名或 agent ID）。两者只记录 provenance，不参与游戏状态计算。
最新 GUI 交互改为节点旁结构化填写窗：左键只负责高亮，右键打开填写窗；生命和金币使用正负数记录变化；卡牌、遗物、药水分别提供“失去/获得”多行输入。失去项从当前已知资源生成可搜索下拉，获得项从 `0.111.0` 版本目录生成可搜索下拉，并允许手动输入未知对象。完整卡牌目录 607 条、遗物目录 299 条、药水目录 69 条通过 `/api/catalogs` 提供。
地图显示层补充了路线完成色、先古之民起点和首领节点，并把 CLI 的 `RestSite`/`Unknown` 映射为“篝火”/“问号”，避免界面出现无意义的“未知节点”。
路线状态的初始当前位置统一绑定到合成的先古之民第 0 层；第一层节点作为其合法子节点，避免新会话第一次选择被错误判定为不可达。
已实现：seed/RunContext、CLI 地图与 public state、人工决策录入、`.run` 导入与基础地图对齐、JSONL+Manifest 导出、Reliable 过滤导出、checkpoint/branch 元数据、会话级基础校验、public 状态差异接口、pre/post state hash 自动补全、按 run/episode/seed/branch 分组切分。
外部依赖：运行环境中的 Parquet 依赖安装、影子模拟器具体概率实现；GUI 已提供适配协议和严格概率门禁。

## 0. 已锁定的设计结论

1. GUI 只提供状态浏览和人工录入，不处理外部媒体或自动动作识别。
2. 用户根据 CLI、`.run` 或其他外部对局资料，在 GUI 中手动确认局外决策。
3. seed 只用于重建固定上下文下玩家可见的地图，不作为模型输入，也不用于显示不可见的未来随机结果。
4. CLI 负责提供地图、当前 public state 和当时可见的合法/可选动作；用户只填写实际选择。
5. .run 文件是可选的行为和结果来源，不要求从中恢复战斗逐卡牌动作。
6. 战斗中的出牌顺序、目标和 Power 变化由现有 CombatPolicy、CLI Trace 和教师系统负责；全局 GUI 只接收可选的 CombatSummary。
7. 人工选择标记为行为数据，不自动标记为最优教师标签。
8. 所有记录都必须保留版本、来源、状态完整度、SL 状态和可复核定位信息。

## 1. 目标与非目标

### 1.1 目标

构建一个本地运行的人工录入工具，将人工整理的局外决策转换为可重建的结构化轨迹：

~~~
seed + RunContext + CLI public state
    → 人工选择路线/奖励/商店/篝火/事件动作
    → 自动记录状态、选项、结果和 provenance
    → global_behavior.jsonl / Parquet
~~~

支持的决策类型：

- 地图节点选择；
- 战斗后卡牌奖励选择或跳过；
- 商店买卡、买遗物、买药水和移除；
- 篝火休息、锻造和升级卡牌；
- 普通事件选项；
- 先古之民选项。

### 1.2 非目标

- 不导入外部媒体文件；
- 不在首版录入战斗逐卡牌动作；
- 不在首版预测所有未访问节点的隐藏内容；
- 不把 GUI 做成实时游戏控制器；
- 不把人工选择直接当成 NOSL 最优标签；
- 不把 Save/Load 探索到的未来结果写入 public state。

## 2. 数据来源和职责

| 来源 | 作用 | 默认标记 |
|---|---|---|
| 人工整理记录 | 提供已确认的局外选择 | human_observed |
| .run 文件 | 提供 seed、已访问路线和每层聚合结果 | engine_recorded |
| seed + 固定 RunContext | 重建地图和可复现入口 | seed_reconstructed |
| CLI | 提供地图、public state、可见选项和结果 | cli_observed |
| 当前战斗模型 | 提供战斗胜率/HP 损失等摘要 | model_estimated |
| 全局反事实教师 | 评估未选择动作 | counterfactual_branch |

同一条记录可以有多个来源，但必须分别记录 state_source、action_source 和 outcome_source，不能将推演结果伪装成实际发生的结果。

## 3. 用户工作流

### 3.1 创建标注会话

用户输入或选择：

~~~
标注批次或来源标识
外部对局记录引用（可选）
标注备注规则
游戏版本
角色
玩家人数
Ascension
游戏模式和 Modifier
解锁配置/Profile hash
seed
对应 .run 文件（可选）
~~~

系统生成 run_context_hash，启动固定版本 CLI。

### 3.2 建立地图

~~~
start_run(character, ascension, seed)
→ get_map
→ 显示完整可见地图
→ 人工确认已记录的当前节点和路线
~~~

地图视图显示：

- Act、楼层、节点坐标；
- 节点类型和连接关系；
- 当前节点、已访问节点和已记录路线；
- Boss；
- 节点信息来源和可信度；
- 重建不一致的节点。

### 3.3 标注决策点

在决策点暂停外部记录，GUI 自动或通过 CLI 填充：

~~~
当前 public state
当前节点和可达动作
可见选项
~~~

用户手动确认：

~~~
人工确认的 action
可选的外部定位说明
可选备注或理由
~~~

### 3.4 推进到下一节点

提交动作后，GUI：

1. 保存 pre_state_hash；
2. 通过 CLI 执行或重放动作；
3. 获取 post state 和资源变化；
4. 保存 checkpoint；
5. 更新地图当前位置；
6. 进入下一个决策点。

如果没有战斗逐动作记录，使用“战斗摘要跳转”或 .run 的楼层结果推进，并明确标记状态为 Estimated，不声称逐步 replay 完整一致。

### 3.5 导出

完成一局或一个片段后，用户执行：

~~~
Validate → Review unresolved → Export JSONL → Build Parquet/Manifest
~~~

## 4. 三种状态推进模式

### 4.1 Exact Replay

适用于存在完整 checkpoint、CLI Trace 或可重放战斗动作的对局：

~~~
seed + 全部历史动作
→ CLI 逐步重放
→ 每个 public state 与 hash 校验
~~~

### 4.2 Combat Summary Jump

适用于只有局外决策记录、没有战斗逐动作记录的情况：

~~~
进入战斗
→ CombatPolicy/CLI 得到 CombatSummary
→ 使用 .run 或已观察结果填充战斗后状态
→ 跳到下一局外决策点
~~~

只把以下摘要交给全局模型：

~~~
expected_win_probability
expected_hp_loss
death_probability
expected_turns
potion_consumption
confidence
~~~

### 4.3 Decision Snapshot

当无法可靠重放前置战斗时，允许单独标注某个决策点：

~~~
seed/context（若有）
+ 当时可见地图/选项
+ CLI 或人工锚定的 public state
+ 专家选择
~~~

该记录不强行连接到完整 episode，标记 partial_episode=true，可用于行为克隆辅助数据。

## 5. GUI 页面设计

### 5.1 会话列表

显示：

- session ID；
- 来源引用；
- seed 和 RunContext；
- 已标注 Act/floor 数；
- Exact/Estimated/Uncalculable 统计；
- 未解决 mismatch 数；
- 最后修改时间；
- 导出状态。

### 5.2 RunContext 页面

提供固定字段编辑和版本锁定。修改角色、Ascension、版本或 seed 后，必须重新生成 run_context_hash，旧轨迹不能静默复用。

### 5.3 地图页面

功能：

- SVG/Canvas 绘制节点和连线；
- 点击节点查看节点类型、坐标和来源；
- 高亮已记录路线；
- 显示当前可达节点；
- 显示已访问、未访问和待确认状态；
- 显示 map replay mismatch；
- 支持从 checkpoint 建立只读分支。

### 5.4 决策录入页面

通用字段：

~~~
decision_type
act / floor
node_id / node_coord
public_state_before
legal_actions / offered_options
selected_action
source_note
confidence
~~~

不同决策类型的动作：

~~~
route: map:act1:row7:col3
reward: pick/skip + card_instance_or_model_id
shop: buy/remove/leave + offer_id
campfire: rest/smith + card_instance_id
event: EVENT_ID + option_id
ancient: ANCIENT_ID + option_id
~~~

### 5.5 状态差异页面

对比：

- HP/max HP；
- 金币；
- 卡组增删、转化和升级；
- 遗物；
- 药水；
- 当前节点；
- 事件、奖励和商店结果；
- pre/post hash；
- CLI 与 .run 的差异。

### 5.6 审查和导出页面

按以下条件筛选：

- 缺失 seed/context；
- map replay mismatch；
- 缺失合法动作集合；
- 手动覆盖字段；
- sl_status=unknown；
- label_quality=Estimated；
- 同一 seed 重复记录；
- 未解决状态字段。

## 6. 后端架构

推荐使用本地 Python 服务，不做 Electron 和云端账号系统：

~~~
GUI static frontend
        ↓ HTTP/WebSocket
Local Annotation API
        ├── RunContextStore
        ├── CliProcessManager
        ├── MapReplayService
        ├── DecisionRecorder
        ├── CheckpointStore
        ├── ValidationService
        └── DatasetExporter
~~~

### 6.1 推荐目录

~~~
training/global_annotation_gui/
  app.py
  cli_session.py
  run_context.py
  replay.py
  decisions.py
  checkpoints.py
  validation.py
  export.py
  schemas/
  static/
  tests/
~~~

### 6.2 首版存储

- SQLite：会话、草稿、索引、未解决问题；
- JSONL：不可变原始标注记录；
- JSON sidecar：public、teacher、audit 状态引用；
- Parquet：训练数据；
- Manifest：版本、数量、哈希和质量统计。

不存储外部媒体本体，只保存来源 ID、外部记录路径和可选校验信息。

## 7. CLI 适配层

复用当前 v0.111 CLI：

~~~
start_run
get_map
action/select_map_node
enter_room
get_combat_snapshot
write_continue_save
load_save
~~~

首版适配器应提供：

~~~python
start_run(context) -> RunHandle
get_map(run) -> MapSnapshot
get_public_state(run) -> PublicRunState
get_decision_options(run) -> LegalActionSet
execute(run, action) -> TransitionRecord
save_checkpoint(run) -> CheckpointRef
restore_checkpoint(ref) -> RunHandle
~~~

后续 CLI 接口需求：

- get_run_context；
- get_map_node_state；
- get_run_snapshot(view=public|teacher|audit)；
- 完整全局 checkpoint；
- 稳定的 event/shop/reward/campfire action ID；
- 非战斗节点的 pre/post hash。

外部概率提供器协议：

```text
request:  {"cmd":"enumerate_outcomes", "state":{}, "action":{}, "depth":N}
response: {"type":"outcomes", "outcomes":[{"probability":0.7,"next_node":{...}}]}
```

提供器负责影子模拟器或语义数据库的随机分支，GUI 只校验概率和、构造树并调用 Expectimax。

## 8. 数据契约

### 8.1 RunContext

~~~json
{
  "schema_version": "global-run-context-v1",
  "game_version": "v0.111.0",
  "game_commit": "41cef1ea",
  "assembly_sha256": "...",
  "cli_protocol_version": "0.2.0",
  "character": "SILENT",
  "player_count": 1,
  "ascension": 10,
  "game_mode": "standard",
  "modifiers": [],
  "unlock_profile_hash": "...",
  "run_seed": "ZWVCV8EKV8RG",
  "run_context_hash": "..."
}
~~~

### 8.2 GlobalDecisionRecord

~~~
record_id
schema_version
run_context_hash
run_seed
episode_id
branch_id
parent_checkpoint_id

source_type
source_id
annotator_id
sl_status

act
floor
node_id
node_coord
node_type
decision_type

public_state_before
public_state_hash_before
legal_actions
selected_action
action_source

public_state_after
public_state_hash_after
hp_before / hp_after
gold_before / gold_after
deck_diff
relic_diff
potion_diff
next_node
realized_outcome
combat_summary

map_source
state_source
outcome_source
provenance
confidence
label_quality
manual_override_fields
notes
~~~

### 8.3 来源和质量枚举

~~~
provenance:
  engine_recorded
  cli_observed
  seed_reconstructed
  manual_annotation
  counterfactual_branch
  manually_constructed

label_quality:
  ExactPublic
  ObservedPartial
  Estimated
  Synthetic
  Uncalculable
~~~

只有 ExactPublic 且没有未来信息泄漏的记录，才可进入高质量行为主集；Estimated 和 Synthetic 只能按权重进入辅助集。

## 9. .run 导入和对齐

GUI 调用现有：

~~~
training/extract_run_history.py
~~~

读取 .run 后：

1. 保存原始文件 SHA-256；
2. 导入 seed、版本、角色、Ascension、Act 和 Modifier；
3. 导入 map_point_history；
4. 导入每层 room、encounter、HP、金币和选择；
5. 用同上下文 CLI get_map 生成完整地图；
6. 用 floor 顺序、节点类型、房间 ID 和 Boss 对齐路线；
7. 对齐失败时保留记录并标记 map_reconstruction_failed；
8. 不把 .run 的聚合变化伪装成逐动作状态。

.run 只提供已发生的主线行为；未访问节点和未发生随机结果不从 .run 自动填充到 public view。

## 10. NOSL/SL 信息边界

### 10.1 默认 Player View

可显示：

- 玩家当时可见的完整地图；
- 当前节点和可达节点；
- 当前事件、奖励、商店和篝火选项；
- 当前 HP、金币、卡组、遗物和药水；
- 当前战斗摘要。

### 10.2 Teacher/Audit View

可以保存但不显示给人工决策输入：

- 原始 RNG 状态；
- 未来牌序；
- 未访问节点的隐藏内容；
- 反事实分支结果；
- 完整 teacher snapshot。

GUI 必须在代码层把 public 和 teacher/audit 字段分开，不能只依靠界面隐藏来保证隔离。

## 11. 数据导出和训练衔接

### 11.1 行为数据

~~~
global_behavior.jsonl
global_behavior.parquet
global_behavior.manifest.json
~~~

记录专家实际选择，作为：

- 行为克隆；
- 路线、事件、商店、奖励和篝火先验；
- 长期结果监督；
- 全局模型 warm start。

### 11.2 教师数据

单独生成：

~~~
global_teacher.jsonl
global_teacher.parquet
global_teacher.manifest.json
~~~

由 checkpoint、反事实分支和 NOSL 全局搜索产生，不覆盖 human_action。

### 11.3 切分规则

按以下 group 切分：

~~~
run_context_hash
episode_id
run_seed
branch_id
~~~

同一局及其分支不能跨 train、validation、test。seed 不能作为模型输入。

## 12. 质量门禁

导出前至少检查：

1. JSON schema 解析率 100%；
2. RunContext 版本元数据完整；
3. seed/context 与 CLI 兼容；
4. 地图重建与已知路线一致；
5. 每条记录有稳定 action ID；
6. selected action 属于当时合法或可见动作；
7. public view 没有 raw RNG 或未来内容；
8. .run、CLI 和人工来源字段一致；
9. 同一记录没有重复或冲突状态；
10. sl_status=unknown 的记录没有进入 NOSL Reliable 主集；
11. 同一 seed/episode 没有跨 split 泄漏；
12. 导出后可由 manifest 和源文件 hash 重建。

## 13. 实施阶段

### G0：Schema 和样本

交付：

- RunContext、GlobalDecisionRecord schema；
- 一个 .run 导入样本；
- 一个手工录入的专家决策样本；
- public/teacher 字段隔离测试。

出口：可以从 JSONL 重建一条人工决策记录。

### G1：CLI 会话和地图浏览器

交付：

- CLI process manager；
- start_run/get_map；
- 地图拓扑显示；
- seed/context hash；
- 路线点击和当前位置。

出口：固定 seed 下地图重复，能显示完整可见地图。

### G2：人工决策录入

交付：

- route、reward、shop、campfire、event、ancient 表单；
- CLI 自动填充 public state 和 options；
- 人工提交 selected action；
- 来源 ID、标注者 ID 和备注字段；
- 自动 pre/post hash。

出口：可连续录入一局中的多个局外决策。

### G3：.run 导入和状态对齐

交付：

- 调用 extract_run_history.py；
- floor、room、choice 对齐；
- HP、金币、卡组和遗物差异显示；
- 对齐失败队列。

出口：至少一局 .run 可转换为合法 global_behavior.jsonl，并在 CLI 地图可用时输出基础楼层对齐与 mismatch_count。

### G4：战斗摘要跳转和 checkpoint

交付：

- CombatSummary 接口；
- 战斗后摘要跳转；
- checkpoint 保存/恢复；
- partial episode 标记；
- 不污染主线的只读分支。

出口：没有战斗逐动作数据时，也能继续标注后续局外决策。

### G5：导出和质量门禁（已完成）

交付：

- JSONL/Parquet 导出；
- split 工具；
- DatasetManifest；
- quality report；
- unresolved/mismatch 审查页面。

当前已完成 JSONL、Manifest、SHA-256、质量统计、会话级校验、状态差异、分组切分和 Parquet 转换适配器；Parquet 实际运行需在训练环境安装 PyArrow。

### G6：全局教师衔接（GUI 接口部分完成）

已实现接口、一步/多层确定性 CLI 分支桥接、CLI 响应 action ID/CLI action 参数归一化（地图、事件、篝火、奖励、商店）、显式概率树 Expectimax、外部 JSONL 概率提供器适配和 `teacher_batch.py` 批量物化：`teacher-records` 要求 checkpoint 父引用，`teacher-evaluate`、`teacher-cli-tree` 和 `teacher-provider-tree` 对分支执行并恢复父状态，`teacher-search` 对概率树执行深度限制搜索；影子模拟器的具体概率实现仍待接入。

GUI 已交付稳定键、checkpoint 分支、teacher-search、teacher-provider-tree、teacher_batch 和独立 Manifest；正式 NOSL 全局搜索由 Core/Simulator 提供器完成后接入。

交付：

- checkpoint 反事实分支；
- NOSL 全局教师价值；
- global_teacher 数据集；
- human/teacher 差异报告。

出口：行为数据和教师数据可通过稳定键关联，但来源保持分离；GUI 范围验收完成。

## 14. 测试计划

### 单元测试

- RunContext hash 稳定性；
- action ID 规范化；
- .run 字段解析；
- card、relic、potion、deck diff；
- public/teacher 字段过滤；
- split group 防泄漏。

### CLI 集成测试

- 同 seed/context 两次 get_map 一致；
- 路线选择后当前位置和可达节点正确；
- 事件、奖励、商店、篝火选项可记录；
- action 前后 hash 可重放；
- checkpoint 恢复不改变主线；
- CLI 失败后保留此前有效记录。

### 数据门禁测试

- 缺失版本或 seed 被拒绝；
- 非法 action 被拒绝；
- raw RNG 写入 public view 被拒绝；
- future outcome 写入学生字段被拒绝；
- map mismatch 被降级；
- sl_status=unknown 不得进入 Reliable 主集。

## 15. 资源和部署

GUI 本身可在现有电脑运行，不需要 GPU。推荐：

~~~
本地 GUI：32GB RAM 足够
CLI/人工标注：本地 x86_64 Windows 优先
批量重放：可迁移到 x86_64 Ubuntu/Windows 服务器
全局大规模分支：64–128GB RAM 服务器
PyTorch 训练：独立 NVIDIA GPU 节点
~~~

标注草稿实时写入磁盘；每个 episode 独立保存，避免 GUI 崩溃丢失整局数据。

## 16. 首版验收标准

首版 GUI 完成定义：

1. 不依赖外部媒体解析；
2. 可输入固定 RunContext 和 seed；
3. 可从 CLI 显示地图拓扑和当前 public state；
4. 可手动记录路线、事件、Ancient、奖励、商店和篝火选择；
5. 可导入 .run 并对齐已访问路线；
6. 没有战斗逐动作记录时可使用 Combat Summary 跳转；
7. 每条记录包含 source、provenance、SL 和 quality metadata；
8. public view 不含未来隐藏信息；
9. 可导出 global_behavior.jsonl 和 manifest；
10. 原始标注可重放、审查和修正；
11. 人工行为数据与后续 NOSL 教师标签保持分离。

## 17. 推荐第一批数据

先不追求大量数据，建议先完成一小批高质量、无明显 SL 的完整对局标注，用于验证流程：

~~~
固定 v0.111.0
单人
Act 1 起步
至少覆盖路线、奖励、商店、篝火、事件和 Ancient
保留来源 ID 和标注备注
~~~

流程稳定后，再扩展角色、Ascension、Act 和不同专家风格。数据量增加前先确认 map_reconstruction、state_source 和 label_quality 的统计正确。

## 18. 与现有项目的关系

~~~
人工标注 GUI
    → global_behavior
    → Route/Event/Shop/Reward/Campfire 模型
             ↓ CombatSummary
现有 CombatPolicy + DeterministicSimulator + Expectimax
             ↓
GlobalDecisionOrchestrator 动态重规划
~~~

该 GUI 是全局决策数据入口，不改变当前战斗模型的动作契约，也不要求等待战斗模型全部语义完成后才能开始 G0–G3。只有 G4 之后的高质量路线价值和全局教师阶段，才依赖稳定的 CombatSummary 和战斗差分门禁。
