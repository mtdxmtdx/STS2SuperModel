# STS2SuperModel — NOSL V4

Silent A10 单场战斗 NOSL 独立工程。当前接入 **最新上游规则＋最小 NOSL 连接层**：真实 C# 规则引擎适配、独立分支终局、公共知识和后验采样。M3–M6 的终局评价、公共信息教师、数据管线与独立学生工程已进入可审查检查点；200点工程数据质量/吞吐检查已完成，后续试点数据与受限试训尚待推进。不是游戏 Mod；不连接客户端；尚未正式训练或晋升模型。

## 从这里开始

| 入口 | 内容 |
|---|---|
| [M3–M6检查点](docs/M3_M6_STATUS.md) | 新实现、实测证据、数据阶段及尚未闭合的门槛 |
| [当前状态](docs/STATUS.md) | 已实现范围、阶段状态和剩余缺口 |
| [开发与协议](docs/DEVELOPMENT.md) | 构建、测试、JSONL worker 和 API |
| [实施结果](docs/M0-M2_REPORT.md) | M0–M2 改动与证据 |
| [验证记录](docs/VERIFICATION.md) | 实施与发布副本检查 |
| [文档导航](docs/README.md) | 审计资料、V4 原始规格和历史入口 |

## 快速验证

安装 .NET SDK **9.0.303**，在仓库根目录执行：

```powershell
dotnet build .\Nosl.M012.sln -c Release -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet test .\tests\Nosl.Tests\Nosl.Tests.csproj -c Release --no-build -m:1 -nr:false
python -B .\tools\protocol_smoke.py
```

规则由 C# 引擎执行；Python 用于数据准备和学生工程，详见 [Python入口](python/README.md)。协议烟测只需标准库。首次包还原需要 NuGet 或已有对应包缓存。无需游戏程序集或 GPU。`NuGetAudit=false` 避免离线漏洞索引请求，不改变依赖版本。

## 目录

```text
src/Nosl.Contracts/     公共 DTO 和诊断策略契约
src/Nosl.Worker/        战斗适配、选择、知识、belief、分支诊断、JSONL
src/Nosl.PublicPolicy/  仅依赖 Contracts 的独立公共诊断策略进程
tests/Nosl.Tests/       真实适配与 NOSL 集成测试
vendor/sts2-sim/        固定上游 Core/Core.Tests 源码及 MIT 许可证
configs/               基线、覆盖、环境摘要和逐项验收
docs/                  当前说明、引擎补丁和 V4 原始规格
tools/                 两进程协议烟测
```

引擎来源固定为 [sts2-sim 5a9576b](https://github.com/iRyougi/sts2-sim/tree/5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0)，规则声明 `0.111.0 / 41cef1ea / 222455745`。最小只读观察/分支生命周期补丁及来源说明见 [vendor README](vendor/sts2-sim/README.md)。

## 当前边界

已切换到用户指定的 [iRyougi/sts2-sim 5a9576b](https://github.com/iRyougi/sts2-sim/tree/5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0)。全目录按上游注册表直接接入，覆盖静默单人牌池、生成与跨池内容、全部药水/遗物，以及普通/精英/Boss/多敌人原生遭遇。规则不在连接层重写。

**目录接入不等于所有组合验证完成。** 最新连接契约、采样限制、强制事件上下文和实测范围见 [覆盖与连接说明](docs/COVERAGE_BRIDGE.md)。`READY_FOR_TRAINING=false`；M3–M6 的后续状态由独立交付报告更新。

旧源码、数据和提交历史保留在 [codex/history-2026-10-01](https://github.com/mtdxmtdx/STS2SuperModel/tree/codex/history-2026-10-01)，固定归档提交为 [b0bb07b](https://github.com/mtdxmtdx/STS2SuperModel/tree/b0bb07bdd09f8ae7833724126b18738c93d09eb4)。当前 main 没有混入旧模型或未完成的 B/C/D 任务；本次使用普通快进提交，不改写历史。
