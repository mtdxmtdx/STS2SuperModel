# STS2 自我对抗式策略迭代构想

更新时间：2026-08-30  
状态：Idea / 基线模型完成后实施  
关联主计划：`PLAN_NOSL.md`  
关联全局决策构想：`GLOBAL_DECISION_ARCHITECTURE_IDEA.md`  
关联标注 GUI：`PLAN_GUI.md`

## 1. 一句话结论

可以让模型进行自我对抗式策略迭代，但 STS2 不是双方对称的 PvP 游戏，因此这里的“自我博弈”不是让两个模型分别控制玩家和敌人，而是让不同版本、不同风险偏好的玩家策略在相同 seed、相同起始状态和相同规则下竞争整局结果，并以真实 CLI、确定性影子模拟器和 Expectimax 作为环境与评审。

第一阶段先训练一个可用的基线模型；基线模型达到离线门禁后，才启动自生成轨迹、旧版本对抗、教师比较和版本晋级循环。基线模型不是最终模型，而是后续策略迭代的起点。

## 2. 为什么适合 STS2

STS2 的主要不确定性来自：

- 抽牌、随机目标和随机效果；
- 敌人行动和战斗结果；
- 地图、事件、奖励、商店和路线；
- 玩家在资源受限下的长期取舍。

单纯依靠一次离线监督训练，容易出现以下问题：

1. 训练数据只覆盖专家访问过的状态；
2. 模型犯错后进入训练集没有覆盖的状态，错误继续累积；
3. 模型只拟合专家动作，不一定学到整局结果与风险权衡；
4. 卡牌、遗物、敌人组合变化后，静态标签泛化不足。

策略迭代可以让模型在大量 seed 上主动产生状态分布，再用旧模型、Expectimax 和真实引擎结果进行筛选。它更接近“策略评估 → 策略改进”的循环，而不是一次性拟合。

## 3. 术语和边界

### 3.1 本项目中的 self-play

本项目把以下过程称为自我对抗式策略迭代：

```text
Policy_t、Policy_(t-1)、Expectimax、启发式策略
    在同一批 seed / 场景上独立运行
    → 比较整局回报和风险
    → 训练 Policy_(t+1)
```

“对抗”指策略版本之间的竞争和回归，不表示敌人由第二个神经网络控制。

### 3.2 不属于本方案的内容

- 不让模型读取未来牌序、未来事件或未访问地图内容；
- 不把 seed 的完整未来展开直接作为模型输入；
- 不把 Save/Load 得到的结果混入 NOSL public observation；
- 不在模拟器尚未通过真实引擎差分前大规模自我生成 Reliable 标签；
- 不用最新模型生成的全部数据替换专家数据和旧 replay buffer；
- 不把人工行为、教师标签和自生成轨迹混成同一来源。

## 4. 总体架构

```text
                 ┌────────────────────────┐
                 │ Public observation      │
                 │ 地图/资源/选项/战斗状态 │
                 └───────────┬────────────┘
                             │
                     action mask
                             │
                 ┌───────────▼────────────┐
                 │ PyTorch Policy/Value/  │
                 │ Risk 模型               │
                 └───────────┬────────────┘
                             │ 候选动作排序
                 ┌───────────▼────────────┐
                 │ 合法性过滤与稳定 ID    │
                 └───────────┬────────────┘
                             │
                 ┌───────────▼────────────┐
                 │ Expectimax 最终验证     │
                 └───────────┬────────────┘
                             │
          ┌──────────────────▼──────────────────┐
          │ DeterministicSimulator / 真实 CLI   │
          └──────────────────┬──────────────────┘
                             │ transition / outcome
                 ┌───────────▼────────────┐
                 │ 轨迹校验与质量分类     │
                 └───────────┬────────────┘
                             │
                 ┌───────────▼────────────┐
                 │ Replay Buffer          │
                 └───────────┬────────────┘
                             │
                 ┌───────────▼────────────┐
                 │ 训练 Policy_(t+1)      │
                 └────────────────────────┘
```

模型在部署时仍采用：

```text
真实合法动作枚举
→ 神经网络排序/估值/风险估计
→ Expectimax 最终验证
→ 执行或回退
```

神经网络不直接绕过动作合法性检查和未知效果回退。

## 5. 两层策略的分工

### 5.1 战斗策略

战斗策略输入当前 `CombatSnapshot` 的 public view，输出：

- 合法动作的 policy logits；
- 动作价值 `Q(a)` 或状态价值 `V(s)`；
- 死亡/高损失风险；
- 置信度和不确定性。

战斗策略由现有 `DeterministicSimulator`、`Expectimax` 和真实 CLI 评估。每个版本可在同一战斗节点上比较：

- 胜率；
- 结束时 HP 和 HP 损失；
- 死亡率；
- 能量和牌堆结果；
- 药水消耗；
- 回合数；
- 与完整 Expectimax 的价值差。

### 5.2 全局策略

全局策略输入当时可见的：

- 地图拓扑和当前可达节点；
- HP、最大 HP、金币、卡组、遗物和药水；
- 奖励、商店、篝火、事件和先古之民选项；
- 战斗模型提供的 `CombatSummary`。

全局策略输出：

- 路线动作；
- 奖励选择或跳过；
- 商店购买、移除或离开；
- 篝火休息、锻造和升级卡牌；
- 事件和先古之民选项。

全局策略不读取战斗逐卡牌隐藏状态。它只接收经过契约化的战斗摘要，例如 `expected_win_probability`、`expected_hp_loss`、`death_probability` 和 `confidence`。

## 6. 第一版基线模型

### 6.1 基线来源

基线训练数据由三部分组成：

1. 已验证的 Expectimax 教师标签；
2. 人工筛选的专家行为数据；
3. 启发式和随机策略生成的覆盖数据。

专家数据用于行为先验，Expectimax 用于动作价值和策略改进，启发式/随机轨迹用于覆盖边界状态。三者必须保留 `source_type` 和 `label_quality`，不能在导出时丢失来源。

### 6.2 基线模型输出

建议使用共享编码器和多个输出头：

```text
Encoder(public_state)
 ├─ Policy head：动作概率/排序
 ├─ Value head：状态或动作价值
 └─ Risk head：死亡率、HP 损失和不确定性
```

动作必须使用稳定的 canonical ID，不能把手牌索引、药水槽位或动态列表位置当作跨进程主键。

### 6.3 基线出口

启动自我迭代前，基线至少满足：

- 训练、验证和 challenge seed 分组无泄漏；
- public view 没有 raw RNG 和未来结果；
- 所有输出动作经过 action mask；
- 真实 CLI 与影子模拟器关键字段差分为零；
- 在 challenge set 上不劣于启发式基线；
- 模型输出可由 PyTorch 与 ONNX 重复得到；
- 低置信度和未知语义可回退 Expectimax。

## 7. 策略迭代循环

### 7.1 生成候选轨迹

每一轮从固定的 seed 池抽取一批训练 seed，并混合以下策略：

```text
当前 champion                  30%
上一稳定版本                    20%
Expectimax / 全局教师           20%
专家行为策略                    20%
受限探索策略                    10%
```

比例是初始建议，写入生成配置并计算配置 hash。所有策略使用相同的游戏版本、CLI protocol、程序集 hash 和语义数据库版本。

探索策略只在合法动作集合内采样，可使用温度、受限 epsilon 或 top-k 随机，不能随机执行未验证动作。

### 7.2 轨迹验证

每条自生成 transition 进入 replay buffer 前，执行：

1. 验证 `pre_state_hash` 与父状态一致；
2. 验证 `selected_action` 在合法动作集合中；
3. 用影子模拟器执行动作；
4. 对关键样本用真实 CLI 重放；
5. 比较 HP、格挡、能量、牌堆、Power、遗物、药水、节点和 RNG 消耗；
6. 检查 public/teacher 字段隔离；
7. 检查版本锁和 seed 分组；
8. 记录失败步骤、恢复信息和 mismatch 字段。

验证不通过的轨迹可以保留为诊断数据，但不能进入 Reliable policy 主集。

### 7.3 计算回报

每个 episode 保存终局和逐节点结果。建议先采用终局主奖励：

```text
通关：高正奖励
死亡：高负奖励
剩余 HP：中等正奖励
精英/Boss 成功：中等正奖励
不必要的 HP 损失：负奖励
路线、金币、牌组和药水：低权重辅助奖励
```

奖励函数必须版本化，例如：

```text
scorer_version
scorer_config_hash
```

更换权重后不能把不同 scorer 的回报直接混作同一标签。

建议使用潜势差分进行有限 reward shaping：

```text
F(s, s') = γ Φ(s') - Φ(s)
```

其中 `Φ` 可以包含生存能力、战斗胜率和路线剩余价值，但不能使用未来不可见信息。

## 8. 训练阶段

### S0：监督 warm start

使用专家和高质量教师数据训练第一版 Policy/Value/Risk。此阶段不做在线自我对抗，先确认输入编码、动作 mask 和损失函数正确。

### S1：离线策略改进

使用已验证的 teacher 和人工轨迹进行 IQL、AWR 或加权行为克隆。Estimated、Synthetic 和 Unknown 数据按较低权重使用。

### S2：受限自生成

让 champion 在训练 seed 上生成轨迹，但保留 Expectimax 最终验证。只将通过质量门禁的高价值轨迹加入 replay buffer。

### S3：版本联赛

同时运行：

- 当前 champion；
- 上一稳定版本；
- Expectimax；
- 启发式基线；
- 探索策略；
- 可选的专家行为策略。

同一 seed 的各版本结果成组保存，避免把不同随机局面误认为策略差异。

### S4：有限在线微调

当 S3 稳定后，再考虑 PPO、A2C 或其他受限在线方法。在线微调必须使用版本锁定的环境和回滚 checkpoint，不能一开始直接在真实游戏中无门槛探索。

## 9. Replay Buffer 设计

每条样本至少包含：

```json
{
  "episode_id": "...",
  "run_context_hash": "...",
  "seed_group": "train",
  "policy_version": "policy-0007",
  "opponent_or_source": "expectimax",
  "pre_public_state": {},
  "legal_actions": [],
  "selected_action": {},
  "post_public_state": {},
  "reward": 0.0,
  "return": 0.0,
  "done": false,
  "quality": "ExactPublic",
  "sl_status": "verified_no_sl",
  "simulator_version": "...",
  "scorer_config_hash": "..."
}
```

推荐按以下维度分层采样：

- 角色；
- Ascension；
- Act/floor；
- 单敌人/多敌人；
- 低 HP/低能量；
- 卡牌、遗物和药水组合；
- 路线、商店、事件、篝火和奖励；
- Easy/Hard/Uncalculable 状态。

不能让最新模型产生的常见简单状态完全占满 replay buffer。

## 10. 版本联赛与晋级门禁

### 10.1 固定评估集

建立不可参与训练的 challenge set：

- 未见 seed；
- 未见 episode；
- 不同角色和 Ascension；
- 常见和罕见卡牌/遗物组合；
- 随机目标、随机弃牌、消耗、虚无和生成卡；
- 低 HP、低能量、濒死和多敌人状态；
- 路线、奖励、商店、篝火和事件分支。

### 10.2 晋级指标

新模型必须同时满足：

1. 通关率不低于 champion 的门槛；
2. 死亡率不增加超过允许阈值；
3. 平均 HP 损失不恶化超过阈值；
4. 与 Expectimax 的 regret 不超过阈值；
5. 合法动作率为 100%；
6. 真实 CLI/影子 mismatch 为 0；
7. 重复运行 hash 一致；
8. public/teacher leakage 为 0；
9. 未知语义状态能正确回退；
10. PyTorch、ONNX 和 C# 推理结果在容差内一致。

### 10.3 晋级记录

每次晋级保存：

```text
model_version
parent_model_version
training_data_manifest_hash
simulator_version
semantic_database_version
scorer_config_hash
challenge_set_hash
metrics
promotion_decision
```

任何门禁失败都保留旧 champion，不覆盖旧模型。

## 11. NOSL 信息边界

### 11.1 模型 public 输入

模型只能看到：

- 当时已经显示的地图；
- 当前节点和合法选项；
- HP、金币、卡组、遗物、药水；
- 已知的敌人意图和战斗状态；
- 当前可见的奖励、商店、篝火和事件选项。

### 11.2 教师/审计信息

教师和审计层可以保存：

- RNG counter；
- 隐藏牌序；
- 未访问节点内容；
- 反事实分支结果；
- 完整模拟轨迹。

这些字段只能用于计算期望价值、审计和差分，不能进入模型的 public feature tensor，也不能写入学生策略的 observation。

### 11.3 seed 的角色

seed 可以用于：

- 固定环境重放；
- 训练/验证分组；
- 对同一局的不同策略进行公平比较；
- 复现 CLI 和模拟器差异。

seed 不作为策略的可见特征。若模型直接从 seed 推断未发生内容，就会产生 SL 风格策略，失去 NOSL 目标。

## 12. 防止自我迭代失控

### 12.1 模拟器漏洞

模型可能学会利用影子模拟器错误，而不是真实游戏规律。解决方式：

- 关键状态定期真实 CLI 重放；
- mismatch 轨迹隔离；
- 语义版本锁定；
- 未验证对象不晋级 Reliable；
- challenge set 保留真实引擎结果。

### 12.2 策略坍缩

如果只用最新模型生成数据，策略会越来越像自己，探索范围变窄。解决方式：

- 保留旧 champion 和专家轨迹；
- 使用固定比例的 Expectimax 和探索数据；
- 按状态类别重采样；
- 定期加入困难和失败轨迹；
- 每轮做未见 seed 评估。

### 12.3 奖励投机

模型可能通过囤积药水、避开精英或拖延战斗取得局部奖励。解决方式：

- 以通关和死亡为主指标；
- 报告 HP、金币、牌组、药水和路线等多维指标；
- 使用风险头和死亡约束；
- 不能只按单一 shaped reward 晋级。

### 12.4 确定性过拟合

固定 seed 有利于复现，但长期只训练固定 seed 会导致记忆路线。解决方式：

- 每轮重新抽取训练 seed；
- 保持隐藏 challenge seed；
- 对同一场景改变可见状态和角色组合；
- 报告 seed 去重和近邻状态覆盖率。

## 13. 战斗模型与全局模型的耦合

全局策略不直接展开所有战斗动作，而是调用战斗模型产生摘要：

```text
全局候选路线/奖励/商店动作
    → 预测后续战斗节点
    → CombatPolicy + Expectimax 估计
    → CombatSummary
    → 全局 Value/Risk 评估
```

当战斗模型版本变化时：

1. 更新 `combat_model_version`；
2. 重新生成受影响的 CombatSummary；
3. 更新全局教师和 manifest；
4. 不把旧战斗摘要和新 scorer 无标记混合。

这样既能让全局模型使用战斗能力，又不会把地图、事件和战斗动作塞进一个不可解释的巨型动作空间。

## 14. 数据和日志产物

每轮策略迭代应产生独立目录：

```text
data/self_play/
  iteration-0000/
    config.json
    rollout.jsonl
    rollout.manifest.json
    replay-index.json
    league-results.json
    quality-report.json
    challenge-results.json
  iteration-0001/
    ...
models/
  policy-0000/
  policy-0001/
```

`config.json` 至少包含：

- seed 分组和数量；
- policy 版本及父版本；
- 对手/来源混合比例；
- simulator、CLI、语义数据库版本；
- scorer 配置 hash；
- 探索参数；
- 训练超参数。

## 15. 资源规划

### 本地机器

`9950X3D + 32GB RAM + 12GB GPU` 足以完成：

- 小规模 rollout；
- 战斗策略基线训练；
- 单机多进程影子模拟；
- 版本联赛原型；
- PyTorch 小型 Policy/Value/Risk 网络。

### 租用服务器的时机

当出现以下需求时再租服务器：

- 同时运行数百或数千个全局 episode；
- 保存大量分支 checkpoint；
- 多版本联赛并行；
- replay buffer 超过本地内存；
- 训练网络需要更大 batch 或更复杂编码器。

数据生成优先增加 x86_64 CPU、RAM 和 NVMe；PyTorch 训练再单独租 NVIDIA GPU。服务器配置不会改变 NOSL 信息边界，也不能弥补模拟器语义错误。

## 16. 分阶段实施计划

### SP0：冻结基线契约

- 冻结 public/teacher observation schema；
- 冻结 stable action ID；
- 冻结 simulator、CLI、semantic database 和 scorer 版本；
- 建立 train/validation/challenge seed 清单。

出口：同一输入的模型和模拟器运行可重复。

### SP1：训练第一版基线

- 整理专家行为和可靠教师数据；
- 训练 Policy/Value/Risk；
- 实现合法动作 mask；
- 建立离线评估和回退路径。

出口：基线在 challenge set 上达到启发式门槛。

### SP2：单步自生成

- 在限定 seed 上运行 champion；
- 记录 transition、回报和质量；
- 用真实 CLI 抽样复核；
- 只将通过门禁的数据写入 replay buffer。

出口：可重复生成一批可追溯自生成轨迹。

### SP3：版本联赛

- 加入上一版本、Expectimax、专家和探索策略；
- 同 seed 进行公平比较；
- 输出 league-results 和 regret 分析；
- 实现 champion 晋级/回滚。

出口：新版本在未见 seed 上稳定优于或不劣于旧版本。

### SP4：战斗—全局联合迭代

- 将 CombatSummary 接入全局 Value/Risk；
- 对路线、奖励、商店、篝火和事件做整局 rollout；
- 生成 global_teacher 与 global_behavior 的分离数据；
- 增加全局风险约束。

出口：全局模型改进不降低战斗安全指标。

### SP5：真实运行部署

- ONNX/PyTorch/C# 输出一致；
- C# 影子模式记录模型/教师差异；
- 低置信度自动回退 Expectimax；
- 版本不匹配时拒绝加载模型。

出口：模型可以在真实 CLI/Mod 环境中以 shadow mode 运行，再逐步开放执行。

## 17. 最小可行实验

第一轮不需要大规模训练，建议：

```text
角色：IRONCLAD、SILENT
Ascension：0、5
训练 seed：100
验证 seed：20
challenge seed：20
每个 seed：1 个 champion + 1 个旧版本 + 1 个 Expectimax 对照
```

记录：

- 通关率；
- 死亡率；
- 平均剩余 HP；
- 平均回合数；
- Expectimax regret；
- CLI mismatch；
- public leakage；
- 重复运行 hash。

实验目的不是马上超过 Expectimax，而是验证“生成—校验—入库—训练—评估—晋级”闭环。

## 18. 最终验收条件

自我对抗式策略迭代阶段完成需要满足：

1. 基线模型已经通过离线输入、动作和版本门禁；
2. 自生成轨迹可由 seed、配置和模型版本重建；
3. 新旧策略在相同 seed 上可公平比较；
4. replay buffer 保留来源、质量和版本元数据；
5. 真实 CLI 与影子模拟器关键差分为零；
6. challenge seed 上没有只对训练 seed 有效的退化；
7. 新模型晋级和失败回滚均可追溯；
8. public observation 没有 SL/teacher 信息泄漏；
9. 战斗模型和全局模型的动作契约保持分离；
10. 模型异常、未知语义和版本不一致会回退到已验证策略或 Expectimax。

## 19. 与当前计划的关系

该构想不替代当前 P0/P1 语义补齐、真实 CLI 差分、教师数据闭环或人工标注 GUI，而是建立在这些基础之上：

```text
P0/P1 语义和差分门禁
        ↓
Expectimax 教师 + 专家行为数据
        ↓
第一版 Policy/Value/Risk 基线
        ↓
自我对抗式策略迭代
        ↓
全局路线/事件/商店/奖励模型
```

在 P1 语义数据库或 scorer 变化后，必须重新生成受影响的 rollout、replay index、manifest 和 challenge 结果。旧迭代数据可以保留用于审计，但不能与新版本无标记混合。
