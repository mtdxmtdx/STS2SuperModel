> 历史基线记录：本文的11卡等数量属于 d01c5f8e 的原始 M0–M2 检查点。当前上游接入与验证请看 [COVERAGE_BRIDGE](../docs/COVERAGE_BRIDGE.md) 和 coverage_manifest.json；不要把历史数量视为现有目录限制。

# M0–M2 实施结果

状态：M0_M1_M2_SUPPORTED_SCOPE。M0 已完成；M1 完成受限技术闭环；M2 完成有限支持机制的公共知识与后验采样。不是全 Silent 内容验收或训练就绪。

## 已实现

- 锁定 commit a3a66276ee79bf9f75e8af32e9ef5a51592b26f8、规则 0.111.0、SDK 9.0.303；从干净 HEAD 创建源码副本，原 reference 与历史工作目录未改动。
- 真实 Silent A10 初始化、完整合法动作封装、手牌/目标/药水/结束回合、真实异步手牌子选择与独立 replay。
- 显式 CloneForNosl：CurrentRoom/BaseRoom 改为分支 room；结算任务与 reward 容器独立。保留原 Clone 的共享 room API 语义。
- 修复 A10 clone 招式图生成顺序：先设置 CombatState，再 GenerateMoveStateMachine，保留公开进阶伤害。
- 完整自动结算后读取 HP/最大HP/药水及清理前公开事实；奖励已生成但领取次数为 0；重复结算不重触发回血。
- 公共 DTO 与独立公共策略进程；牌序只外发未知多重集合与已知位置，不含真实 seed、move state、私有指纹或教师元数据。
- 抽牌/洗牌/公开放回牌顶驱动知识更新；采样先规范化未知牌，再重排未知位置并独立重建未来随机流。
- 三类支持怪物的后验依赖公开意图/历史，其他潜在记忆明确阻塞；小牌序精确概率与采样频率相符。

changed_branch/field：CloneForNosl 的 CurrentRoom/BaseRoom、A10 monster clone CombatState 初始化顺序、PublicObservation 白名单、UnknownDraw/KnownDraw 条件采样。

## 证据

原模拟器基线：build 成功，45/45 相关测试。新增集成：31/31；核心定向回归：93/93。根诊断全部候选在两个独立 world 下完整结算，隐藏替换前后的终局分布逐项相同。独立 worker＋公共策略 JSONL 闭环：2 次决策获胜、终局 70 HP、奖励选择 0 次。回滚副本：6 个原始引擎文件恢复，原 45 项测试通过；当前实现保持 M012_IMPLEMENTED。

## M0 审计资料

- [基线锁](../configs/baseline.lock.json)
- [环境报告](../configs/environment_report.json)
- [实现审计](../configs/implementation_audit.md)
- [覆盖清单](../configs/coverage_manifest.json)
- [逐项验收进度](../configs/acceptance_progress.json)
- [使用与复现](DEVELOPMENT.md)
- [源码入口](../src/Nosl.Worker/CombatSession.cs)

覆盖清单包含 1428 个注册模型及 91 个 Silent 牌池条目，注册不代表已验证。当前技术白名单：11 类卡牌、5 类药水、3 类遗物、3 类真实怪物。构造局和自然 starter 分开声明。

## 仍保留的边界

实际客户端版本与原版对拍未核对；全内容、复杂遗物/附魔、其他怪物的潜在记忆、额外奖励队列、非手牌和 bundle 选择、通用中途选择采样仍待后续阶段。M2 只做隔离诊断，不冒称 M4 完整教师；READY_FOR_TRAINING=false。实施阶段没有 M3–M8、正式数据生产或训练；本次整理把已实现的 M0–M2 起步范围发布到 main，不把发布动作当作新的技术验收。


## 发布组织

源码和原始实施目录保持不变；公开工程使用相对路径，隔离规则引擎、公共契约、worker 与公共诊断策略。原始日志、构建产物、回滚副本和本机绝对路径不入库。V4 原始规格见 `spec/v4/`，其中历史交付状态不代表当前工程状态。
