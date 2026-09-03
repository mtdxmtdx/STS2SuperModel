# A0 已死亡目标的剩余多段命中规则

版本锁：STS2 v0.111.0，commit `41cef1ea`，`sts2.dll` SHA-256 `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`。

固定种子 `a0-target-dead`，遭遇 `SLIMES_WEAK`：

1. `TWIG_SLIME_S:3` 初始 HP 为 7。
2. `STRIKE_IRONCLAD` 造成 6 点伤害，目标剩余 1 HP。
3. 对同一目标使用 `TWIN_STRIKE`（5 点、2 段）。
4. 第一段击杀目标；第二段没有转移到另外两个存活敌人。
5. `LEAF_SLIME_S:1` 保持 15 HP，`LEAF_SLIME_M:2` 保持 33 HP。

结论：指定目标在多段效果结算期间死亡后，剩余段落空；这属于确定性规则，不应产生 `uncalculable_target`。真正缺少 `CombatTargets` RNG 的随机目标仍维持 `uncalculable_random_target`。

证据：

- `data/a0-dead-target-multihit-trace.jsonl`，SHA-256 `A6671EDB4758A4D150A48B3139E4E500BC7B009B086F6A9A7E276F681DEDF4BD`
- `data/a0-dead-target-multihit-diff-report.json`，SHA-256 `9124B822274D7931F5886CF52F26A3BB7D0ED0A7904B38213ED445289650BF92`
- ShadowDiff：`match=true`、`mismatch_count=0`、`confidence=Reliable`
