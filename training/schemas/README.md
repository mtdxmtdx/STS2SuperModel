# STS2 SuperModel Training Schemas (v1)

本文档定义 Slay the Spire 2 `v0.111.0` P0 阶段核心数据契约与机器校验规范。

## 1. 版本锁定与元数据 (Version Gate)

所有数据集、轨迹文件与 Manifest 必须严格包含以下固定元数据：

- `game_version`: `"v0.111.0"`
- `game_commit`: `"41cef1ea"`
- `assembly_sha256`: `"0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9"`
- `cli_protocol_version`: `"0.2.0"`
- `trace_schema`: `1`
- `schema_version`: `1`

## 2. 模式清单

| 文件 | 用途 | 适用数据 |
| :--- | :--- | :--- |
| `trace-schema-v1.json` | 原始 CLI 动作执行轨迹 | `data/*-trace.jsonl` |
| `public-state-schema-v1.json` | 学生端公共战斗观测状态 | `public_observation` / `public_state` |
| `teacher-state-schema-v1.json` | 教师端特权战斗状态快照 | `teacher_snapshot` |
| `training-decision-record-v1.json` | 归一化训练决策记录 | `data/*-training.jsonl` |
| `dataset-manifest-v1.json` | 版本化数据集清单 | `data/*-manifest.json` |

## 3. 双视图隐私与权限隔离原则 (Privacy Guard)

### 学生视图 (Public View) - 严格禁止泄露：
1. `run_seed`: 严禁包含全局运行种子。
2. `rng_raw_words`: 严禁包含原始 xoshiro256** 状态字（`s0`, `s1`, `s2`, `s3`）。
3. `future_draw_order` / `future draw identities`: 严禁包含完整抽牌堆顺序及未来抽牌身份。
4. `teacher-only` 内部模拟状态与未公开的后继状态。

### 教师视图 (Teacher View) - 当前允许与边界：
1. 允许暴露隐藏抽牌堆/弃牌堆身份 (`draw_pile`, `discard_pile`, `exhaust_pile`)。
2. 允许暴露 7 条 RNG 流的调用计数器 (`rng_counters`)。
3. **关键边界**：`rng_raw_words_exposed` 必须显式保持 `false`（raw RNG words 保持未暴露）。

## 4. 稳定动作身份规范 (Action Candidate Identity)

- 动作候选使用稳定的语义与实例身份：
  - `PlayCard`: 必须具备非空 `source_instance_id`，指向当前手牌中唯一卡牌实例。
  - `UsePotion`: 必须具备非空 `source_instance_id`，指向当前玩家药水栏中唯一实例。
  - `EndTurn`: 不要求 `source_instance_id`。
  - 需要指定目标的动作（如单体攻击/药水）：必须提供 `target_id`。
- **Index 隔离**：`card_index`、`potion_index`、`target_index` 仅作为 CLI 执行层的临时辅助索引，严禁作为训练标签身份。
- **禁止模糊回退**：找不到精确 `source_instance_id` 时，回放层必须在发送指令前抛出错误，严禁按 `model_id` 猜测。

## 5. Schema 演进与向前兼容规则

1. **增量字段**：允许在保持现有字段语义的前提下添加可选字段（`additionalProperties: true`）。
2. **破坏性变更**：任何必填字段删除、类型变更、公共视图隐私放宽，必须升级至 `schema-v2` 并保持旧版本校验器独立。
3. **校验门禁**：所有生成的 JSONL/JSON 产物必须通过 `training/validate_dataset.py` 流式校验，0 错误方可合入。
