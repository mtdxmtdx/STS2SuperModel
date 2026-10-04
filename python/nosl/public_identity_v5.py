"""Full v5 semantic identity. Graphs are canonical by validation, never hashed features."""
import hashlib
import json

from .public_identity_v4 import canonical_public_input, legacy_alias_digests as v4_aliases
from .schema_v5 import _v4_mechanics_projection

PUBLIC_IDENTITY_SCHEME = "nosl.public-identity.student-v5.v1"


def public_input_digest(public):
    return hashlib.sha256(json.dumps({"scheme": PUBLIC_IDENTITY_SCHEME,
        "public_input": canonical_public_input(public)}, sort_keys=True, separators=(",", ":"),
        ensure_ascii=False, allow_nan=False).encode()).hexdigest()


def legacy_alias_digests(public):
    """Conservative split isolation only, never supervision, admission or features."""
    aliases = set()
    def add(value):
        if not isinstance(value, dict): return
        try: aliases.add(public_input_digest(value))
        except (ValueError, TypeError, KeyError): pass
        try: aliases.update(v4_aliases(_v4_mechanics_projection(value)))
        except (ValueError, TypeError, KeyError, AttributeError): pass
        context = value.get("controller_context")
        if isinstance(context, dict): add(context.get("anchor"))
    add(public)
    return aliases
