> 历史基线记录：本文的11卡等数量属于 d01c5f8e 的原始 M0–M2 检查点。当前上游接入与验证请看 [COVERAGE_BRIDGE](../docs/COVERAGE_BRIDGE.md) 和 coverage_manifest.json；不要把历史数量视为现有目录限制。

# M0 实现审计

基线：mtdxmtdx/sts2-sim a3a66276ee79bf9f75e8af32e9ef5a51592b26f8；规则声明 0.111.0 / 41cef1ea / 222455745；.NET SDK 9.0.303。实际客户端版本 client_version_unverified，不把源码声明当作客户端匹配证明。

| 组件 | 原有证据 | 本轮结论 |
|---|---|---|
| 规则引擎 | CombatEngine、CardModel.CanPlay、PotionCmd.CanUseManually、CombatTargetCandidates | 复用；不在 Python 重写规则 |
| 单场初始化 | RunState、Player.CreateForNewRun、CombatRoom.Enter | 接入 Silent A10；自然 starter 和明确标记的构造局 |
| 子选择 | CardSelectCmd 数量/归属检查；ICardSelectionDecisionSource | TCS 暴露真实 choice，公开组合候选；稳定 clone 和独立 replay 分开 |
| 精确 clone | CombatState.Clone、CombatCloneMap | 原实现共享 room；新增显式 CloneForNosl，分支生命周期独立 |
| 进阶 clone | GenerateMoveStateMachine 在 CombatState 赋值前生成 | 修复上下文赋值顺序；3 类真实 A10 怪物保持公开意图和后续 trace |
| 终局 | CombatRoom.ResolveOutcomeAsync 的一次性任务 | 分支复用完整生命周期；生成奖励并执行 offer hook，领取 0 次 |
| 公共观察 | 原 CombatStateDescription 含审计私有状态，不能直接外发 | 新 DTO 白名单，单独 Contracts assembly；PublicPolicy 无 sim 依赖 |
| 已知牌序 | 原引擎没有持久玩家知识层 | 只读移动/抽牌/洗牌事件驱动；ThinkingAhead 实际选择验证 |
| belief | CloneExact / RNG reseed 各自存在，但没有 combat belief 消费者 | 规范化多重集合后重排未知位置，独立未来流，保持公开意图 |
| 怪物后验 | 原 monster clone 复制完整 memory | 仅支持公开状态确定 memory 的 3 类；其余保留缺口，不做假后验 |
| 策略融合 | 原完整状态接口可被策略读取 | 诊断续策只接受 DecisionPacket；不同假想世界在新信息前不分化 |
| 同进程并发 | 静态 ModelDb、可变 room 和异步选择 | 本轮仅单 worker 顺序；并发未验证 |
| 教师/训练 | 规格包没有真实实现 | M3–M8 未执行，不生成正式标签，不训练 |

## 验证范围

原模拟器基线 build 成功，定向既有测试 45/45。新增真实适配/NOSL 集成 31/31；受核心扩展影响的定向回归 93/93；持久 worker 与独立策略进程的 JSONL 闭环通过。回滚在另一个副本恢复 6 个原始引擎文件并通过原 45 项测试。

基线构建仅有 4 个 NU1900 离线漏洞索引警告，依赖已从现有缓存恢复；后续明确 NuGetAudit=false，不升级或另装依赖。首次集成测试暴露的失败分别修复了 A10 clone 和真实 Draw 路径的知识更新；原始运行日志保留在本地证据归档，不作为仓库源码发布。

## 内容覆盖与未完成项

coverage_manifest.json 保留所有已注册内容作为保守审计全集，并单列 Silent 官方牌池；REGISTERED 不代表通过语义验证或可供正式训练。技术白名单是 M1/M2 起步范围，不缩减 V4 首版全范围要求。

原 README 明示的无效/不完整先古遗物、起始遗物升级、附魔、affliction、自动选牌和胜利事件逐项保留。适配缺口包括潜在怪物记忆、排队额外奖励、bundle 与非手牌选择、附魔和 affliction、通用中途选择采样。原版客户端对拍仍未执行；这些不是本轮 31 个用例能替代的证据。
