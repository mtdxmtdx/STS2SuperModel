"""Versioned v2 semantic identity and conservative legacy split aliases.

Identity is public-only. Aliases are solely for isolation, never deduplication,
features or labels. The legacy implementation remains byte-for-byte unchanged.
"""
from copy import deepcopy
import hashlib
import json

from .public_identity import (canonical_unknown_draw, normalize_numeric_leaves,
                              public_input_digest as legacy_digest)

PUBLIC_IDENTITY_SCHEME = "nosl.public-identity.student-v2.v1"
EXTENSION_EVENTS = {"native_entry_assets", "forced_event_context", "event_merchant_inventory_revealed"}


def _json(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)


def canonical_public_input(public):
    """Normalize recursively, including public anchors and event JSON payloads.

    Only public unknown/permanent decks are unordered. Hand, history, relic
    acquisition, merchant stock, purchased slots and action order stay ordered.
    """
    def visit(value):
        if isinstance(value, list):
            return [visit(item) for item in value]
        if not isinstance(value, dict):
            return normalize_numeric_leaves(value)
        result = {key: visit(item) for key, item in value.items() if key != "revision"}
        if isinstance(result.get("unknownDraw"), list):
            result["unknownDraw"] = canonical_unknown_draw(result["unknownDraw"])
        if isinstance(result.get("knownDraw"), list):
            if any(not isinstance(x, dict) or type(x.get("position")) is not int for x in result["knownDraw"]):
                raise ValueError("known_draw_position_invalid")
            result["knownDraw"] = sorted(result["knownDraw"], key=lambda x: (x["position"], _json(x)))
        if isinstance(result.get("kind"), str) and isinstance(result.get("detail"), str):
            try:
                parsed = visit(json.loads(result["detail"], parse_constant=lambda x: (_ for _ in ()).throw(ValueError(x))))
            except (ValueError, TypeError):
                return result
            if result["kind"] == "native_entry_assets" and isinstance(parsed, dict) and isinstance(parsed.get("deck"), list):
                parsed["deck"] = sorted(parsed["deck"], key=_json)
            result["detail"] = _json(parsed)
        return result
    if not isinstance(public, dict):
        raise ValueError("public_input_not_object")
    return visit(public)


def public_input_digest(public):
    return hashlib.sha256(_json({"scheme": PUBLIC_IDENTITY_SCHEME,
                                "public_input": canonical_public_input(public)}).encode()).hexdigest()


def legacy_alias_digests(public):
    """Retain old identity, context-free projection, and embedded anchor aliases.

    This deliberately over-groups observations whose new context differs. It
    prevents adding/removing extensions or a finite plan from escaping an old
    protected test component, even when source IDs were changed or are invalid.
    """
    result = set()
    def add(value):
        if not isinstance(value, dict): return
        try: result.add(legacy_digest(value))
        except (ValueError, TypeError): pass
        projected = deepcopy(value)
        projected["schema_version"] = "nosl.student.public.v1"
        projected["controller_context"] = {"status": "inactive"}
        obs = projected.get("observation")
        if isinstance(obs, dict) and isinstance(obs.get("history"), list):
            obs["history"] = [event for event in obs["history"]
                              if not isinstance(event, dict) or event.get("kind") not in EXTENSION_EVENTS]
        try: result.add(legacy_digest(projected))
        except (ValueError, TypeError): pass
        context = value.get("controller_context")
        if isinstance(context, dict): add(context.get("anchor"))
    add(public)
    return result
