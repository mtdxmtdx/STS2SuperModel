# STS2SuperModel — NOSL V4

Silent A10 单场战斗 NOSL 独立工程。当前发布 **M0–M2 有限支持机制的技术闭环**：真实 C# 规则引擎适配、独立分支终局、公共知识和后验采样。不是游戏 Mod；不连接客户端；没有正式教师、训练数据或模型权重。

## 从这里开始

| 入口 | 内容 |
|---|---|
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

Python 只用于标准库协议烟测；规则由 C# 引擎执行。首次包还原需要 NuGet 或已有对应包缓存。无需游戏程序集或 GPU。`NuGetAudit=false` 避免离线漏洞索引请求，不改变依赖版本。

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

引擎来源固定为 [sts2-sim a3a6627](https://github.com/mtdxmtdx/sts2-sim/tree/a3a66276ee79bf9f75e8af32e9ef5a51592b26f8)，规则声明 `0.111.0 / 41cef1ea / 222455745`。修改的 6 个引擎文件及来源说明见 [vendor README](vendor/sts2-sim/README.md)。

## 当前边界

支持 11 类卡牌、5 类药水、3 类遗物以及 TwigSlimeS/LeafSlimeS/Nibbit 单敌人场景；其余内容明确不支持。**不等于全 Silent 内容完成，`READY_FOR_TRAINING=false`。** M3–M8 本轮未执行；客户端版本和原版保真尚未核对。详细范围以根目录 [NOSL_STATUS.json](NOSL_STATUS.json) 和覆盖清单为准。

旧源码、数据和提交历史保留在 [codex/history-2026-10-01](https://github.com/mtdxmtdx/STS2SuperModel/tree/codex/history-2026-10-01)，固定归档提交为 [b0bb07b](https://github.com/mtdxmtdx/STS2SuperModel/tree/b0bb07bdd09f8ae7833724126b18738c93d09eb4)。当前 main 没有混入旧模型或未完成的 B/C/D 任务；本次使用普通快进提交，不改写历史。
