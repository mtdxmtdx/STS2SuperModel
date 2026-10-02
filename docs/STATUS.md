已完成5,000个有效决策点和497步受限试训；完整验收与正式训练准备仍受标定、覆盖和策略质量门槛限制。当前 M3–M6 实现、测试与试点结果请看 [M3–M6检查点](M3_M6_STATUS.md) 与 [TRAINING_READINESS](../TRAINING_READINESS.json)。以下原M0–M2记录仅供追溯。

> 历史基线记录：本文的11卡等数量属于 d01c5f8e 的原始 M0–M2 检查点。当前上游接入与验证请看 [COVERAGE_BRIDGE](../docs/COVERAGE_BRIDGE.md) 和 coverage_manifest.json；不要把历史数量视为现有目录限制。

# 历史M0–M2状态（2026-10-01，d01c5f8e）

当前机器可读状态：[NOSL_STATUS.json](../NOSL_STATUS.json)。下表保留原始 `M0_M1_M2_SUPPORTED_SCOPE` 历史记录，不代表当前交付状态。

| 阶段 | 当前状态 |
|---|---|
| M0 | 已锁定源码/规则/SDK，建立覆盖清单和实现审计 |
| M1 | 已完成有限内容的单场战斗技术闭环、独立分支生命周期和终局事实 |
| M2 | 已完成有限机制的公共知识、独立后验采样和公共进程隔离诊断 |
| M3–M8 | 本轮未执行；完整教师、数据生产、训练和部署未开始 |

## 历史真实支持范围

Silent A10；11 类卡牌、5 类药水、3 类遗物；TwigSlimeS、LeafSlimeS、Nibbit 的单敌人场景。卡牌升级只覆盖这些类型的合法升级。公开知识和后验按当前支持内容建模；未知内容显式返回 unsupported，不暗中简化。

覆盖全集登记了 1428 个注册模型、91 个 Silent 牌池条目；登记不代表验收通过。50 项 V4 验收逐项保留限定状态，见 [acceptance_progress.json](../configs/acceptance_progress.json)，没有批量标记全量通过。

## 历史验证与缺口

原实施基线相关测试 45/45，新增集成 31/31，核心定向回归 93/93；独立 worker 和公共策略协议烟测通过。当前 main 的整理副本重新验证构建、31 项集成测试和两进程协议。完整终局根诊断是 M2 信息隔离证据，不是 M4 教师效用。

`full_silent_content_ready=false`、`ready_for_training=false`、`ready_for_pilot=false`。实际客户端版本未核对，客户端保真对拍未执行。同进程并发未验证。

待闭合内容包括全 Silent 合法内容、复杂遗物/附魔/affliction、其他怪物潜在记忆、额外奖励队列、bundle/非手牌选择、通用中途选择采样与客户端对拍。本次上传不改变这些边界。
