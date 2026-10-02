"""Small public-entity/set encoder plus candidate-conditional student.

Unknown draw contents are a weighted set with NO position encoding. Public hand,
known draw positions, potion slots, enemy slots, choice slots and history order
remain observable. Hashes below encode ONLY already schema-validated public
history; neither audit IDs nor teacher information is accepted by forward().
"""
from __future__ import annotations

import hashlib
import json
import math

import torch
from torch import nn

from .public_identity import canonical_unknown_draw
from .schema import EVENT_KINDS, validate_public


def public_history_features(event: dict, dim: int) -> list[float]:
    """Fixed, bounded signed feature hash of public event paths/values.

The GRU preserves event order. This intentionally compact, lossy summary is
versioned with the package; it is not a proof that every future mechanic has
sufficient history. Unsupported events/mechanics are rejected by the schema.
"""
    try:
        detail = json.loads(event["detail"])
    except ValueError:
        detail = event["detail"]
    if event["kind"] == "action" and isinstance(detail, dict):
        detail = {k: v for k, v in detail.items() if k != "revision"}
    return feature_hash(detail, event["kind"], dim)


def feature_hash(detail, namespace: str, dim: int) -> list[float]:
    result = [0.0] * dim

    def visit(value, path):
        if isinstance(value, dict):
            for key in sorted(value):
                visit(value[key], path + "." + key)
        elif isinstance(value, list):
            for index, item in enumerate(value):
                visit(item, path + "." + str(index))
        else:
            # Accepted JSON numbers 2 and 2.0 (including -0.0 and 0) have the
            # same meaning. Do not coerce booleans or numeric-looking strings.
            if isinstance(value, float) and math.isfinite(value) and value.is_integer():
                value = int(value)
            token = path + "=" + json.dumps(value, sort_keys=True, separators=(",", ":"))
            digest = hashlib.sha256(token.encode("utf-8")).digest()
            bucket = int.from_bytes(digest[:4], "little") % dim
            result[bucket] += 1.0 if digest[4] % 2 else -1.0
    visit(detail, namespace)
    return result


class Student(nn.Module):
    def __init__(self, config: dict):
        super().__init__()
        self.config = config
        d = config["hidden_dim"]
        tokens = ["empty", "self", "none"]
        for category in ("cards", "enemies", "potions", "relics", "powers", "intents", "keywords", "enchantments", "afflictions", "orbs"):
            tokens += [category + ":" + x for x in config.get("supported_" + category, [])]
        tokens += ["type:" + x for x in ("Attack", "Skill", "Power", "Status", "Curse")]
        tokens += ["action:" + x for x in ("play", "potion", "discard_potion", "choose", "end_turn")]
        tokens += ["event:" + x for x in EVENT_KINDS]
        tokens += ["source:" + x for x in config.get("supported_sources", config["supported_cards"])]
        self.vocab = {token: i for i, token in enumerate(sorted(set(tokens)))}
        self.entity = nn.Embedding(len(self.vocab), d)
        self.position = nn.Embedding(config["max_entities"], d)
        self.card_numeric = nn.Linear(3, d)
        self.enemy_numeric = nn.Linear(3, d)
        self.power_numeric = nn.Linear(1, d)
        self.intent_numeric = nn.Linear(4, d)
        self.hand_encoder = nn.GRU(d, d, batch_first=True)
        self.history_features = nn.Linear(config["history_hash_dim"], d)
        self.public_details = nn.Linear(config["history_hash_dim"], d)
        self.history_encoder = nn.GRU(d, d, batch_first=True)
        self.global_numeric = nn.Sequential(nn.Linear(13, d), nn.ReLU(), nn.Linear(d, d))
        self.state_encoder = nn.Sequential(nn.Linear(13 * d, d), nn.ReLU(), nn.Linear(d, d), nn.ReLU())
        self.choice_numeric = nn.Linear(3, d)
        self.action_numeric = nn.Linear(3, d)
        self.candidate = nn.Sequential(nn.Linear(6 * d, d), nn.ReLU(), nn.Linear(d, d), nn.ReLU())
        self.heads = nn.ModuleDict({name: nn.Linear(d, 1) for name in
                                  ("value", "win_probability", "death_probability", "expected_final_hp", "potion_net_change")})
        self.hp_distribution = nn.Linear(d, config["hp_bins"])

    @property
    def device(self):
        return self.entity.weight.device

    def tensor(self, values):
        return torch.tensor(values, dtype=torch.float32, device=self.device)

    def token(self, name):
        return self.entity(torch.tensor(self.vocab[name], device=self.device))

    def pos(self, index):
        return self.position(torch.tensor(index, device=self.device))

    def pool(self, values):
        return torch.stack(values).sum(dim=0) if values else self.token("empty")

    def details(self, value, namespace):
        return self.public_details(self.tensor(feature_hash(value, namespace, self.config["history_hash_dim"])))

    def card(self, card):
        enriched = self.details({k: card[k] for k in ("details", "enchantments", "affliction", "publicState") if k in card}, "card:" + card["id"])
        return torch.tanh(enriched + self.token("cards:" + card["id"]) + self.token("type:" + card["type"])
                + self.card_numeric(self.tensor([card["upgrade"] / 10, card["cost"] / 10, card["starCost"] / 10]))
                + self.pool([self.token("keywords:" + x) for x in sorted(card["keywords"])]))

    def powers(self, powers):
        return self.pool([self.token("powers:" + x["id"]) + self.power_numeric(self.tensor([x["amount"] / 100])) + self.details(x, "power:" + x["id"])
                          for x in sorted(powers, key=lambda x: (x["id"], x["amount"]))])

    def enemy(self, enemy):
        intents = []
        for intent in enemy["intents"]:
            intents.append(self.token("intents:" + intent["kind"]) + self.intent_numeric(self.tensor([
                (intent["damage"] or 0) / 100, (intent["repeats"] or 0) / 10,
                float(intent["damage"] is not None), float(intent["repeats"] is not None)])))
        return torch.tanh(self.token("enemies:" + enemy["id"]) + self.pos(enemy["slot"])
                + self.enemy_numeric(self.tensor([enemy["hp"] / 100, enemy["maxHp"] / 100, enemy["block"] / 100]))
                + self.powers(enemy["powers"]) + self.pool(intents))

    def ordered(self, values, encoder):
        if not values:
            return self.token("empty")
        _, hidden = encoder(torch.stack(values).unsqueeze(0))
        return hidden[-1, 0]

    def forward(self, public: dict) -> dict[str, torch.Tensor]:
        validate_public(public, self.config)
        obs = public["observation"]
        hand = [torch.tanh(self.card(x) + self.pos(i)) for i, x in enumerate(obs["hand"])]
        choices = [] if obs["choice"] is None else [torch.tanh(self.card(x) + self.pos(i)) for i, x in enumerate(obs["choice"]["candidates"])]
        potions = [torch.tanh(self.token("empty" if x is None else "potions:" + x) + self.pos(i)) for i, x in enumerate(obs["potions"])]
        enemies = {x["slot"]: self.enemy(x) for x in obs["enemies"]}
        pets = {x["slot"]: self.enemy({**x, "intents": []}) for x in (obs.get("pets") or [])}
        targets = {**enemies, **pets}
        if obs["choice"] is not None and obs["choice"].get("bundles") is not None:
            choices = [torch.tanh(choices[i] + self.pool([self.card(c) for c in bundle])) for i, bundle in enumerate(obs["choice"]["bundles"])]
        # Canonical set ordering avoids floating-point order effects. Counts are not hidden positions.
        unknown = canonical_unknown_draw(obs["unknownDraw"])
        unknown_set = self.pool([self.card(x["card"]) * x["count"] for x in unknown])
        known = self.pool([torch.tanh(self.card(x["card"]) + self.pos(x["position"])) for x in sorted(obs["knownDraw"], key=lambda x: x["position"])])
        history = [self.token("event:" + x["kind"]) + self.history_features(self.tensor(public_history_features(x, self.config["history_hash_dim"]))) for x in obs["history"]]
        choice = self.pool(choices)
        if obs["choice"] is not None:
            c = obs["choice"]
            choice = choice + self.token("source:" + c["source"]) + self.choice_numeric(self.tensor([c["min"] / 10, c["max"] / 10, float(c["cancelable"])]))
        global_values = [obs[k] / 100 for k in ("startHp", "hp", "maxHp", "block", "energy", "stars", "turn", "drawCount")]
        global_values += [len(obs[k]) / 100 for k in ("hand", "discard", "exhaust", "potions", "enemies")]
        state = self.state_encoder(torch.cat([
            self.global_numeric(self.tensor(global_values)), self.ordered(hand, self.hand_encoder), unknown_set, known,
            self.pool([self.card(x) for x in sorted(obs["discard"], key=lambda x: json.dumps(x, sort_keys=True))]),
            self.pool([self.card(x) for x in sorted(obs["exhaust"], key=lambda x: json.dumps(x, sort_keys=True))]),
            self.pool(potions), self.pool([self.token("relics:" + x) for x in sorted(obs["relics"])]),
            self.powers(obs["powers"]), self.pool([enemies[i] for i in sorted(enemies)]),
            self.ordered(history, self.history_encoder), choice,
            self.details({k: obs[k] for k in ("counters", "relicStates", "gold", "startGold", "orbCapacity", "orbs", "pets", "unidentifiedDrawCount") if k in obs}, "public-v2")]))
        candidate_vectors = []
        for action in public["candidate_actions"]:
            kind, slot, target = action["kind"], action["slot"], action["target"]
            selected = action["selection"] or []
            entity = hand[slot] if kind == "play" else potions[slot] if kind in ("potion", "discard_potion") else self.token("none")
            target_entity = targets[target] if target >= 0 else self.token("self" if target == -2 else "none")
            selected_entities = self.ordered([choices[i] for i in selected], self.hand_encoder)
            numeric = self.action_numeric(self.tensor([slot / 100, target / 100, len(selected) / 100]))
            candidate_vectors.append(torch.cat([state, self.token("action:" + kind), entity, target_entity, selected_entities, numeric]))
        hidden = self.candidate(torch.stack(candidate_vectors))
        output = {name: head(hidden).squeeze(-1) for name, head in self.heads.items()}
        output["hp_distribution"] = self.hp_distribution(hidden)
        output["legal_mask"] = torch.tensor(public["legal_mask"], dtype=torch.bool, device=self.device)
        # Ranking is one learned objective head; auxiliary heads do not redefine policy.
        output["ranking_score"] = output["value"].masked_fill(~output["legal_mask"], -torch.inf)
        return output
