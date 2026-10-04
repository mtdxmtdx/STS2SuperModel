"""Versioned, public-only semantic identity for split isolation and deduplication.

This is not a validator, model feature, config hash, private-state hash or label.
It canonicalizes representation equivalences already established by the public
contract. Meaningful sequences and categorical strings retain their identities.
"""
from __future__ import annotations

from copy import deepcopy
import hashlib
import json
import math

PUBLIC_IDENTITY_SCHEME = "nosl.public-identity.v2"


def normalize_numeric_leaves(value):
    """Keep booleans/strings distinct; normalize finite integral JSON numbers."""
    if isinstance(value, float):
        if not math.isfinite(value):
            raise ValueError("nonfinite_public_number")
        return int(value) if value.is_integer() else value
    if isinstance(value, list):
        return [normalize_numeric_leaves(item) for item in value]
    if isinstance(value, dict):
        return {key: normalize_numeric_leaves(item) for key, item in value.items()}
    return value


def _json(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)


def canonical_unknown_draw(entries):
    """A card multiset: order and partitioning identical-card counts are immaterial."""
    groups = {}
    for entry in entries:
        if not isinstance(entry, dict) or set(entry) != {"card", "count"}:
            raise ValueError("unknown_draw_entry_shape")
        card, count = normalize_numeric_leaves(entry["card"]), normalize_numeric_leaves(entry["count"])
        if type(count) is not int or count <= 0:
            raise ValueError("unknown_draw_count_invalid")
        key = _json(card)
        if key not in groups:
            groups[key] = {"card": card, "count": 0}
        groups[key]["count"] += count
    return [groups[key] for key in sorted(groups)]


def canonical_public_input(public_input):
    """Return a fresh canonical public object without touching supplied data."""
    result = normalize_numeric_leaves(deepcopy(public_input))
    if not isinstance(result, dict):
        raise ValueError("public_input_not_object")
    # Candidate revisions are anti-stale execution tokens, not policy information.
    # Original execution payloads are untouched; only this fresh identity copy changes.
    for action in result.get("candidate_actions", []):
        if isinstance(action, dict):
            action.pop("revision", None)
    observation = result.get("observation")
    if not isinstance(observation, dict):
        return result
    if isinstance(observation.get("unknownDraw"), list):
        observation["unknownDraw"] = canonical_unknown_draw(observation["unknownDraw"])
    if isinstance(observation.get("knownDraw"), list):
        known = observation["knownDraw"]
        if any(not isinstance(item, dict) or type(item.get("position")) is not int for item in known):
            raise ValueError("known_draw_position_invalid")
        # Preserve card-to-position identity; only container order is normalized.
        observation["knownDraw"] = sorted(known, key=lambda item: (item["position"], _json(item)))
    for event in observation.get("history", []):
        if not isinstance(event, dict) or not isinstance(event.get("detail"), str):
            continue
        try:
            parsed = json.loads(event["detail"], parse_constant=lambda _: (_ for _ in ()).throw(ValueError("nonfinite_detail")))
            parsed = normalize_numeric_leaves(parsed)
        except (ValueError, TypeError):
            continue
        # The public model explicitly omits this anti-stale transport token.
        # Actual action choices and history sequence remain in the identity.
        if event.get("kind") == "action" and isinstance(parsed, dict):
            parsed.pop("revision", None)
        event["detail"] = _json(parsed)
    return result


def public_input_digest(public_input):
    payload = {"scheme": PUBLIC_IDENTITY_SCHEME, "public_input": canonical_public_input(public_input)}
    return hashlib.sha256(_json(payload).encode("utf-8")).hexdigest()
