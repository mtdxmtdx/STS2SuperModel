from __future__ import annotations

import json
from pathlib import Path

import pytest

from training.global_decision.cli_global_adapter import adapt_cli_decision
from training.global_decision.contracts import (
    GlobalActionCandidate,
    GlobalOfferSnapshot,
    GlobalRunStatePublic,
    PublicStateLeakageError,
    stable_hash,
    validate_public_payload,
)


ROOT = Path(__file__).resolve().parent
SCHEMA_ROOT = ROOT / "schemas" / "global"


def test_contract_roundtrip_and_schema_files_exist() -> None:
    candidate = GlobalActionCandidate(
        action_id="reward:skip",
        action_type="reward_skip",
        semantic_id=None,
        transport_action="skip_card_reward",
        candidate_role="skip",
    )
    offer = GlobalOfferSnapshot(
        offer_snapshot_hash=stable_hash({"offer": "x"}),
        decision_type="card_reward",
        candidates=(candidate,),
        candidate_order=("reward:skip",),
        visible_context_hash=stable_hash({"act": 1}),
        legal_actions_complete=True,
        source="synthetic",
    )
    assert GlobalActionCandidate(**candidate.to_dict()).to_dict() == candidate.to_dict()
    assert offer.to_dict()["candidates"][0]["action_id"] == "reward:skip"
    assert GlobalOfferSnapshot.from_dict(offer.to_dict()).to_dict() == offer.to_dict()
    assert sorted(p.name for p in SCHEMA_ROOT.glob("*.json")) == [
        "global-action-candidate-v1.json",
        "global-dataset-manifest-v1.json",
        "global-deck-evaluation-record-v1.json",
        "global-offer-snapshot-v1.json",
        "global-public-state-v1.json",
        "global-run-key-v1.json",
    ]


def test_public_leakage_rejected_recursively() -> None:
    with pytest.raises(PublicStateLeakageError):
        validate_public_payload({"visible": {"nested": {"future_draw_order": ["BASH"]}}})


def test_cli_card_reward_is_complete_only_with_offer_ids() -> None:
    complete = adapt_cli_decision(
        {
            "decision": "card_reward",
            "context": {"character": "Ironclad", "act": 1, "floor": 4, "room_type": "combat"},
            "cards": [
                {"index": 0, "id": "BASH", "offer_id": "offer-a"},
                {"index": 1, "id": "POMMEL_STRIKE", "offer_id": "offer-b"},
            ],
            "can_skip": True,
                "player": {"hp": 60, "max_hp": 75, "gold": 100, "deck": [], "relics": [], "potions": []},
        }
    )
    assert complete["legal_actions_complete"] is True
    assert complete["missing_fields"] == []
    assert complete["offer_snapshot"]["candidates"][-1]["action_id"] == "reward:skip"

    incomplete = adapt_cli_decision(
        {
            "decision": "shop",
            "context": {},
            "player": {},
            "cards": [{"index": 0, "name": "localized only"}],
        }
    )
    assert incomplete["legal_actions_complete"] is False
    assert "cards[0].id" in incomplete["missing_fields"]
    assert "cards[0].offer_id" in incomplete["missing_fields"]
    assert "player.max_hp" in incomplete["missing_fields"]
    assert "player.deck" in incomplete["missing_fields"]


def test_cli_proceed_and_leave_have_stable_actions() -> None:
    for decision, expected in (("proceed", "proceed"), ("leave", "leave")):
        result = adapt_cli_decision(
            {
                "decision": decision,
                "context": {"character": "Ironclad", "act": 1, "floor": 4},
                "player": {"hp": 60, "max_hp": 75, "gold": 100, "deck": [], "relics": [], "potions": []},
            }
        )
        assert result["legal_actions_complete"] is True
        assert result["offer_snapshot"]["candidates"][0]["action_id"] == expected
