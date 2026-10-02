# sts2-sim — current upstream vendored Core

来源：[iRyougi/sts2-sim](https://github.com/iRyougi/sts2-sim/tree/5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0)，固定提交 `5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0`，规则声明 `0.111.0 / 41cef1ea / 222455745`，MIT。用户指定直接信任上游规则，原版对拍不作为本工程接入门槛。

保留完整公开 Core/Core.Tests 源码、AGENTS、CONTRIBUTING、LICENSE；上游原 README 保存在 [UPSTREAM_README.md](UPSTREAM_README.md)。没有加入游戏程序集、资源或反编译文件。构建入口为根目录 `Nosl.M012.sln`。

相对当前上游的最小 NOSL 连接补丁见 [vendor-sts2-sim.patch](../../docs/vendor-sts2-sim.patch)：独立房间终局上下文、A10 clone 上下文赋值顺序、只读公共抽牌/移动/生成/移除观察、原生运行观察器转发、玩家 HP／药水已提交变化与自动结算边界通知、明确公开状态的只读 getter，以及 friend assembly。没有重写卡牌/药水/遗物/敌人效果。普通顺序随机模式保持原语义；另增显式启用的 `LabelRandomScope`，仅供有独立版本声明的假想随机带标签先验，详见下文。旧投影 API 保持原语义；奖励/具体 RunState 敏感内容使用独立原生 replay。

修改文件：
- `src/Sts2Sim.Core/Combat/CombatEngine.cs`
- `src/Sts2Sim.Core/Combat/CombatState.Clone.cs`
- `src/Sts2Sim.Core/Combat/CombatState.cs`
- `src/Sts2Sim.Core/Combat/ICombatObserver.cs`
- `src/Sts2Sim.Core/Commands/CardCmd.cs`
- `src/Sts2Sim.Core/Commands/CardPileCmd.cs`
- `src/Sts2Sim.Core/Commands/PotionCmd.cs`
- `src/Sts2Sim.Core/Entities/Creatures/Creature.cs`
- `src/Sts2Sim.Core/Entities/Players/IPlayerOutcomeObserver.cs`
- `src/Sts2Sim.Core/Entities/Players/Player.cs`
- `src/Sts2Sim.Core/Map/StandardActMap.cs`
- `src/Sts2Sim.Core/Models/CardModel.cs`
- `src/Sts2Sim.Core/Models/Cards/Bolas.cs`
- `src/Sts2Sim.Core/Models/Cards/Bombardment.cs`
- `src/Sts2Sim.Core/Models/Cards/Dowsing.cs`
- `src/Sts2Sim.Core/Models/Cards/Fetch.cs`
- `src/Sts2Sim.Core/Models/Cards/Guilty.cs`
- `src/Sts2Sim.Core/Models/Cards/ThrummingHatchet.cs`
- `src/Sts2Sim.Core/Models/Monsters/TwoTailedRat.cs`
- `src/Sts2Sim.Core/Models/Powers/NightmarePower.cs`
- `src/Sts2Sim.Core/Random/LabelRandomScope.cs`
- `src/Sts2Sim.Core/Random/MegaRandom.cs`
- `src/Sts2Sim.Core/Random/Rng.cs`
- `src/Sts2Sim.Core/Rewards/RewardsSet.cs`
- `src/Sts2Sim.Core/Rooms/CombatRoom.cs`
- `src/Sts2Sim.Core/Runs/RunDriver.cs`
- `src/Sts2Sim.Core/Runs/RunState.cs`
- `src/Sts2Sim.Core/Sts2Sim.Core.csproj`
- `tests/Sts2Sim.Core.Tests/Combat/PlayerOutcomeObserverTests.cs`
- `tests/Sts2Sim.Core.Tests/Random/LabelRandomScopeTests.cs`
- `tests/Sts2Sim.Core.Tests/Entities/Creatures/MonsterHpRollingTests.cs`

补丁为零上下文 unified diff；从固定上游复现时使用 `git apply --unidiff-zero`。

原生敌人扩展另加两个只读证书检查：TwoTailedRat 的召唤倒计时 getter，以及 CombatState 的遭遇槽位顺序比较。二者仅用于核对公开历史可以确定的状态，不修改游戏规则、随机数或克隆行为。

2026-10-02 已从该固定提交的全新本地归档依次应用两个补丁，对比全部 2,229 个已跟踪 Core／Core.Tests 文件，字节完全一致。验证摘要见 [vendor_patch_verification.json](../../configs/vendor_patch_verification.json)。最新初始前缀回调版本另独立运行一次完整 Core 测试：4,493 通过、3 个既有 opt-in 跳过、0 失败，共 4,496 项；保留该次真实 stdout／stderr、TRX、源码清单与程序集 SHA-256，路径及耗时记录在验证摘要中。玩家观察器不会被 clone 复制；结算统计只记录已经发生的数值变化，不进入策略输入，也不重复加入终局代价。详见 [结算事件记录](../../docs/OUTCOME_EVENT_ACCOUNTING.md)。

`LabelRandomScope` 是明确的标签分布扩展：仍按原生顺序推进每次随机生成器状态、计数器和数值转换，只在显式异步作用域内用假想随机带替换原始 64 位随机字。完整抽取前状态作为地址，相同状态（包括精确 clone）共享值；不同状态的共同种子相关性在新理想先验中被独立随机字替代。默认顺序模式、现有 keyed 模式及旧数据的先验不会因此改变。洗牌提议只通过原来的 Fisher–Yates 循环消费随机字；初始敌人HP提议只包装原生唯一HP算法中的一次抽取，保留原来的可选值、排重、赋值与精确概率校正。战斗奖励另加可选、仅用于标签的生成作用域回调，置于原生药水是否出现的抽取之前，保留药水、金币、卡牌及其余奖励的原生生成逻辑与抽取顺序。未启用奖励回调时保持现有语义。该扩展不是客户端顺序 seed 回放的替代品，也没有写入任一远端模拟器仓库。详见 [条件随机带采样](../../docs/NATIVE_CONDITIONAL_TAPE.md)。

首幕（act index 0）普通遭遇生成另加可选、仅用于标签的作用域回调，在原生普通遭遇循环之前进入；保留原生遭遇袋选择、原生抽取顺序及各次抽取的相关性。该回调未启用时不会额外获取随机流，也不改变其他幕的原生行为。

初始前缀接入另共享原生地图工厂与逐幕地图 RNG 派生，并在随机节点数量生成之前加入可选、仅用于标签的地图作用域回调。原生地图流名称、随机节点数量、地图生成各阶段与后续 hooks 均保持原顺序；未启用回调时沿用相同原生生成路径。相对 `425a898`，Core 仅修改 `Map/StandardActMap.cs`、`Random/LabelRandomScope.cs` 与 `Runs/RunState.cs`。独立地图剪枝性能补丁保持字节不变。

本地可选性能补丁：[vendor-map-pruning-optimization.patch](../../docs/vendor-map-pruning-optimization.patch)。在上述连接补丁后应用，同样使用 `git apply --unidiff-zero`。仅修改 `src/Sts2Sim.Core/Map/MapPathPruning.cs`，省略同一次剪枝扫描中完全相同节点片段的重复计算；不改变地图、候选顺序或 RNG 消耗。证明、前后测量和精确对照见 [sampling/cloning profile](../../docs/CLONE_PROFILING.md)。未向任一上游仓库写入。
