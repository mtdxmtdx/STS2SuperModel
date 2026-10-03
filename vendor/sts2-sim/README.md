# sts2-sim — current upstream vendored Core

来源：[iRyougi/sts2-sim](https://github.com/iRyougi/sts2-sim/tree/5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0)，固定提交 `5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0`，规则声明 `0.111.0 / 41cef1ea / 222455745`，MIT。用户指定直接信任上游规则，原版对拍不作为本工程接入门槛。

保留完整公开 Core/Core.Tests 源码、AGENTS、CONTRIBUTING、LICENSE；上游原 README 保存在 [UPSTREAM_README.md](UPSTREAM_README.md)。没有加入游戏程序集、资源或反编译文件。构建入口为根目录 `Nosl.M012.sln`。

相对当前上游的最小 NOSL 连接补丁见 [vendor-sts2-sim.patch](../../docs/vendor-sts2-sim.patch)：独立房间终局上下文、A10 clone 上下文赋值顺序、只读公共抽牌/移动/生成/移除观察、原生运行观察器转发、玩家 HP／药水已提交变化与自动结算边界通知、明确公开状态的只读 getter，以及 friend assembly。没有重写卡牌/药水/遗物/敌人效果。普通顺序随机模式保持原语义；另增显式启用的 `LabelRandomScope`，仅供有独立版本声明的假想随机带标签先验，详见下文。旧投影 API 保持原语义；奖励/具体 RunState 敏感内容使用独立原生 replay。

## 当前 Map v6 补丁包（2026-10-03）

当前 Core 固定于 `28cccddc44b3cbc4e91eabc7aaf5dfe48dff04a7`，源码树 `31d0fe0c98d774f28dc967b68f8093d7d0f7d1f4`；Core.Tests 树 `2f0adb0d57ab9b7c1cc54a2f6ceaf495e9cc55f1` 与原始恢复包一致。主连接补丁包含 63 个文件段，另保留独立地图剪枝补丁。相对上一已验证的 `a6ee771`／打包提交 `b34d0e3`，仅修改 `Random/Rng.cs` 并新增 `Random/IAbortableLabelShuffleBoundary.cs`；其余 61 个主补丁段、Core.Tests 和地图剪枝补丁均字节不变。本地 Doors 接入经过独立审查后合入为 `215e098`，其 Core 字节与原提交 `462e1ed` 一致。

新增的是可选 label 洗牌边界的原生失败通知。原有 Fisher–Yates 循环的 `NextInt` 抽取、列表读取、交换及次序完全保留；只有实现 `IAbortableLabelShuffleBoundary` 的参与者才在原生失败时收到 `Abort`，并确保其通知或清理错误不会替换原生异常。普通 scope 的释放语义、普通原生调用及 RNG 消耗不变，没有增加抽取或改变种子。本次打包没有修改任何源码或测试。

此前显式启用的默认变换、UnknownRoom 和 Merchant 边界仍保留。默认变换没有作用域时直接执行原表达式；启用后仍执行原生候选顺序、单次索引抽取和变换构造。UnknownRoom 与 Merchant 分别围绕原生房间抽取、玩家遗物袋及首次商店库存生成，成功完成值在原生方法成功后记录。相对原始 `4d888e5` 恢复源码，当前共九个 Core 文件不同，其中四个文件为新增。

本次重新核验同一已授权原始恢复包全部 56 个 payload 与 `source.zip` 全部 2,741 个文件。对已验证的 2,239 个 Core／Core.Tests 文件逆向应用原始两个补丁，得到 2,225 文件的上游派生基线；原始补丁正向重放完全复现原始源码。随后当前累计主补丁加不变的地图剪枝补丁，逐文件重建全部 2,243 个当前已跟踪文件。文件集合、大小、SHA-256、直接字节比较与重建前后源码清单均一致。此流程使用原始源码的来源证据，没有新下载上游，也不声明独立全新上游归档校验。上游固定点仍为已验证原始包中的 `5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0`。

当前主补丁 SHA-256 为 `ca7ffdb4a77a421ff039a34a857c4f315b476592bb26cdcf6959f276ba6f9ec5`；地图补丁仍为 `fad7d6a7e85ea44901da2b01227f972926a116af5398686a2628326002bcdc08`。完整方法、文件哈希、来源边界、当前结果与旧报告见 [vendor_patch_verification.json](../../configs/vendor_patch_verification.json)。`PhialHolster.cs:36` 的既有四空格空白行按原字节保留，严格 whitespace 检查仍会报告该历史空白，不影响补丁应用与字节重建。

当前完整 Core 已由主执行任务在冻结 runtime `28cccdd` 上新运行一次，以 exit 0 结束：4,493 通过、3 个既有 opt-in 跳过、0 失败，终端报告 4 分 21 秒。本打包任务独立读取并核对当前真实 log／TRX、隔离目录 `artifacts/map-v6-full-core` 的程序集哈希及运行后源码清单；后者与本次 2,243 文件重建清单完全一致。当前 Core 程序集 SHA-256 为 `121281039e9e56b81fe3f15dd64a8963a3f041c795bff71fd43c8573eada3c5d`，测试程序集为 `106974951361b5f16c50c6adf8e28e7197e18a289f4ccc0c08844177d64489f6`。没有另捕获测试开始前清单；准确执行命令、路径、时间与哈希见验证 JSON。全部旧回归仍按原源码固定点单独保留，没有重复计数。本打包任务没有启动任何构建或测试；没有生产生成、学习或训练，也没有对 `iRyougi/sts2-sim` 或 `mtdxmtdx/sts2-sim` 进行远端写入。

当前主补丁修改／新增文件：
- `src/Sts2Sim.Core/Combat/CombatEngine.cs`
- `src/Sts2Sim.Core/Combat/CombatState.Clone.cs`
- `src/Sts2Sim.Core/Combat/CombatState.cs`
- `src/Sts2Sim.Core/Combat/ICombatObserver.cs`
- `src/Sts2Sim.Core/Commands/CardCmd.cs`
- `src/Sts2Sim.Core/Commands/CardPileCmd.cs`
- `src/Sts2Sim.Core/Commands/PotionCmd.cs`
- `src/Sts2Sim.Core/Content/Acts/Overgrowth.cs`
- `src/Sts2Sim.Core/Content/EncounterDefinition.cs`
- `src/Sts2Sim.Core/Entities/Creatures/Creature.cs`
- `src/Sts2Sim.Core/Entities/Merchant/MerchantInventory.cs`
- `src/Sts2Sim.Core/Entities/Players/IPlayerOutcomeObserver.cs`
- `src/Sts2Sim.Core/Entities/Players/Player.cs`
- `src/Sts2Sim.Core/Factories/CardFactory.cs`
- `src/Sts2Sim.Core/Factories/PotionFactory.cs`
- `src/Sts2Sim.Core/Map/StandardActMap.LabelReconstruction.cs`
- `src/Sts2Sim.Core/Map/StandardActMap.cs`
- `src/Sts2Sim.Core/Models/CardModel.cs`
- `src/Sts2Sim.Core/Models/Cards/Bolas.cs`
- `src/Sts2Sim.Core/Models/Cards/Bombardment.cs`
- `src/Sts2Sim.Core/Models/Cards/Dowsing.cs`
- `src/Sts2Sim.Core/Models/Cards/Fetch.cs`
- `src/Sts2Sim.Core/Models/Cards/Guilty.cs`
- `src/Sts2Sim.Core/Models/Cards/ThrummingHatchet.cs`
- `src/Sts2Sim.Core/Models/Events/Ancients/Neow.cs`
- `src/Sts2Sim.Core/Models/MonsterModel.cs`
- `src/Sts2Sim.Core/Models/Monsters/CorpseSlug.cs`
- `src/Sts2Sim.Core/Models/Monsters/TwoTailedRat.cs`
- `src/Sts2Sim.Core/Models/Powers/NightmarePower.cs`
- `src/Sts2Sim.Core/Models/Relics/PhialHolster.cs`
- `src/Sts2Sim.Core/Models/Relics/ScrollBoxes.cs`
- `src/Sts2Sim.Core/MonsterMoves/RandomBranchState.cs`
- `src/Sts2Sim.Core/Odds/CardRarityOdds.cs`
- `src/Sts2Sim.Core/Odds/PotionRewardOdds.cs`
- `src/Sts2Sim.Core/Random/IAbortableLabelShuffleBoundary.cs`
- `src/Sts2Sim.Core/Random/LabelCardTransformScope.cs`
- `src/Sts2Sim.Core/Random/LabelCombatReshuffleScope.cs`
- `src/Sts2Sim.Core/Random/LabelCorpseSlugScope.cs`
- `src/Sts2Sim.Core/Random/LabelEventGenerationScope.cs`
- `src/Sts2Sim.Core/Random/LabelMapConstructionScope.cs`
- `src/Sts2Sim.Core/Random/LabelMerchantScope.cs`
- `src/Sts2Sim.Core/Random/LabelMonsterMoveScope.cs`
- `src/Sts2Sim.Core/Random/LabelPhialHolsterScope.cs`
- `src/Sts2Sim.Core/Random/LabelRandomProvenance.cs`
- `src/Sts2Sim.Core/Random/LabelRandomScope.cs`
- `src/Sts2Sim.Core/Random/LabelRewardResourceScope.cs`
- `src/Sts2Sim.Core/Random/LabelSlimesWeakScope.cs`
- `src/Sts2Sim.Core/Random/LabelUnknownRoomScope.cs`
- `src/Sts2Sim.Core/Random/MegaRandom.cs`
- `src/Sts2Sim.Core/Random/PlayerRngSet.cs`
- `src/Sts2Sim.Core/Random/Rng.cs`
- `src/Sts2Sim.Core/Rewards/GoldReward.cs`
- `src/Sts2Sim.Core/Rewards/RewardsSet.cs`
- `src/Sts2Sim.Core/Rooms/CombatRoom.cs`
- `src/Sts2Sim.Core/Rooms/RoomFactory.cs`
- `src/Sts2Sim.Core/Runs/RunDriver.cs`
- `src/Sts2Sim.Core/Runs/RunRngSet.cs`
- `src/Sts2Sim.Core/Runs/RunState.cs`
- `src/Sts2Sim.Core/Saves/SerializableRng.cs`
- `src/Sts2Sim.Core/Sts2Sim.Core.csproj`
- `tests/Sts2Sim.Core.Tests/Combat/PlayerOutcomeObserverTests.cs`
- `tests/Sts2Sim.Core.Tests/Entities/Creatures/MonsterHpRollingTests.cs`
- `tests/Sts2Sim.Core.Tests/Random/LabelRandomScopeTests.cs`

补丁为零上下文 unified diff；从固定上游复现时使用 `git apply --unidiff-zero`。

## 历史接口演进与验证记录（保留原检查点）

以下段落保留旧检查点的接口背景、文件计数与当时记录的测试结果。其中“最新”“本次”等措辞仅指各段对应的历史检查点；当前状态以上节和验证 JSON 顶层记录为准。

Map v5 的 `a6ee771`／打包提交 `b34d0e3` 已重建全部 2,242 个 Core／Core.Tests 文件，主补丁 SHA-256 为 `088dd4a85ddee8983d69fb08083ee2aefc9fe7689cb99ad9476c075087822d8d`。该固定点全新运行一次完整 Core：4,493 通过、3 个既有 opt-in 跳过、0 失败，终端报告 4 分 9 秒，exit 0。真实 log／TRX、独立程序集与运行后源码清单均已核验，未另捕获测试开始前清单。完整旧报告、主补丁、README 与源码清单按原字节保留，当前验证 JSON 的 `previous_verified_package` 给出路径与哈希，旧报告继续保留 Map v4 及原始恢复包的历史链。


Map v4 的 `e1d1cd2`／打包提交 `97e4dfa` 已重建 2,241 个 Core／Core.Tests 文件，主补丁 SHA-256 为 `00e11ca3d6dd3209c5c862ceed8534bf29b5f42ee42702bbab1a525c94cbdca6`，并全新运行一次完整 Core：4,493 通过、3 个既有 opt-in 跳过、0 失败，终端报告 4 分 12 秒。该次 log／TRX、隔离程序集与运行后源码清单已独立核验并保留；没有单独的测试开始前清单。整个旧报告、旧补丁、旧 README 与源码清单均原样另存，当前验证 JSON 的 `previous_verified_package` 给出路径和哈希，不把该次测试重复算作 Map v5 新测试。

更早 `50edac4` 的 4,493／3／0 历史结果也保留其原报告与哈希；该次旧完整 Core 的 raw log／TRX 不在原始恢复包内，本次未重新核验这些旧日志。这个缺失只指该历史检查点，不指上一段有实际原始日志的 Map v4 检查点。


原生敌人扩展另加两个只读证书检查：TwoTailedRat 的召唤倒计时 getter，以及 CombatState 的遭遇槽位顺序比较。二者仅用于核对公开历史可以确定的状态，不修改游戏规则、随机数或克隆行为。

2026-10-02 已从该固定提交的全新本地归档依次应用两个补丁，对比全部 2,230 个已跟踪 Core／Core.Tests 文件，字节完全一致。验证摘要见 [vendor_patch_verification.json](../../configs/vendor_patch_verification.json)。最新 Rewards 卡牌选择回调版本另独立运行一次完整 Core 测试：4,493 通过、3 个既有 opt-in 跳过、0 失败，共 4,496 项；保留该次真实 stdout／stderr、TRX、源码清单与程序集 SHA-256，路径及耗时记录在验证摘要中。玩家观察器不会被 clone 复制；结算统计只记录已经发生的数值变化，不进入策略输入，也不重复加入终局代价。详见 [结算事件记录](../../docs/OUTCOME_EVENT_ACCOUNTING.md)。

`LabelRandomScope.Enter` 是既有的全状态标签分布扩展：仍按原生顺序推进每次随机生成器状态、计数器和数值转换，只在显式异步作用域内用假想随机带替换原始 64 位随机字。完整抽取前状态作为地址，相同状态（包括精确 clone）共享值；不同状态的共同种子相关性在新理想先验中被独立随机字替代。默认顺序模式、现有 keyed 模式及旧数据的先验不会因此改变。洗牌提议只通过原来的 Fisher–Yates 循环消费随机字；初始敌人HP提议只包装原生唯一HP算法中的一次抽取，保留原来的可选值、排重、赋值与精确概率校正。战斗奖励另加可选、仅用于标签的生成作用域回调，置于原生药水是否出现的抽取之前，保留药水、金币、卡牌及其余奖励的原生生成逻辑与抽取顺序。未启用奖励回调时保持现有语义。该扩展不是客户端顺序 seed 回放的替代品，也没有写入任一远端模拟器仓库。详见 [条件随机带采样](../../docs/NATIVE_CONDITIONAL_TAPE.md)。

首幕（act index 0）普通遭遇生成另加可选、仅用于标签的作用域回调，在原生普通遭遇循环之前进入；保留原生遭遇袋选择、原生抽取顺序及各次抽取的相关性。该回调未启用时不会额外获取随机流，也不改变其他幕的原生行为。

初始前缀接入另共享原生地图工厂与逐幕地图 RNG 派生，并在随机节点数量生成之前加入可选、仅用于标签的地图作用域回调。原生地图流名称、随机节点数量、地图生成各阶段与后续 hooks 均保持原顺序；未启用回调时沿用相同原生生成路径。相对 `425a898`，Core 仅修改 `Map/StandardActMap.cs`、`Random/LabelRandomScope.cs` 与 `Runs/RunState.cs`。独立地图剪枝性能补丁保持字节不变。

另增显式启用、独立版本声明的 Rewards provenance 基础设施：`LabelRandomScope.EnterRewardProvenance` 使用 `native-rewards-provenance-tape-v1`，玩家 Rewards 以来源／有效初始种子／原始随机字游标定位；未标记 RNG 保留独立的全状态分区。精确 clone、reseed 与序列化显式保留对应来源或分区，缺失或不支持的混合快照标记会被拒绝；不能从数值状态或包装器 Counter 反推出 provenance。该先验有意区分 Rewards 与全状态流碰撞，并区分不同 Rewards 种子的错位状态碰撞；未标记流内部的原有别名关系不变。原生数值转换与状态推进保持原路径，普通顺序模式和既有标签入口的语义不变。相对上一打包源码 `863f5e4`，仅修改／新增七个 Core 文件；补丁已包含新文件 `Random/LabelRandomProvenance.cs`，其余补丁段与独立地图性能补丁保持字节不变。新增 provenance 用例属于 `Nosl.Tests`，不计入上述 Core 回归结果。后续工作层已接入独立混合先验与公开奖励卡牌提议；该段基础设施回归仅对应此前固定源码。

本地可选性能补丁：[vendor-map-pruning-optimization.patch](../../docs/vendor-map-pruning-optimization.patch)。在上述连接补丁后应用，同样使用 `git apply --unidiff-zero`。仅修改 `src/Sts2Sim.Core/Map/MapPathPruning.cs`，省略同一次剪枝扫描中完全相同节点片段的重复计算；不改变地图、候选顺序或 RNG 消耗。证明、前后测量和精确对照见 [sampling/cloning profile](../../docs/CLONE_PROFILING.md)。未向任一上游仓库写入。

最新连接层在 `CardFactory` 两种原生重载的逐卡选择边界增加可选 label-only 上下文，并从 `CardRarityOdds` 暴露已有阈值；不额外抽取 RNG，不重排生成流程，也不修改普通顺序模式。相对 `b8f2b4c` 仅这两个文件及 `LabelRandomScope.cs` 变化；全部2,230个文件重新从上游归档加补丁复现一致，完整 Core 4,493通过、3个既有opt-in跳过。完整新混合提议的 Worker 证明与吞吐结果见 [混合奖励验证](../../configs/native_rewards_hybrid_verification.json)，本次尚未通过广度吞吐门槛，也没有新增生产数据或训练。

v4组合检查点重新复现全部2,231个跟踪源码文件，并在固定生产源码`ded6aeb7`上重新完整运行Core：4,493通过、3个既有opt-in跳过、0失败。相对上一打包源码`3ae3160`，只新增或修改8个Core文件：`Factories/PotionFactory.cs`、`Models/Events/Ancients/Neow.cs`、`Models/Relics/ScrollBoxes.cs`、`Odds/PotionRewardOdds.cs`、`Random/LabelRandomScope.cs`、`Random/LabelRewardResourceScope.cs`、`Rewards/GoldReward.cs`和`Rewards/RewardsSet.cs`。这些是显式启用的公开条件标签边界；奖励异常时通知可中止边界，保留原始异常，不改变普通原生调用的随机抽取和生成顺序。其余补丁段及独立地图性能补丁不变。准确源码、程序集、原始测试日志和补丁哈希见[最新重建证据](../../configs/vendor_patch_verification.json)。历史段落中的2,230文件计数与回归结果仅对应各自旧检查点。

v5连接层相对已打包源码`ded6aeb7`只修改4个Core文件：`Content/EncounterDefinition.cs`、`Models/Monsters/CorpseSlug.cs`、新增`Random/LabelCorpseSlugScope.cs`和`Rooms/CombatRoom.cs`。显式启用的label作用域在遭遇工厂、战斗房间准备及CorpseSlug原生`NextInt(3)`之前提供实际假想工厂上下文；不传入来源seed或目标招式，也不直接改写怪物状态。未启用作用域时保持原生生成和抽取顺序。对固定源码`18075150`从上游归档加两个补丁重新复现全部2,232个跟踪Core／Core.Tests文件，字节与文件集合完全一致；其余补丁段、独立地图性能补丁与Core测试树保持不变。

该v5固定源码另全新运行一次完整Core套件：4,493通过、3个既有opt-in跳过、0失败，构建加测试270.32秒。使用独立构建目录，保留本次真实stdout／stderr、TRX、执行记录、源码清单及程序集哈希；运行前后源码哈希均一致。上一v4报告和原始回归记录按原源码固定点单独保留，不计作本次新测试。准确路径、时间与SHA-256见[最新重建证据](../../configs/vendor_patch_verification.json)。

v6连接层相对上一打包源码`18075150`只修改9个Core文件：`Commands/CardPileCmd.cs`、`Content/Acts/Overgrowth.cs`、`Content/EncounterDefinition.cs`、`Models/MonsterModel.cs`、`MonsterMoves/RandomBranchState.cs`、新增`Random/LabelCombatReshuffleScope.cs`、`Random/LabelMonsterMoveScope.cs`、`Random/LabelSlimesWeakScope.cs`及`Rooms/CombatRoom.cs`。这些显式启用的label作用域分别标记战斗弃牌重洗、怪物分支招式及弱史莱姆编队；普通原生调用仍按原顺序执行getter和随机抽取，包括编队的一元素抽取。重洗只在原生`StableShuffle`正常返回后确认成功，怪物招式只在原生赋值完成后记录完成状态；条件分支另外核对遍历时权重与原生求和时一致。

对固定源码`4c589ed0`从上游归档加两个补丁重新复现全部2,235个跟踪Core／Core.Tests文件，字节与文件集合完全一致。其余补丁段、独立地图性能补丁和Core测试树保持不变；v5与更早回归证据保留各自原始源码固定点。

该v6固定源码另全新运行一次完整Core套件：4,493通过、3个既有opt-in跳过、0失败，构建加测试261.96秒。采用独立构建目录与单MSBuild worker；保留本次真实stdout／stderr、TRX、执行记录、源码清单及程序集SHA-256，运行前后源码哈希均一致。上一v5报告和原始回归记录独立保留，不计作本次新测试。准确路径、时间与哈希见[最新重建证据](../../configs/vendor_patch_verification.json)。
