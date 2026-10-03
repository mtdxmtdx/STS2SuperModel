# STS2SuperModel — NOSL V4

Silent A10 单场战斗 NOSL 独立工程。当前接入 **固定上游规则＋最小 NOSL 连接层**：真实 C# 规则引擎适配、独立分支终局、公共知识和后验采样。M3–M6 的终局评价、公共信息教师、数据管线与独立学生工程已进入可审查检查点；已完成5,000个去重有效决策点和1轮、497步的受限试训。16场独立构造测试中双方均全胜，学生多消耗4瓶药水，尚不能声称策略更强；资源标定、后验覆盖和后续数据质量门槛仍未闭合。不是游戏 Mod；不连接客户端；尚未正式训练或晋升模型。

## 从这里开始

| 入口 | 内容 |
|---|---|
| [最终工程检查点](docs/M3_M6_ENGINEERING_CONTINUATION.md) | 原生事件、自然局入库、有限回血与学生工程已验证；完整覆盖/资源标定仍有门槛 |
| [首轮试点结果](docs/FIRST_BOUNDED_PILOT.md) | 数据、实际试训、闭环结果、错误诊断和未闭合门槛 |
| [后续160次验证](docs/BALANCED_VALIDATION_BLOCK.md) | 40场构造战斗、73个有效决策点、超时与标签稳定性；扩大规模门槛未通过 |
| [已确认偏好](docs/CONFIRMED_PREFERENCE_FEEDBACK.md) | 两个具体偏好约束及稀有药水的暂定倾向，不虚构统一价格 |
| [50项验收映射](docs/M3_M6_ACCEPTANCE_MAP.md) | 原始编号逐项对应实测证据、支持边界和未完成工作 |
| [M3–M6检查点](docs/M3_M6_ACCEPTANCE_CHECKPOINT.md) | 新实现、实测证据、数据阶段及尚未闭合的门槛 |
| [当前地图条件采样](docs/NATIVE_COMPLETE_MAP_RECONSTRUCTION.md) | 新版独立地图先验、完整公开地图重建与v5学生通道；新开发根局面已暴露可修复的奖励兼容与公开历史提议缺口 |
| [历史混合采样验证](docs/NATIVE_REWARDS_TAPE.md) | 原奖励先验及其公开历史提议的验证边界 |
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

旧源码、数据和提交历史保留在 [codex/history-2026-10-01](https://github.com/mtdxmtdx/STS2SuperModel/tree/codex/history-2026-10-01)，固定归档提交为 [b0bb07b](https://github.com/mtdxmtdx/STS2SuperModel/tree/b0bb07bdd09f8ae7833724126b18738c93d09eb4)。本次工作发布为 STS2SuperModel 的独立分支与草稿PR，未合并main；实验权重与数据单独保存，不混入源码提交。

当前独立地图先验将地图生成随机带与奖励、其他原生状态随机带分开，完整公开地图通过v2证据与v5学生通道传递。新版v3采样组合已补齐已定位的事件选项、Gorge公开卡牌、DaggerThrow与Fairy历史约束，并修复被捕获的原生回调错误分类。完整Worker回归**1,928/1,928通过**，v5数据、受限训练接口和模型边界**53/53通过**；Core源码未再改变，既有完整回归为4,493通过、3项可选跳过。

新的原生候选API已实际完成一个根局面的7动作×2独立世界共14条结算分支，并通过Python适配、完整公开输入/结果校验及有限的前向损失检查。没有新增反向传播、优化器步骤或入库授权；v5接口只提供辅助监督，未知资源价值与策略排序仍有掩码。相同8根的隔离复测在219秒内完成执行：5根完整、68/98条分支结算、30条计算截断，引擎错误0；第三场战斗门槛仍未通过。新定位缺口为完整公开商店库存以及后续房间/敌人入口条件。另12个新开发来源已预声明，当前保持未启动。参见[v3验证](configs/native_map_origin_verification_v3.json)、[新候选API](docs/NATIVE_COMPLETE_MAP_CANDIDATES.md)、[v5数据准备](docs/V5_NATIVE_DATA_PREPARATION.md)及[受限试训接口](docs/V5_BOUNDED_PILOT_INTERFACE.md)。历史v2为4根完整、54/98条分支结算，不能替代当前版本测量。

历史原生及混合采样证据保存在[通用回放](docs/NATIVE_OWNED_REPLAY.md)、[条件随机带](docs/NATIVE_CONDITIONAL_TAPE.md)、[混合v5](docs/NATIVE_HYBRID_V5.md)和[v7验证](configs/native_rewards_hybrid_v7_verification.json)。旧先验v6的固定8根诊断曾完成5根、结算64/96条分支，但战斗索引2未通过；v7的2,048个地图候选仅12个匹配全部公开切片，三根带历史局面各256次零匹配。它们是不同版本下的历史测量，不能作为新地图先验的吞吐结果。尚未扩大生产数据或再次拟合。
