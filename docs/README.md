# 文档导航

## 当前工程

- [原生生成药水入口](NATIVE_GENERATION_POTIONS.md)：固定200根中174根可导入，2,222次完整结算；仅开发证据

- [M3–M6实施检查点](M3_M6_STATUS.md)：教师、数据、学生工程及未闭合门槛
- [首轮受限试点](FIRST_BOUNDED_PILOT.md)：5,000有效根、497步试训、闭环结果与资源错误诊断
- [实验策略拒绝机制](EXPERIMENTAL_POLICY_GUARD.md)：未定价资源决策整体拒绝，保留诊断预测
- [平衡数据配方](BALANCED_GENERATION_V5.md)：按预先固定尝试区间记录成功、缺失与超时
- [训练准备状态](../TRAINING_READINESS.json)：机器可读门槛与实际证据

- [状态与边界](STATUS.md)：当前状态入口和M0–M2历史基线。
- [开发与协议](DEVELOPMENT.md)：相对路径构建、测试、worker JSONL 和 C# API。
- [M0–M2 实施结果](M0-M2_REPORT.md)：改动、证据与覆盖范围。
- [验证记录](VERIFICATION.md)：原实施证据及本次发布副本验证。
- [引擎补丁](vendor-sts2-sim.patch)：历史补丁参考；当前上游锁和本地变更以vendor说明与Git diff为准。
- [审计与配置](../configs/)：基线锁、环境摘要、实现审计、覆盖清单与逐项验收。

## V4 原始规格（历史输入）

[规格包入口](spec/v4/README.md)、[完整计划](spec/v4/PLAN_NOSL_FULL_COMBAT_V4.md)、[教师契约](spec/v4/TEACHER_CONTRACT_V4.md)、[原始实施提示词](spec/v4/AGENT_IMPLEMENTATION_PROMPT_V4.md)。此处保留输入版本，`DELIVERY_STATUS.json` 只描述当时规格包，不覆盖根目录 `NOSL_STATUS.json`。

其中 30 项 Python 合成契约测试仅是历史偏好示例，不能单独作为真实模拟器或训练放行证据。当前 M3–M6 实现与验证见 [实施检查点](M3_M6_STATUS.md)；正式训练与模型晋升仍未执行。

## 历史工程

旧源码和数据继续保留在 [codex/history-2026-10-01](https://github.com/mtdxmtdx/STS2SuperModel/tree/codex/history-2026-10-01)；没有混入当前 main。
