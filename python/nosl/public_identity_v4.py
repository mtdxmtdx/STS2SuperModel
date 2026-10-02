"""Full v4 semantic conditioning identity and conservative split-only aliases."""
from copy import deepcopy
import hashlib
import json

from .public_identity_v3 import (canonical_public_input as canonical_v3,
                                 legacy_alias_digests as v3_aliases,
                                 public_input_digest as v3_digest)

PUBLIC_IDENTITY_SCHEME = "nosl.public-identity.student-v4.v1"


def _json(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)


def canonical_public_input(public):
    """Retain the entire typed history, including unselected offers and gaps.

    Transport action revisions have no semantic meaning. Permanent deck order
    is unobserved; every offer/selection/event/map-list order remains explicit.
    """
    result = canonical_v3(public)
    def visit(value):
        if isinstance(value, list):
            for item in value: visit(item)
        elif isinstance(value, dict):
            if set(value) == {"hp", "maxHp", "gold", "deck", "relics", "potions", "maxEnergy", "potionSlots", "orbSlots", "cardRemovalsUsed"}:
                value["deck"] = sorted(value["deck"], key=_json)
            for item in value.values(): visit(item)
    visit(result)
    return result


def public_input_digest(public):
    return hashlib.sha256(_json({"scheme": PUBLIC_IDENTITY_SCHEME,
                                "public_input": canonical_public_input(public)}).encode()).hexdigest()


def legacy_alias_digests(public):
    """Over-group evidence-stripped roots/anchors solely for split isolation."""
    aliases = set()
    def strip(value):
        result = deepcopy(value)
        if not isinstance(result, dict): return result
        result.pop("public_evidence", None)
        result["schema_version"] = "nosl.student.public.v3"
        context = result.get("controller_context")
        if isinstance(context, dict) and isinstance(context.get("anchor"), dict): context["anchor"] = strip(context["anchor"])
        return result
    def add(value):
        if not isinstance(value, dict): return
        for candidate in (value, strip(value)):
            for digest in (public_input_digest, v3_digest):
                try: aliases.add(digest(candidate))
                except (ValueError, TypeError, KeyError): pass
            try: aliases.update(v3_aliases(candidate))
            except (ValueError, TypeError, KeyError): pass
        context = value.get("controller_context")
        if isinstance(context, dict): add(context.get("anchor"))
    add(public)
    return aliases
