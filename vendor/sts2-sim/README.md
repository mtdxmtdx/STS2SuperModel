# sts2-sim — current upstream vendored Core

来源：[iRyougi/sts2-sim](https://github.com/iRyougi/sts2-sim/tree/5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0)，固定提交 `5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0`，规则声明 `0.111.0 / 41cef1ea / 222455745`，MIT。用户指定直接信任上游规则，原版对拍不作为本工程接入门槛。

保留完整公开 Core/Core.Tests 源码、AGENTS、CONTRIBUTING、LICENSE；上游原 README 保存在 [UPSTREAM_README.md](UPSTREAM_README.md)。没有加入游戏程序集、资源或反编译文件。构建入口为根目录 `Nosl.M012.sln`。

相对当前上游的最小 NOSL 连接补丁见 [vendor-sts2-sim.patch](../../docs/vendor-sts2-sim.patch)：独立房间终局上下文、A10 clone 上下文赋值顺序、只读公共抽牌/移动/生成/移除观察、明确公开状态的只读 getter，以及 friend assembly。没有重写卡牌/药水/遗物/敌人效果或随机规则。旧投影 API 保持原语义；奖励/具体 RunState 敏感内容使用独立原生 replay。

修改文件：
- `src/Sts2Sim.Core/Combat/CombatEngine.cs`
- `src/Sts2Sim.Core/Combat/CombatState.Clone.cs`
- `src/Sts2Sim.Core/Combat/ICombatObserver.cs`
- `src/Sts2Sim.Core/Commands/CardCmd.cs`
- `src/Sts2Sim.Core/Commands/CardPileCmd.cs`
- `src/Sts2Sim.Core/Models/CardModel.cs`
- `src/Sts2Sim.Core/Models/Cards/Bolas.cs`
- `src/Sts2Sim.Core/Models/Cards/Bombardment.cs`
- `src/Sts2Sim.Core/Models/Cards/Dowsing.cs`
- `src/Sts2Sim.Core/Models/Cards/Fetch.cs`
- `src/Sts2Sim.Core/Models/Cards/Guilty.cs`
- `src/Sts2Sim.Core/Models/Cards/ThrummingHatchet.cs`
- `src/Sts2Sim.Core/Models/Powers/NightmarePower.cs`
- `src/Sts2Sim.Core/Rooms/CombatRoom.cs`
- `src/Sts2Sim.Core/Sts2Sim.Core.csproj`

补丁为零上下文 unified diff；从固定上游复现时使用 `git apply --unidiff-zero`。

本地可选性能补丁：[vendor-map-pruning-optimization.patch](../../docs/vendor-map-pruning-optimization.patch)。在上述连接补丁后应用，同样使用 `git apply --unidiff-zero`。仅修改 `src/Sts2Sim.Core/Map/MapPathPruning.cs`，省略同一次剪枝扫描中完全相同节点片段的重复计算；不改变地图、候选顺序或 RNG 消耗。证明、前后测量和精确对照见 [sampling/cloning profile](../../docs/CLONE_PROFILING.md)。未向任一上游仓库写入。
