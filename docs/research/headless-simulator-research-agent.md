# StS2 无头模拟器与种子机制研究

> 研究对象：`D:\STS2BestChoice\STS2SuperModel\杀戮尖塔种子机制研究.md` 中的 headless / AutoSlayer 说法，以及它们对当前 NOSL Expectimax 教师和影子模拟器的实际价值。
>
> 核验日期：2026-08-29（Asia/Shanghai）

版本注记：官方 [v0.111.0 beta patch notes](https://store.steampowered.com/news/app/2868840/view/671751488532383386) 标记为 `public-beta`；本报告所有 DLL/行为结论均限定在项目锁定的 `v0.111.0` assembly hash，不与 main 分支 v0.107.1 混用。

## 1. 结论先行

1. **“无头模拟器”不是一个单一组件。** 当前资料中至少有三种东西被混称：
   - Godot 的通用 `--headless` 渲染/显示模式；
   - `sts2-cli` 的真实 `sts2.dll` 引擎 harness（.NET 进程 + GodotSharp stubs + 少量 IL patch）；
   - 游戏程序集内的 `MegaCrit.Sts2.Core.AutoSlay.AutoSlayer` 自动化框架。
   三者都不等同于项目自己的 `DeterministicSimulator`。
2. **v0.111 的 RNG 事实已能在本地直接复核。** 当前锁定 DLL（`v0.111.0`, commit `41cef1ea`, SHA-256 `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`）的 `Rng._random` 是 `MegaRandom`，内部有四个 `UInt64` 状态字；反射运行输出与 xoshiro256** 参考公式逐次相同。`RunRngSet` 当前包含 12 条流。
3. **AutoSlayer 适合做 QA/实机自动化，不适合作为 NOSL 教师。** 默认 handler 会随机选牌、随机选目标、自动选奖励；公开反编译的 combat handler 还会注入测试用 Plating/Regen，因此其轨迹不能直接当作普通游戏教师数据。
4. **`sts2-cli` 适合做真实引擎 oracle、轨迹采集和差分验证。** 它能从固定 seed 建立真实 `RunState`、执行动作、保存 checkpoint，并输出版本门禁、状态 hash、normalized action ID、trace schema 和 RNG counter；它不能替代高吞吐影子模拟器做百万级 Expectimax 分支。
5. **NOSL 应保留双层架构。** `DeterministicSimulator`/`NoslExpectimaxTeacher` 只从 public observation 建立 belief 并对未知结果走 chance node；`sts2-cli`/AutoSlayer 只做真实引擎 oracle、根状态采集、语义差分、回放和回归。raw RNG、未来牌序、完整 teacher snapshot 只能进入审计 sidecar。

## 2. 原研究稿逐条核验

| 原稿说法 | 当前证据 | 结论 |
|---|---|---|
| 每局有 12 字符 run seed，且由多个 PRNG 派生出牌堆、奖励、事件等随机过程 | Mega Crit 官方 v0.107.1 公告明确描述 primary run seed、多个分别影响抽牌/战斗奖励/事件的 PRNG | **确认（版本相关）** |
| v0.107.1 改用 xoshiro256** | 官方公告明确写出替换为 xoshiro256**；本地 v0.111 `MegaRandom` 四状态字和输出也逐次匹配 | **确认** |
| 全部数值管线使用 `decimal` | 官方公告没有此承诺；当前 DLL 的 RNG API 同时存在 `NextFloat`/`NextDouble`，不能从“random decimal”推断数值类型 | **不成立/证据不足** |
| 所有洗牌都采用固定 ID 预排序的 `StableShuffle` | v0.111 DLL 有 `ListExtensions.StableShuffle`/`UnstableShuffle`；反编译 `CardPileCmd.Shuffle` 调用前者，社区实现表现为先排序再 Fisher–Yates。但这不证明所有随机列表都统一使用它 | **局部确认，不能泛化** |
| 内置 AutoSlayer 是哑随机 headless Bot | 当前 DLL 元数据确有 `AutoSlayer`、room/screen handlers、Watchdog；反编译代码显示默认随机决策。但“headless”是运行环境描述，不是 AutoSlayer 的产品定义 | **模块确认，表述需收窄** |
| 恢复 seed 即可精确恢复未来 RNG | `sts2-save-rebuild` 明确把 upcoming RNG state 列为不可恢复，重建后进入 parallel universe | **错误** |
| xoshiro256** 使所有子系统统计独立 | 官方只说散点图不再显示 human-detectable correlation，没有证明数学独立性或跨版本调用顺序 | **过强** |

官方依据：[Mega Crit Major Update #2 – v0.107.1](https://store.steampowered.com/news/app/2868840/view/710026912607505280)。算法依据：[xoshiro256** reference implementation](https://prng.di.unimi.it/xoshiro256starstar.c)。

## 3. 当前 DLL 的本地实证

对 `D:\STS2BestChoice\STS2SuperModel\sts2-cli-v0111\lib\sts2.dll` 做 `System.Reflection.Metadata` 读取和反射运行：

- `MegaCrit.Sts2.Core.Random.Rng._random : MegaCrit.Sts2.Core.Random.MegaRandom`；
- `MegaRandom._s0.._s3 : UInt64`；
- `SerializableRng` 有 `counter,state0,state1,state2,state3` 字段；
- `RunRngSet` 暴露 12 条流：`UpFront`, `Shuffle`, `UnknownMapPoint`, `CombatCardGeneration`, `CombatPotionGeneration`, `CombatCardSelection`, `CombatEnergyCosts`, `CombatTargets`, `MonsterAi`, `Niche`, `CombatOrbGeneration`, `TreasureRoomRelics`；
- `Rng(UInt64 seed=123456)` 调用无界 `NextUnsignedLong()` 三次得到：

```text
2366053268901514180
2749059519329956733
13249008983097773270
```

三次输出以及每次后的四个状态字与 xoshiro256** 参考实现逐字匹配。这项实证比仅看社区文章可靠，但仍应把 DLL hash 和版本写入每个数据集分片。

## 4. 三种“无头”技术的边界

### 4.1 Godot 原生 `--headless`

Godot 官方文档将 `--headless` 定义为关闭显示/窗口管理并使用 Dummy audio，适合服务器、脚本和 CI；它不会自动提供 StS2 的动作协议、状态快照、RNG 分支或合法性检查。见 [Godot command-line tutorial](https://docs.godotengine.org/en/stable/tutorials/editor/command_line_tutorial.html) 与 [dedicated-server export](https://docs.godotengine.org/en/stable/tutorials/export/exporting_for_dedicated_servers.html)。因此不能把 `godot --headless` 直接当作 StS2 模拟器。

### 4.2 `sts2-cli` 真实引擎 harness

上游 [wuhao21/sts2-cli](https://github.com/wuhao21/sts2-cli) 的架构为：

```text
Python/JS/LLM --JSON stdin/stdout--> Sts2Headless (C#)
                                      |
                                sts2.dll（真实逻辑）
                                + GodotSharp stubs
                                + Harmony/IL patches
```

当前 fork `D:\STS2BestChoice\STS2SuperModel\sts2-cli-v0111` 的 `setup.sh` 会复制并备份 DLL，然后用 Mono.Cecil 将 `Task.Yield` 的 `IsCompleted` 置为 true、将等待队列方法替换为 `Task.CompletedTask`，以避免 headless async deadlock；这不是运行 Godot GUI 的截图方案。`RunSimulator.StartRun` 使用 `RunState.CreateForTest`、`SetUpTest`、`GenerateRooms`、`Launch` 和 `EnterAct` 建立真实运行状态。源码：[RunSimulator.cs](https://github.com/wuhao21/sts2-cli/blob/main/src/Sts2Headless/RunSimulator.cs)、[Program.cs](https://github.com/wuhao21/sts2-cli/blob/main/src/Sts2Headless/Program.cs)、[setup.sh](https://github.com/wuhao21/sts2-cli/blob/main/setup.sh)。

当前 fork 的 `Program.InspectCompatibility` 在存在 `lib/sts2.dll.original` 时以 original 文件计算兼容 hash；因此 trace 中的 `assembly_sha256` 不能单独表示运行时已加载的 patched 二进制。建议 sidecar 另记 patched/runtime binary hash 和 patch profile。

当前 fork 还支持 `start_run`/`action`/`load_save`/`get_map`/`set_player`/`set_draw_order`、`STS2_TRACE_PATH` JSONL flush、版本元数据、pre/post hash、normalized action ID 和 public/teacher view。一次本地双跑（seed `HEADLESS_RESEARCH_001`）观察到：

```text
exit=0
game_version=v0.111.0, game_commit=41cef1ea, compatible=true
start post hash=949522EF2C84DD06862CCA85D56486F10837890B347505C052F9B431D032C8A7
quit  post hash=E943A75C981F6427512D0F80A53F19340C9B00C4FD323AA5F7A8B14046F582E2
trace lines flushed=2
```

两次独立进程的 hash 完全相同；这证明当前 harness 的启动/trace 门禁可复现，不等于全部语义已零差异。

**流式协议注意事项（当前未改代码）：** 目前没有足够证据认定 `Program.WriteLine` 存在未 flush 缺陷；`Console.Out.WriteLine` 在带后台读取线程的实测中可以在进程仍运行时返回 JSON。真正可复现的是 Godot 初始化器会把一行非 JSON 日志（`SentryGodotInitializer: ...`）写到 stdout。严格 JSON 客户端必须丢弃/转发不以 `{` 开头的日志后再解析响应；仓库现有 `tests/conftest.py` 和 `agent/sts2_bridge.py` 已采用这一读取策略。应保留一个“进程不退出即可读到 ready/decision、并过滤非 JSON 行”的 streaming smoke test，而不是仅依赖退出后批量读取；是否显式 `Flush()` 应以该测试结果决定，不能把它写成已确认的协议 bug。

### 4.3 内置 AutoSlayer

当前 v0.111 DLL 元数据包含 `MegaCrit.Sts2.Core.AutoSlay.AutoSlayer`、`AutoSlayConfig`、`AutoSlayLog`、`Watchdog` 以及 Combat/Map/Event/Shop/Rest/Treasure/Victory 和多种 screen handler。社区反编译镜像 [AutoSlayer.cs](https://github.com/zhiyue/sts2-rl-agent/blob/main/decompiled/MegaCrit.Sts2.Core.AutoSlay/AutoSlayer.cs) 显示它把 seed 设为 `NGame.DebugSeedOverride`，使用 Godot 主线程、timeout 和 watchdog 驱动整局流程；默认 combat handler 用 `random.NextItem` 选可打牌和目标，默认 card selector 随机 shuffle 选牌。

[RL bridge guide](https://github.com/zhiyue/sts2-rl-agent/blob/main/docs/MOD_BUILD_GUIDE.md) 展示了 patch `NGame.IsReleaseGame()` 解锁 AutoSlay、替换 handlers 并以 WaitSpeed/AnimationSpeed 加速约 5–10 倍。公开反编译镜像的 [CombatRoomHandler.cs](https://github.com/zhiyue/sts2-rl-agent/blob/main/decompiled/MegaCrit.Sts2.Core.AutoSlay.Handlers.Rooms/CombatRoomHandler.cs) 还显示**该镜像版本**在 combat 开始时给玩家施加高层 Plating/Regen（999），这是 QA fixture；在当前 v0.111 运行前应重新反射/探针确认，且这类轨迹不得标记为普通游戏的 Reliable 教师数据。

## 5. 对当前 NOSL 影子模拟器的影响

| 层 | 输入 | 允许知道什么 | 用途 |
|---|---|---|---|
| 真实 CLI oracle | 固定版本、seed、公开动作 | 完整引擎状态（隐藏部分只进 sidecar） | 语义差分、轨迹采集、回放 |
| 影子模拟器/Expectimax | `NoslBeliefState` + `ActionCandidate` | public state；未观察随机只作为分布 | 教师标签、运行时兜底 |
| AutoSlayer bridge | 实机对象/handler | 引擎对象、seed、隐藏状态 | 在线 agent 接入、QA |
| 学生模型 | public observation + legal mask | 不含 raw RNG、未来牌序、teacher snapshot | 部署 |

### RNG 流覆盖

当前 `LiveCombatSnapshotAdapter`/`RandomModel.cs` 主要捕获七条 combat 流：Shuffle、CombatCardGeneration、CombatPotionGeneration、CombatCardSelection、CombatEnergyCosts、CombatTargets、CombatOrbGeneration。这对一回合战斗可作为第一阶段，但全 run 还会用到其余五条 `UpFront`、`UnknownMapPoint`、`MonsterAi`、`Niche`、`TreasureRoomRelics`。建议为 run-level sidecar 扩展这些流，并按版本锁定数量/名称；不要把未捕获流伪装成已知。

作为**仅供参考、不是本项目依赖**的交叉证据，`D:\STS2BestChoice\reference\CombatSolver\src\Engine\InCombat\Simulation\CombatPredictionRngSet.cs` 克隆了 9 条战斗流（七条基础流加 `MonsterAi`、`Niche`），`IntentForecaster` 使用 `MonsterAi` 推演随机怪物状态机，`MonsterMoveEffects` 使用 `Niche` 生成怪物随机数值。这说明多回合/完整战斗预测确实需要另外两条流；不应直接把 CombatSolver 接入 NOSL。

### StableShuffle

当前 DLL 的 `ListExtensions.StableShuffle` 已实证存在。社区反编译实现为 `sort(list) -> UnstableShuffle(list)`，后者是 Fisher–Yates；本地反射调用 `StableShuffle([9,1,7,3,5], seed=123456)` 得 `[3,7,5,9,1]`，与该算法和项目高半 128 位 bounded mapping 完全吻合。影子模拟器只能在具体路径证实后使用它；动态牌、重复实例仍需验证 comparer/instance ID 规则。

### NOSL 标签

- `run_seed` 只能作为数据集分片键、重放键和版本回归键，不能作为策略输入；
- raw RNG words、未来 draw pile、实际随机结果和完整 teacher view 只能写审计 sidecar；
- 同一 public state 配不同隐藏 RNG 时，NOSL 标签应保持不变（或按观测等价类聚合）；
- 未知结果生成 chance 分支，分支后重新求 max continuation；超过阈值时记录可复现采样种子、概率质量和置信区间，不标成 exact/Reliable。

## 6. 推荐后续顺序

1. 固化 v0.111 reflection smoke test：检查 `Rng._random`、四状态字、`SerializableRng` 五字段和 12 条流；patched/original DLL hash 分开记录。
2. 用三个固定 seed 双跑 CLI，比较启动 decision、map、public snapshot、trace hash 和 counter。
3. 对 `StableShuffle` 建立空/单元素/重复元素测试，验证实例 ID 排序。
4. 从 CLI 采集代表性战斗根状态；只把 public snapshot 给 NOSL teacher，完整 RNG/牌堆保存在 sidecar。
5. 每个 `ActionCandidate` 做 CLI 一次执行与影子一次执行，比较 HP、Block、Energy、Piles、Power、Relic、counter delta 和 state hash；多 seed 零差异后才晋级 Reliable。
6. 真实 CLI 只负责根状态校验，影子模拟器按 root/shard 并行执行 exact/stratified Expectimax；先 1k smoke，再 10k/100k，并区分 Reliable/Estimated/Uncalculable。

## 7. 资源与性能

Godot headless 主要关闭显示/音频，GPU 不是 oracle 的前置条件；真实 CLI/AutoSlayer 受进程、主线程和对象图成本限制，适合低频 oracle。百万级分支由纯 C# 影子模拟器承担。9950X3D + 32 GB RAM 可先启动，先监控 worker 峰值、GC 和 trace 磁盘吞吐再决定加内存；GPU 留给后续神经网络训练。可参考 [Zamiell StS2 emulator](https://github.com/Zamiell/slay-the-spire-2-emulator)，其明确声明是 subset/not-yet-full emulator，也采用 C# 核心与 Python 训练层。

## 8. 最终判断

**可以使用无头真实引擎，但不应把它当作 NOSL Expectimax 本体。**

- `sts2-cli-v0111`：真实引擎 oracle + trace/差分；
- `AutoSlayer`：在线自动化/QA 参考，默认随机和测试注入轨迹不进教师集；
- `DeterministicSimulator`：高吞吐、可复制、可分支的 NOSL 搜索核心；
- `NoslExpectimaxTeacher`：在 public belief 上计算 top-k/期望值，不读取 seed 未来信息。

### 与既有《种子机制核验》的关系

`D:\STS2BestChoice\STS2SuperModel\种子机制核验.md`（2026-08-24）当时把 AutoSlayer/StableShuffle 标为“缺少一手证据”。本报告没有推翻其中关于“seed 不能恢复中途 RNG”和“xoshiro 不等于统计独立”的结论；只是针对当前锁定的 v0.111 DLL 增加了元数据、反射和固定输入输出证据。因此应把那两项更新为“v0.111 局部已证实、仍不得跨路径泛化”，并保留版本锁要求。

## 来源

- [Mega Crit 官方 v0.107.1 公告](https://store.steampowered.com/news/app/2868840/view/710026912607505280)
- [Mega Crit 官方 v0.111.0 beta patch notes](https://store.steampowered.com/news/app/2868840/view/671751488532383386)
- [sts2-cli README](https://github.com/wuhao21/sts2-cli)
- [sts2-cli RunSimulator.cs](https://github.com/wuhao21/sts2-cli/blob/main/src/Sts2Headless/RunSimulator.cs)
- [sts2-cli setup.sh](https://github.com/wuhao21/sts2-cli/blob/main/setup.sh)
- [AutoSlayer decompiled source mirror](https://github.com/zhiyue/sts2-rl-agent/blob/main/decompiled/MegaCrit.Sts2.Core.AutoSlay/AutoSlayer.cs)
- [AutoSlay bridge guide](https://github.com/zhiyue/sts2-rl-agent/blob/main/docs/MOD_BUILD_GUIDE.md)
- [StableShuffle decompiled extension mirror](https://github.com/zhiyue/sts2-rl-agent/blob/main/decompiled/MegaCrit.Sts2.Core.Extensions/ListExtensions.cs)
- [xoshiro256** reference implementation](https://prng.di.unimi.it/xoshiro256starstar.c)
- [Godot headless documentation](https://docs.godotengine.org/en/stable/tutorials/editor/command_line_tutorial.html)
- [sts2-save-rebuild](https://github.com/MufanQiu/sts2-save-rebuild)
- [Zamiell StS2 emulator](https://github.com/Zamiell/slay-the-spire-2-emulator)
- [tckmn correlated-randomness analysis](https://tck.mn/blog/correlated-randomness-sts2/)（逆向研究，版本限定）
