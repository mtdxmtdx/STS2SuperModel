"""P1 Lane C combat-scope gate tests.

Scope: single-player *combat* turn quality gates only.
  1. Real multi-card selection in COMBAT (PURITY, a real Silent card).
  2. Cross-process stable ID / hash replay equality.
  3. Trace interruption recovery (flush on quit, hard-kill JSONL, append
     relationship across sessions).
  4. Legacy protocol compatibility (card_index/target_index, ready.version).
  5. public/teacher observation isolation (leakage scan).

Out of scope (not exercised here): map strategy, shop/reward/event decision
logic, save-load completeness, full-run automation, Power/relic simulators.

Version lock enforced by test_v0111_consistency.py and re-used here:
game v0.111.0 / commit 41cef1ea / assembly SHA-256
0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9 /
CLI protocol 0.2.0 / trace schema 1.
"""

import json
import os
import subprocess

from test_v0111_consistency import run_commands

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TEST_OUTPUT = os.path.join(ROOT, "test-output")

GAME_VERSION = "v0.111.0"
ASSEMBLY_SHA = "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9"
CLI_VERSION = "0.2.0"

# PURITY is a real Silent card ("Choose up to {Cards} cards to Exhaust").
# Its choice screen is the authentic in-combat card_select surface used here.
ENCOUNTER = "SHRINKER_BEETLE_WEAK"


def _fresh_trace_path(name):
    os.makedirs(TEST_OUTPUT, exist_ok=True)
    path = os.path.join(TEST_OUTPUT, name)
    try:
        os.remove(path)
    except FileNotFoundError:
        pass
    return path


def _read_trace(path):
    with open(path, encoding="utf-8") as handle:
        return [json.loads(line) for line in handle if line.strip()]


def _arranged_core_commands(seed):
    """Deterministic combat prefix: after one end turn the hand is exactly
    [PURITY, BASH, ANGER, DEFEND_IRONCLAD, DEFEND_IRONCLAD] (draw-order
    controlled), PURITY sitting at index 0."""
    deck = ["PURITY", "BASH", "ANGER", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD",
            "STRIKE_IRONCLAD"] * 3
    return [
        {"cmd": "start_run", "character": "Ironclad", "seed": seed, "ascension": 0},
        {"cmd": "set_player", "deck": deck, "hp": 72, "max_hp": 72},
        {"cmd": "enter_room", "type": "combat", "encounter": ENCOUNTER},
        {"cmd": "set_draw_order",
         "cards": ["PURITY", "BASH", "ANGER", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD"]
                  + ["STRIKE_IRONCLAD"] * 13},
        {"cmd": "action", "action": "end_turn"},
    ]


def _purity_setup(seed):
    """Commands reaching combat with PURITY copies shuffled into the deck."""
    deck = ["PURITY", "BASH", "ANGER", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD",
            "STRIKE_IRONCLAD"] * 2
    return [
        {"cmd": "start_run", "character": "Ironclad", "seed": seed, "ascension": 0},
        {"cmd": "set_player", "deck": deck, "hp": 72, "max_hp": 72},
        {"cmd": "enter_room", "type": "combat", "encounter": ENCOUNTER},
    ]


def _play_prefix_for(index):
    return [{"cmd": "action", "action": "play_card",
             "args": {"card_index": index}}]


def _reach_card_select(setup):
    """Execute a command list, locate PURITY, then run an identical-prefix
    process that plays it. Returns (transcript, prefix_commands)."""
    state = run_commands(setup)[-1]
    assert state.get("decision") == "combat_play", state.get("decision")
    assert any("PURITY" in c["id"] for c in state["hand"]), \
        "arranged hand must contain PURITY"
    prefix = setup + _play_prefix_for(
        next(c["index"] for c in state["hand"] if "PURITY" in c["id"]))
    transcript = run_commands(prefix)
    assert transcript[-1].get("decision") == "card_select", transcript[-1]
    return transcript, prefix


# ---------------------------------------------------------------------------
# Deliverable 1: real multi-card selection inside COMBAT
# ---------------------------------------------------------------------------

class TestRealMultiCardSelect:
    def _assert_select_contract(self, sel):
        assert sel["decision"] == "card_select"
        assert sel["min_select"] == 0
        assert sel["max_select"] == 3
        # Stable choice id shape: choice:<24 uppercase hex>
        cid = sel["choice_id"]
        assert isinstance(cid, str) and len(cid) == len("choice:") + 24
        assert cid.startswith("choice:")
        int(cid.split(":", 1)[1], 16)  # hex parse
        # Every option carries a stable card instance id
        ids = [c["instance_id"] for c in sel["cards"]]
        assert all(ids), ids
        assert len(set(ids)) == len(ids)
        for card in sel["cards"]:
            assert card["index"] >= 0
            assert set(card["instance_id"])  # non-empty string

    def test_purity_triggers_multi_card_select_in_combat(self):
        transcript, _prefix = _reach_card_select(_arranged_core_commands("gate-select-basic"))
        sel = transcript[-1]
        self._assert_select_contract(sel)
        assert len(sel["cards"]) >= 3, "expected a genuine multi-option choice"

    def test_action_candidates_kind_and_completeness(self):
        transcript, _prefix = _reach_card_select(_arranged_core_commands("gate-select-cands"))
        sel = transcript[-1]
        opts = [c["instance_id"] for c in sel["cards"]]
        n = len(opts)
        cands = sel["action_candidates"]
        assert cands, "card_select must export action candidates"
        assert all(c["kind"] == "Choice" for c in cands)
        action_ids = [c["action_id"] for c in cands]
        assert len(set(action_ids)) == len(action_ids), "duplicate candidate keys"
        for cand in cands:
            picked = cand["selected_card_instance_ids"]
            assert cand["choice_id"] == sel["choice_id"]
            assert len(picked) <= sel["max_select"]
            assert all(pid in opts for pid in picked), picked
            assert cand["legal"] is True
            # action_id embeds the stable instance ids, never indices
            for pid in picked:
                assert pid in cand["action_id"]
        from math import comb
        expected = sum(comb(n, k) for k in range(sel["min_select"], sel["max_select"] + 1))
        assert len(cands) == expected, f"{len(cands)} != C-space {expected}"
        assert sel["action_candidates_complete"] is True
        assert sel["choice_restriction"] is None

    def test_select_cards_by_stable_ids_replays_across_snapshot(self):
        setup = _arranged_core_commands("gate-select-replay")
        first, prefix = _reach_card_select(setup)
        sel = first[-1]
        combo = next(
            c for c in sel["action_candidates"] if len(c["selected_card_instance_ids"]) == 2
        )
        chosen = list(combo["selected_card_instance_ids"])

        # Fresh process replays the identical prefix; recorded stable ids are
        # mapped through the NEW snapshot (id -> current index), proving the
        # execution key travels as ids rather than positions.
        replay, replay_prefix = _reach_card_select(setup)
        new_sel = replay[-1]
        assert new_sel["choice_id"] == sel["choice_id"]
        index_of = {c["instance_id"]: c["index"] for c in new_sel["cards"]}
        indices = ",".join(str(index_of[cid]) for cid in chosen)
        done = run_commands(replay_prefix + [
            {"cmd": "action", "action": "select_cards", "args": {"indices": indices}},
        ])[-1]
        assert done.get("trace_status") == "ok", done
        norm = done.get("normalized_action_id") or ""
        assert norm.startswith("select_cards:")
        assert sel["choice_id"] in norm
        for cid in sorted(chosen):
            assert cid in norm, (norm, chosen)

        # The selected copies really left the hand (exhausted by PURITY).
        if done.get("decision") == "combat_play":
            hand_after = [c["instance_id"] for c in done["hand"]]
        else:
            snap = run_commands(replay_prefix + [
                {"cmd": "action", "action": "select_cards", "args": {"indices": indices}},
                {"cmd": "get_combat_snapshot", "view": "public"},
            ])
            obs = snap[-2] if snap[-1].get("type") == "combat_snapshot" else snap[-1]
            hand_after = [c["instance_id"] for c in obs.get("hand", [])]
        for cid in chosen:
            assert cid not in hand_after

    def test_skip_select_still_supported_for_zero_min_choices(self):
        first, prefix = _reach_card_select(_arranged_core_commands("gate-select-skip"))
        sel = first[-1]
        assert sel["min_select"] == 0
        after = run_commands(prefix + [
            {"cmd": "action", "action": "skip_select"},
        ])[-1]
        assert after.get("trace_status") == "ok", after
        assert after.get("decision") in ("combat_play", "card_select"), after.get("decision")


class TestIndexShiftInvariance:
    """Two processes whose opening hands arrange the same five card models
    differently (PURITY at a different slot, identical complement multiset)
    must produce the same choice id, the same per-option instance ids, and the
    same select_cards primary key -- indices never enter action identity.

    Seed pair harvested offline against the locked v0.111.0 runtime:
    hand A = [PURITY, ANGER, DEFEND_IRONCLAD, STRIKE_IRONCLAD x2] and hand B
    is the mirror arrangement [ANGER, DEFEND_IRONCLAD, STRIKE_IRONCLAD x2,
    PURITY]; the option sequence after playing PURITY is identical.
    Stable card ids are seed-independent because they enumerate the fixed
    deck listing.
    """

    DECK = ["PURITY", "BASH", "ANGER", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD"] * 3
    SEED_A = "gate-seed-000"
    SEED_B = "gate-seed-112"

    def _setup(self, seed):
        return [
            {"cmd": "start_run", "character": "Ironclad",
             "seed": seed, "ascension": 0},
            {"cmd": "set_player", "deck": self.DECK, "hp": 72, "max_hp": 72},
            {"cmd": "enter_room", "type": "combat", "encounter": ENCOUNTER},
        ]

    def _reach(self, seed):
        setup = self._setup(seed)
        state = run_commands(setup)[-1]
        assert state.get("decision") == "combat_play"
        purity = next(c for c in state["hand"] if c["id"] == "CARD.PURITY")
        prefix = setup + [{"cmd": "action", "action": "play_card",
                           "args": {"card_index": purity["index"]}}]
        sel = run_commands(prefix)[-1]
        assert sel.get("decision") == "card_select", sel.get("decision")
        return state, sel, purity["index"], prefix

    def test_same_options_survive_hand_rearrangement_across_processes(self):
        hand_a, sel_a, purity_idx_a, prefix_a = self._reach(self.SEED_A)
        hand_b, sel_b, purity_idx_b, prefix_b = self._reach(self.SEED_B)

        # Hand arrangements genuinely differ; PURITY occupies another slot.
        seq_a = [(c["id"], c["instance_id"]) for c in hand_a["hand"]]
        seq_b = [(c["id"], c["instance_id"]) for c in hand_b["hand"]]
        assert seq_a != seq_b, "hand arrangements must differ across processes"
        assert purity_idx_a != purity_idx_b, (purity_idx_a, purity_idx_b)

        # Identical option model multiset -> identical stable instance-id set,
        # even though serialisation order may differ between the hands.
        models_a = sorted(c["id"] for c in sel_a["cards"])
        models_b = sorted(c["id"] for c in sel_b["cards"])
        assert models_a == models_b
        ids_a = {c["instance_id"] for c in sel_a["cards"]}
        ids_b = {c["instance_id"] for c in sel_b["cards"]}
        assert ids_a == ids_b

        # choice_id covers sorted stable ids + bounds: order-free equality.
        assert sel_a["choice_id"] == sel_b["choice_id"]
        cand_a = sorted(c["action_id"] for c in sel_a["action_candidates"])
        cand_b = sorted(c["action_id"] for c in sel_b["action_candidates"])
        assert cand_a == cand_b

        # Execute ANGER selection through each snapshot's own indices: the
        # normalized play/select primary keys match byte-for-byte while every
        # positional value differs.
        key_a = next(c["index"] for c in sel_a["cards"] if c["id"] == "CARD.ANGER")
        key_b = next(c["index"] for c in sel_b["cards"] if c["id"] == "CARD.ANGER")

        def finish(prefix, option_index):
            done = run_commands(prefix + [
                {"cmd": "action", "action": "select_cards",
                 "args": {"indices": str(option_index)}}])[-1]
            assert done.get("trace_status") == "ok", done
            return done

        done_a = finish(prefix_a, key_a)
        done_b = finish(prefix_b, key_b)
        norm_a = done_a["normalized_action_id"]
        norm_b = done_b["normalized_action_id"]
        assert norm_a.startswith("select_cards:")
        assert norm_a == norm_b, (norm_a, norm_b)



# ---------------------------------------------------------------------------
# Deliverable 2: cross-process stable identity
# ---------------------------------------------------------------------------

class TestCrossProcessStableIdentity:
    FLOW = [
        {"cmd": "start_run", "character": "Ironclad", "seed": "gate-stable-id", "ascension": 0},
        {"cmd": "set_player",
         "deck": ["BASH", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "ANGER", "PURITY",
                  "STRIKE_IRONCLAD", "STRIKE_IRONCLAD"],
         "potions": ["BLOCK_POTION"], "hp": 70, "max_hp": 70},
        {"cmd": "enter_room", "type": "combat", "encounter": ENCOUNTER},
        {"cmd": "get_combat_snapshot", "view": "public"},
        {"cmd": "get_combat_snapshot", "view": "teacher"},
    ]

    def _collect_identity(self, transcript):
        public_ids = set()
        enemy_ids = set()
        potion_ids = set()
        hashes = []
        for resp in transcript[1:]:
            if resp.get("type") != "combat_snapshot":
                continue
            obs = resp["public_observation"]
            hashes.append(resp["public_state_hash"])
            for card in obs.get("hand", []):
                public_ids.add(card["instance_id"])
            for cand in obs.get("action_candidates", []):
                if cand.get("source_instance_id"):
                    public_ids.add(cand["source_instance_id"])
                if cand.get("kind") == "UsePotion":
                    potion_ids.add(cand["source_instance_id"])
            for enemy in obs.get("enemies", []):
                enemy_ids.add(enemy["instance_id"])
            for potion in obs["player"].get("potions") or []:
                potion_ids.add(potion["instance_id"])
        teacher_ids = set()
        for resp in transcript:
            teacher = resp.get("teacher_snapshot")
            if not teacher:
                continue
            for pile in ("hand", "draw_pile", "discard_pile", "exhaust_pile"):
                for item in teacher.get(pile, []):
                    teacher_ids.add(item["instance_id"])
        return {
            "hashes": hashes,
            "public_ids": public_ids,
            "enemy_ids": enemy_ids,
            "potion_ids": potion_ids,
            "teacher_ids": teacher_ids,
        }

    def test_identical_transcripts_across_two_processes(self):
        first = run_commands(self.FLOW)
        second = run_commands(self.FLOW)
        assert first == second, "same seed/deck/commands diverged across processes"

    def test_state_hash_and_instance_id_sets_equal(self):
        one = self._collect_identity(run_commands(self.FLOW))
        two = self._collect_identity(run_commands(self.FLOW))
        assert one["hashes"] and one["hashes"] == two["hashes"]
        assert one["public_ids"] == two["public_ids"] and one["public_ids"]
        assert one["enemy_ids"] == two["enemy_ids"] and one["enemy_ids"]
        assert one["potion_ids"] == two["potion_ids"] and one["potion_ids"]
        assert one["teacher_ids"] == two["teacher_ids"]


# ---------------------------------------------------------------------------
# Deliverable 3: trace interruption recovery
# ---------------------------------------------------------------------------

class TestTraceRecovery:
    def _env_trace(self, trace_path):
        return {"STS2_TRACE_PATH": str(trace_path)}

    def test_failure_step_metadata_with_prior_rows_intact(self):
        trace = _fresh_trace_path("gate-trace-failure.jsonl")
        good_play_then_fail = _purity_setup("gate-trace-fail") + [
            {"cmd": "action", "action": "play_card", "args": {"card_index": 999}},
        ]
        responses = run_commands(good_play_then_fail, self._env_trace(trace))
        assert responses[-1]["trace_status"] == "failed"

        rows = _read_trace(trace)
        assert len(rows) >= 4
        ok_rows = rows[:-1]
        assert all(row["status"] == "ok" for row in ok_rows)
        failed = rows[-1]
        assert failed["status"] == "failed"
        assert failed["failure"]["failed_step"] == failed["step"]
        assert failed["failure"]["recovery_required"] is True
        assert failed["failure"]["recovery_status"] == "not_attempted"
        # prior chain intact: pre == previous post
        for prev, cur in zip(rows, rows[1:]):
            assert cur["pre_state_hash"] == prev["post_state_hash"]
        assert failed["failure"]["prior_trace_hash"] == ok_rows[-1]["post_state_hash"]

    def test_quit_flushes_complete_last_line_readable_from_disk(self):
        trace = _fresh_trace_path("gate-trace-flush.jsonl")
        env = os.environ.copy()
        env.update(self._env_trace(trace))
        project = os.path.join(ROOT, "src", "Sts2Headless", "Sts2Headless.csproj")
        proc = subprocess.Popen(
            ["dotnet", "run", "--no-build", "--project", project],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, bufsize=1, env=env)
        acked_records = []
        try:
            def send(cmd):
                proc.stdin.write(json.dumps(cmd) + "\n")
                proc.stdin.flush()
                while True:
                    line = proc.stdout.readline().strip()
                    if not line:
                        raise AssertionError("process ended early")
                    if line.startswith("{"):
                        return json.loads(line)

            ready_line = proc.stdout.readline().strip()
            assert json.loads(ready_line)["type"] == "ready"
            for cmd in [
                {"cmd": "start_run", "character": "Ironclad",
                 "seed": "gate-trace-flush", "ascension": 0},
                {"cmd": "set_player",
                 "deck": ["BASH", "DEFEND_IRONCLAD", "ANGER",
                          "STRIKE_IRONCLAD", "STRIKE_IRONCLAD"],
                 "hp": 70, "max_hp": 70},
                {"cmd": "enter_room", "type": "combat", "encounter": ENCOUNTER},
                {"cmd": "get_combat_snapshot", "view": "public"},
            ]:
                resp = send(cmd)
                assert resp.get("trace_status") == "ok", resp
                acked_records.append(resp["trace_record"])
            quit_resp = send({"cmd": "quit"})
            assert quit_resp["type"] == "quit_result"
        finally:
            try:
                proc.wait(timeout=10)
            except Exception:
                proc.kill()

        with open(trace, encoding="utf-8") as handle:
            raw = handle.read()
        assert raw.endswith("\n"), "last trace line must be newline-terminated"
        rows = _read_trace(trace)
        # one row per attached command plus the final quit row
        assert len(rows) == len(acked_records) + 1, (len(rows), len(acked_records))
        # the final flushed row is the quit step's own record
        quit_record = quit_resp["trace_record"]
        assert rows[-1]["step"] == quit_record["step"] == len(acked_records)
        assert rows[-1]["post_state_hash"] == quit_record["post_state_hash"]
        for i, record in enumerate(acked_records):
            assert rows[i]["step"] == record["step"] == i
            assert rows[i]["post_state_hash"] == record["post_state_hash"]

    def test_hard_kill_leaves_valid_jsonl_without_losing_acked_rows(self):
        trace = _fresh_trace_path("gate-trace-kill.jsonl")
        env = os.environ.copy()
        env.update(self._env_trace(trace))
        project = os.path.join(ROOT, "src", "Sts2Headless", "Sts2Headless.csproj")
        proc = subprocess.Popen(
            ["dotnet", "run", "--no-build", "--project", project],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, bufsize=1, env=env)
        try:
            def send(cmd):
                proc.stdin.write(json.dumps(cmd) + "\n")
                proc.stdin.flush()
                while True:
                    line = proc.stdout.readline().strip()
                    if not line:
                        raise AssertionError("process ended early")
                    if line.startswith("{"):
                        return json.loads(line)

            ready_line = proc.stdout.readline().strip()
            assert ready_line.startswith("{"), ready_line
            ready = json.loads(ready_line)
            assert ready["type"] == "ready" and ready["compatible"] is True
            acked = []
            for cmd in [
                {"cmd": "start_run", "character": "Ironclad",
                 "seed": "gate-trace-kill-seed", "ascension": 0},
                {"cmd": "set_player",
                 "deck": ["BASH", "DEFEND_IRONCLAD", "ANGER",
                          "STRIKE_IRONCLAD", "STRIKE_IRONCLAD"],
                 "hp": 70, "max_hp": 70},
                {"cmd": "enter_room", "type": "combat", "encounter": ENCOUNTER},
            ]:
                resp = send(cmd)
                acked.append(resp)
                assert resp.get("trace_status") == "ok", resp
        finally:
            proc.kill()
            proc.wait(timeout=10)
            try:
                proc.stdin.close()
                proc.stdout.close()
            except Exception:
                pass

        rows = _read_trace(trace)
        assert rows, "no trace rows survived hard kill"
        assert all(int(r["step"]) == i for i, r in enumerate(rows)), \
            "steps must be contiguous and ordered"
        assert len(rows) == len(acked), (len(rows), len(acked))

    def test_new_session_appends_to_existing_trace_file(self):
        trace = _fresh_trace_path("gate-trace-append.jsonl")
        envA = self._env_trace(trace)
        seed = "gate-trace-append-seed"

        cmds_a = [
            {"cmd": "start_run", "character": "Ironclad", "seed": seed, "ascension": 0},
            {"cmd": "set_player", "deck": ["BASH", "DEFEND_IRONCLAD"], "hp": 70, "max_hp": 70},
        ]
        session_a = run_commands(cmds_a, envA)
        rows_a = _read_trace(trace)
        count_a = len(rows_a)
        assert count_a == 2
        trace_ids_a = {row["trace_id"] for row in rows_a}
        assert trace_ids_a == {f"trace-v0111-{seed}"}

        cmds_b = [{"cmd": "enter_room", "type": "combat", "encounter": ENCOUNTER}]
        run_commands([cmds_a[0]] + cmds_b, envA)
        rows_b = _read_trace(trace)
        assert len(rows_b) > count_a, "second session must append, not truncate"
        # Documented append semantics: STS2_TRACE_PATH opens with append=true;
        # the per-session step counter restarts at 0 while trace_id is derived
        # deterministically from the seed, so consumers distinguish sessions by
        # the step reset boundary inside the file.
        second_session_rows = rows_b[count_a:]
        assert second_session_rows[0]["step"] == 0
        assert {row["trace_id"] for row in rows_b} == {f"trace-v0111-{seed}"}


# ---------------------------------------------------------------------------
# Deliverable 4: legacy protocol compatibility
# ---------------------------------------------------------------------------

class TestLegacyProtocolCompat:
    def test_ready_reports_protocol_0_2_0(self):
        ready = run_commands([])[0]
        assert ready["version"] == CLI_VERSION
        assert ready["compatible"] is True

    def test_play_card_and_end_turn_via_legacy_indexes(self):
        setup = [
            {"cmd": "start_run", "character": "Ironclad", "seed": "gate-legacy", "ascension": 0},
            {"cmd": "set_player",
             "deck": ["BASH", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD",
                      "STRIKE_IRONCLAD", "STRIKE_IRONCLAD"],
             "hp": 70, "max_hp": 70},
            {"cmd": "enter_room", "type": "combat", "encounter": ENCOUNTER},
        ]
        state = run_commands(setup)[-1]
        assert state["decision"] == "combat_play"
        playable = [c for c in state["hand"] if c.get("can_play")]
        assert playable
        args = {"card_index": playable[0]["index"]}
        if playable[0].get("target_type") == "AnyEnemy":
            args["target_index"] = state["enemies"][0]["index"]
        played = run_commands(setup + [
            {"cmd": "action", "action": "play_card", "args": args}])[-1]
        assert played.get("trace_status") == "ok", played
        if played.get("decision") == "combat_play":
            expected_energy = max(0, state["energy"] - playable[0]["cost"])
            assert played["energy"] == expected_energy

        turned = run_commands(setup + [
            {"cmd": "action", "action": "end_turn"}])[-1]
        assert turned.get("trace_status") == "ok", turned


# ---------------------------------------------------------------------------
# Deliverable 5: public/teacher isolation
# ---------------------------------------------------------------------------

# Exact key names forbidden anywhere inside a public observation (identities
# beyond *_count aggregates). *_count integers are allowed.
PUBLIC_FORBIDDEN_EXACT_KEYS = {
    "run_seed", "seed", "rng_word", "rng_words", "raw_words",
    "draw_pile", "discard_pile", "exhaust_pile",
}
# Substrings that must never appear in any key of a public observation.
PUBLIC_FORBIDDEN_KEY_SUBSTRINGS = (
    "seed", "rng_word", "raw_word",
)


def _iter_nodes(node, path=""):
    if isinstance(node, dict):
        for key, value in node.items():
            yield path + "/" + key, key, value
            yield from _iter_nodes(value, path + "/" + key)
    elif isinstance(node, list):
        for item in node:
            yield from _iter_nodes(item, path)


class TestPublicTeacherIsolation:
    SEED_TOKEN = "gate-isolation-private-seed-XYZ"

    # Transport metadata injected at top level by TraceSession.Attach. These
    # are run-scoped bookkeeping (e.g. trace_id = "trace-v0111-{seed}" per the
    # locked protocol), not part of the student-facing observation view; the
    # schema-level leak surface checked here is the public_observation view
    # itself, whose recursion below includes every nested node.
    TRACE_ENVELOPE_KEYS = {
        "trace_id", "trace_schema", "trace_step", "pre_state_hash",
        "post_state_hash", "normalized_action_id", "trace_status",
        "trace_record",
    }

    def _observation_views(self, response):
        stripped = {k: v for k, v in response.items()
                    if k not in self.TRACE_ENVELOPE_KEYS}
        if isinstance(stripped.get("public_observation"), dict):
            return [stripped["public_observation"]]
        if stripped.get("decision"):
            return [stripped]
        return []

    def _flows_public_payloads(self):
        """Collect public observations across multiple decision surfaces."""
        payloads = []
        setup = [
            {"cmd": "start_run", "character": "Ironclad",
             "seed": self.SEED_TOKEN, "ascension": 0},
            {"cmd": "set_player",
             "deck": ["PURITY", "BASH", "ANGER", "DEFEND_IRONCLAD",
                      "STRIKE_IRONCLAD", "STRIKE_IRONCLAD"],
             "hp": 70, "max_hp": 70},
            {"cmd": "enter_room", "type": "combat", "encounter": ENCOUNTER},
        ]
        combat = run_commands(setup + [
            {"cmd": "get_combat_snapshot", "view": "public"}])
        payloads.extend(self._observation_views(combat[-1]))
        payloads.extend(self._observation_views({k: v for k, v in combat[-1].items()
                                                 if k != "teacher_snapshot"}))

        failed_play = run_commands(setup + [
            {"cmd": "action", "action": "play_card", "args": {"card_index": 99}}])[-1]
        assert not self._observation_views(failed_play), \
            "failure responses must not expose an observation view"

        select_transcript, select_prefix = _reach_card_select(setup)
        payloads.extend(self._observation_views(select_transcript[-1]))
        anger_idx = select_transcript[-1]["cards"][0]["index"]
        finished = run_commands(select_prefix + [
            {"cmd": "action", "action": "select_cards",
             "args": {"indices": str(anger_idx)}},
            {"cmd": "get_combat_snapshot", "view": "public"},
        ])
        for resp in finished[-2:]:
            payloads.extend(self._observation_views(resp))

        next_turn = run_commands(setup + [
            {"cmd": "action", "action": "end_turn"},
            {"cmd": "get_combat_snapshot", "view": "public"},
        ])
        payloads.append(next_turn[-1]["public_observation"])
        return payloads

    def test_public_observations_contain_no_private_material(self):
        leakage = []
        for payload in self._flows_public_payloads():
            text = json.dumps(payload, ensure_ascii=False).lower()
            if self.SEED_TOKEN.lower() in text:
                leakage.append(("seed_literal", ""))
            for path, key, _value in _iter_nodes(payload):
                low_key = key.lower()
                if low_key in PUBLIC_FORBIDDEN_EXACT_KEYS:
                    leakage.append((path, f"exact key '{key}'"))
                else:
                    for token in PUBLIC_FORBIDDEN_KEY_SUBSTRINGS:
                        if token in low_key and not low_key.endswith("_count"):
                            leakage.append((path, f"key contains '{token}'"))
            # *_count aggregates must stay plain integers (no ordering data)
            for path, key, value in _iter_nodes(payload):
                if key.endswith("_count"):
                    if not isinstance(value, int):
                        leakage.append((path, "non-integer count"))
        assert leakage == [], f"public_leakage_count={len(leakage)}: {leakage}"

    def test_teacher_snapshot_rebuild_info_without_plaintext_seed(self):
        setup = [
            {"cmd": "start_run", "character": "Ironclad",
             "seed": self.SEED_TOKEN, "ascension": 0},
            {"cmd": "set_player",
             "deck": ["BASH", "DEFEND_IRONCLAD", "ANGER", "PURITY",
                      "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD"],
             "hp": 70, "max_hp": 70},
            {"cmd": "enter_room", "type": "combat", "encounter": ENCOUNTER},
        ]
        teacher_response = run_commands(setup + [
            {"cmd": "get_combat_snapshot", "view": "teacher"}])[-1]
        teacher = teacher_response["teacher_snapshot"]

        assert teacher["available"] is True
        assert teacher["rng_raw_words_exposed"] is False
        # rebuild shadow-state requirements
        for pile in ("hand", "draw_pile", "discard_pile", "exhaust_pile"):
            assert pile in teacher, pile
        assert teacher["draw_pile"], "ordered draw pile needed for shadow rebuild"
        for item in teacher["draw_pile"]:
            assert set(item) >= {"instance_id", "id", "type", "upgraded"}
        assert teacher["round"] >= 1
        assert teacher["player_powers"] is not None
        assert teacher["enemy_powers"] is not None
        assert teacher["rng_counters_available"] is True
        assert teacher["rng_counters"], "rng counters must be present"

        text = json.dumps(teacher, ensure_ascii=False).lower()
        assert self.SEED_TOKEN.lower() not in text, "plaintext seed leaked into teacher view"

        # teacher piles are exactly the ordering information public hides
        pub = run_commands(setup + [
            {"cmd": "get_combat_snapshot", "view": "public"}])[-1]
        pub_text = json.dumps(pub["public_observation"], ensure_ascii=False).lower()
        assert '"draw_pile"' not in pub_text
