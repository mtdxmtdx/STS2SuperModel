# D1 药水语义验证

## 汇总

| 指标 | 前 | 后 |
|---|---:|---:|
| SimulatorSupported | 4 | 28 |
| Reliable eligible | 2 | 28 |
| UnsupportedKnownEffect | 60 | 36 |

28 个确定性药水均通过严格 CLI/ShadowDiff，并通过两轮报告 SHA-256 一致性检查。未支持对象均保留明确技术原因，不猜测随机结果。

## 逐项终态

| Potion | 终态 | 分类 | 原因 |
|---|---|---|---|
| `AMBERGRIS` | UnsupportedKnownEffect | unsupported_power_hook | runtime applies a Power whose listener semantics are not modeled |
| `ASHWATER` | UnsupportedKnownEffect | player_choice_contract | requires a potion-specific card-selection contract before deterministic execution |
| `ATTACK_POTION` | UnsupportedKnownEffect | random_or_hidden_order | depends on a validated chance operator or hidden pile order; no realized result is guessed |
| `BEETLE_JUICE` | UnsupportedKnownEffect | deferred_listener_or_temporary_state | requires a verified listener/temporary-state lifecycle beyond immediate effects |
| `BLESSING_OF_THE_FORGE` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `BLOCK_POTION` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `BLOOD_POTION` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `BONE_BREW` | UnsupportedKnownEffect | unmodeled_resource_subsystem | depends on companion/orb/forge/star state outside the current combat-state contract |
| `BOTTLED_POTENTIAL` | UnsupportedKnownEffect | random_or_hidden_order | depends on a validated chance operator or hidden pile order; no realized result is guessed |
| `CLARITY` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `COLORLESS_POTION` | UnsupportedKnownEffect | random_or_hidden_order | depends on a validated chance operator or hidden pile order; no realized result is guessed |
| `COSMIC_CONCOCTION` | UnsupportedKnownEffect | generated_card_template | requires authoritative generated-card templates and stable instance construction |
| `CUNNING_POTION` | UnsupportedKnownEffect | generated_card_template | requires authoritative generated-card templates and stable instance construction |
| `CURE_ALL` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `DEPRECATED_POTION` | OutOfScope | deterministic | deprecated_or_test_only_registry_entry |
| `DEXTERITY_POTION` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `DISTILLED_CHAOS` | UnsupportedKnownEffect | random_or_hidden_order | depends on a validated chance operator or hidden pile order; no realized result is guessed |
| `DROPLET_OF_PRECOGNITION` | UnsupportedKnownEffect | player_choice_contract | requires a potion-specific card-selection contract before deterministic execution |
| `DUPLICATOR` | UnsupportedKnownEffect | deferred_listener_or_temporary_state | requires a verified listener/temporary-state lifecycle beyond immediate effects |
| `ENERGY_POTION` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `ENTROPIC_BREW` | UnsupportedKnownEffect | random_or_hidden_order | depends on a validated chance operator or hidden pile order; no realized result is guessed |
| `ESSENCE_OF_DARKNESS` | UnsupportedKnownEffect | unmodeled_resource_subsystem | depends on companion/orb/forge/star state outside the current combat-state contract |
| `EXPLOSIVE_AMPOULE` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `FAIRY_IN_A_BOTTLE` | UnsupportedKnownEffect | deferred_listener_or_temporary_state | requires a verified listener/temporary-state lifecycle beyond immediate effects |
| `FIRE_POTION` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `FLEX_POTION` | UnsupportedKnownEffect | deferred_listener_or_temporary_state | requires a verified listener/temporary-state lifecycle beyond immediate effects |
| `FOCUS_POTION` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `FORTIFIER` | UnsupportedKnownEffect | deferred_listener_or_temporary_state | requires a verified listener/temporary-state lifecycle beyond immediate effects |
| `FOUL_POTION` | UnsupportedKnownEffect | multi_domain_targeting | targets players and enemies or a merchant branch; the current target contract cannot represent it |
| `FRUIT_JUICE` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `FYSH_OIL` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `GAMBLERS_BREW` | UnsupportedKnownEffect | player_choice_contract | requires a potion-specific card-selection contract before deterministic execution |
| `GHOST_IN_A_JAR` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `GIGANTIFICATION_POTION` | UnsupportedKnownEffect | deferred_listener_or_temporary_state | requires a verified listener/temporary-state lifecycle beyond immediate effects |
| `GLOWWATER_POTION` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `HEART_OF_IRON` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `KINGS_COURAGE` | UnsupportedKnownEffect | unmodeled_resource_subsystem | depends on companion/orb/forge/star state outside the current combat-state contract |
| `LIQUID_BRONZE` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `LIQUID_MEMORIES` | UnsupportedKnownEffect | player_choice_contract | requires a potion-specific card-selection contract before deterministic execution |
| `LUCKY_TONIC` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `MAZALETHS_GIFT` | UnsupportedKnownEffect | deferred_listener_or_temporary_state | requires a verified listener/temporary-state lifecycle beyond immediate effects |
| `MOCK_DISCARD_AND_ADD_SHIVS_POTION` | OutOfScope | deterministic | deprecated_or_test_only_registry_entry |
| `OROBIC_ACID` | UnsupportedKnownEffect | random_or_hidden_order | depends on a validated chance operator or hidden pile order; no realized result is guessed |
| `POISON_POTION` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `POTION_OF_BINDING` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `POTION_OF_CAPACITY` | UnsupportedKnownEffect | unmodeled_resource_subsystem | depends on companion/orb/forge/star state outside the current combat-state contract |
| `POTION_OF_DOOM` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `POTION_SHAPED_ROCK` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `POT_OF_GHOULS` | UnsupportedKnownEffect | generated_card_template | requires authoritative generated-card templates and stable instance construction |
| `POWDERED_DEMISE` | UnsupportedKnownEffect | deferred_listener_or_temporary_state | requires a verified listener/temporary-state lifecycle beyond immediate effects |
| `POWER_POTION` | UnsupportedKnownEffect | random_or_hidden_order | depends on a validated chance operator or hidden pile order; no realized result is guessed |
| `RADIANT_TINCTURE` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `REGEN_POTION` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `SHACKLING_POTION` | UnsupportedKnownEffect | deferred_listener_or_temporary_state | requires a verified listener/temporary-state lifecycle beyond immediate effects |
| `SHIP_IN_A_BOTTLE` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `SKILL_POTION` | UnsupportedKnownEffect | random_or_hidden_order | depends on a validated chance operator or hidden pile order; no realized result is guessed |
| `SNECKO_OIL` | UnsupportedKnownEffect | random_or_hidden_order | depends on a validated chance operator or hidden pile order; no realized result is guessed |
| `SOLDIERS_STEW` | UnsupportedKnownEffect | deferred_listener_or_temporary_state | requires a verified listener/temporary-state lifecycle beyond immediate effects |
| `SPEED_POTION` | UnsupportedKnownEffect | deferred_listener_or_temporary_state | requires a verified listener/temporary-state lifecycle beyond immediate effects |
| `STABLE_SERUM` | UnsupportedKnownEffect | deferred_listener_or_temporary_state | requires a verified listener/temporary-state lifecycle beyond immediate effects |
| `STAR_POTION` | UnsupportedKnownEffect | unmodeled_resource_subsystem | depends on companion/orb/forge/star state outside the current combat-state contract |
| `STRENGTH_POTION` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `SWIFT_POTION` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `TOUCH_OF_INSANITY` | UnsupportedKnownEffect | player_choice_contract | requires a potion-specific card-selection contract before deterministic execution |
| `VULNERABLE_POTION` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
| `WEAK_POTION` | Reliable | deterministic | strict version-locked CLI ShadowDiff passed |
