# sts2-sim — NOSL vendored core

来源：[mtdxmtdx/sts2-sim](https://github.com/mtdxmtdx/sts2-sim/tree/a3a66276ee79bf9f75e8af32e9ef5a51592b26f8)，固定提交 `a3a66276ee79bf9f75e8af32e9ef5a51592b26f8`，规则声明 `0.111.0 / 41cef1ea / 222455745`。

本目录只随附公开仓库中的 Core 和 Core.Tests 源码、MIT 许可证与贡献规则；不包含上游 CLI/Solver 工程、游戏程序集或游戏资源。构建入口是仓库根目录 `Nosl.M012.sln`，Core 回归可直接运行 `tests/Sts2Sim.Core.Tests/Sts2Sim.Core.Tests.csproj`。

NOSL 对 6 个文件做了最小扩展，补丁见 [vendor-sts2-sim.patch](../../docs/vendor-sts2-sim.patch)。`CloneForNosl` 分支生命周期隔离、A10 clone 上下文顺序、公用抽牌/洗牌观察回调和 worker friend assembly 是新增部分；原 `Clone` API 的共享 room 语义保持不变。规则仍委托原引擎；客户端对拍未执行。

修改文件：
- `src/Sts2Sim.Core/Combat/CombatEngine.cs`
- `src/Sts2Sim.Core/Combat/CombatState.Clone.cs`
- `src/Sts2Sim.Core/Combat/ICombatObserver.cs`
- `src/Sts2Sim.Core/Commands/CardPileCmd.cs`
- `src/Sts2Sim.Core/Rooms/CombatRoom.cs`
- `src/Sts2Sim.Core/Sts2Sim.Core.csproj`
