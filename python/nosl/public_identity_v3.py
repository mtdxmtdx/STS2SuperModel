"""Full-context semantic identity and over-grouping aliases for split isolation.

Aliases intentionally merge context differences. Never use an alias as a label
identity, feature, semantic deduplication key or production admission decision.
"""
from copy import deepcopy
import hashlib
import json

from .public_identity_v2 import (canonical_public_input, legacy_alias_digests as v2_legacy_aliases,
                                public_input_digest as v2_digest)

PUBLIC_IDENTITY_SCHEME = "nosl.public-identity.student-v3.v1"


def public_input_digest(public):
    payload = {"scheme": PUBLIC_IDENTITY_SCHEME, "public_input": canonical_public_input(public)}
    return hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(",", ":"),
                                    ensure_ascii=False, allow_nan=False).encode()).hexdigest()


def legacy_alias_digests(public):
    """Include recursive root/anchor aliases across v1, v2, and v3 identities.

    This deliberately accepts diagnostic/unadmitted public shapes so dropping
    provenance or changing context cannot evade an existing protected component.
    It does not make the source valid or fit-ready.
    """
    aliases = set()
    def strip(value, *, anchor=False):
        result = deepcopy(value)
        if not isinstance(result, dict): return result
        result["schema_version"] = "nosl.student.public.v1" if anchor else "nosl.student.public.v2"
        observation = result.get("observation")
        if isinstance(observation, dict):
            observation.pop("runContext", None)
            if observation.get("schema") == "nosl.public.v3": observation["schema"] = "nosl.public.v2"
        context = result.get("controller_context")
        if isinstance(context, dict) and isinstance(context.get("anchor"), dict):
            context["anchor"] = strip(context["anchor"], anchor=True)
        return result
    def add(value):
        if not isinstance(value, dict): return
        projected = strip(value)
        for candidate in (value, projected):
            for digest in (public_input_digest, v2_digest):
                try: aliases.add(digest(candidate))
                except (ValueError, TypeError, KeyError): pass
            try: aliases.update(v2_legacy_aliases(candidate))
            except (ValueError, TypeError, KeyError): pass
        context = value.get("controller_context")
        if isinstance(context, dict): add(context.get("anchor"))
    add(public)
    return aliases
