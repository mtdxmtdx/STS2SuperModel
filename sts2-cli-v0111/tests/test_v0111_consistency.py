"""Version gate and deterministic seed checks for the v0.111 migration."""

import json
import os
import subprocess


ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = os.path.join(ROOT, "src", "Sts2Headless", "Sts2Headless.csproj")


def run_commands(commands, extra_env=None):
    env = os.environ.copy()
    game_dir = env.get("STS2_GAME_DIR")
    if game_dir:
        env["STS2_GAME_DIR"] = game_dir
    if extra_env:
        env.update(extra_env)
    proc = subprocess.Popen(
        ["dotnet", "run", "--no-build", "--project", PROJECT],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        bufsize=1,
        env=env,
    )
    try:
        line = proc.stdout.readline().strip()
        if not line:
            raise AssertionError("headless process ended before ready")
        ready = json.loads(line)
        responses = [ready]
        for command in commands:
            proc.stdin.write(json.dumps(command) + "\n")
            proc.stdin.flush()
            while True:
                line = proc.stdout.readline().strip()
                if not line:
                    raise AssertionError(f"headless process ended: {proc.stderr.read()}")
                if line.startswith("{"):
                    responses.append(json.loads(line))
                    break
        proc.stdin.write('{"cmd":"quit"}\n')
        proc.stdin.flush()
        return responses
    finally:
        proc.terminate()
        proc.wait(timeout=5)
        proc.stdin.close()
        proc.stdout.close()


def test_v0111_compatibility_gate():
    ready = run_commands([])
    assert ready[0]["compatible"] is True
    assert ready[0]["game_version"] == "v0.111.0"


def test_ready_message_and_fixed_seed_are_stable():
    commands = [
        {"cmd": "start_run", "character": "Ironclad", "seed": "v0111-consistency", "ascension": 0},
        {"cmd": "get_map"},
    ]
    first = run_commands(commands)
    second = run_commands(commands)

    assert first[0]["type"] == "ready"
    assert first[0]["version"] == "0.2.0"
    assert first[0]["game_version"] == "v0.111.0"
    assert first[0]["game_commit"] == "41cef1ea"
    assert first[0]["assembly_sha256"] == "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9"
    assert first[0]["compatible"] is True
    assert first == second


def test_training_trace_and_snapshot_contract():
    trace_path = os.path.join(ROOT, "test-output", "trace-contract.jsonl")
    os.makedirs(os.path.dirname(trace_path), exist_ok=True)
    try:
        os.remove(trace_path)
    except FileNotFoundError:
        pass
    responses = run_commands([
        {"cmd": "start_run", "character": "Ironclad", "seed": "trace-contract", "ascension": 0},
        {"cmd": "get_combat_snapshot", "view": "public"},
        {"cmd": "get_combat_snapshot", "view": "teacher"},
    ], {"STS2_TRACE_PATH": str(trace_path)})

    start, public, teacher = responses[1:]
    assert start["trace_schema"] == 1
    assert start["trace_id"] == "trace-v0111-trace-contract"
    assert start["post_state_hash"]
    assert public["type"] == "combat_snapshot"
    assert public["observation_view"] == "public"
    assert public["public_state_hash"]
    assert teacher["observation_view"] == "teacher"
    assert teacher["teacher_snapshot"]["rng_raw_words_exposed"] is False
    assert teacher["pre_state_hash"] == public["post_state_hash"]

    with open(trace_path, encoding="utf-8") as handle:
        rows = [json.loads(line) for line in handle if line.strip()]
    assert len(rows) >= 3
    assert rows[0]["game_version"] == "v0.111.0"
    assert rows[0]["assembly_sha256"] == "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9"
    assert rows[-1]["post_state_hash"]
    assert all("chance_branch" in row for row in rows)
    assert all(row["produced_chance_branch"] == row["chance_branch"]["produced"] for row in rows)
    os.remove(trace_path)


def test_failed_trace_records_recovery_metadata():
    trace_path = os.path.join(ROOT, "test-output", "trace-failure-contract.jsonl")
    os.makedirs(os.path.dirname(trace_path), exist_ok=True)
    try:
        os.remove(trace_path)
    except FileNotFoundError:
        pass
    responses = run_commands([
        {"cmd": "start_run", "character": "Ironclad", "seed": "trace-failure-contract", "ascension": 0},
        {"cmd": "enter_room", "type": "combat", "encounter": "SHRINKER_BEETLE_WEAK"},
        {"cmd": "action", "action": "play_card", "args": {"card_index": 999, "target_index": 0}},
    ], {"STS2_TRACE_PATH": str(trace_path)})
    assert responses[-1]["trace_status"] == "failed"
    with open(trace_path, encoding="utf-8") as handle:
        rows = [json.loads(line) for line in handle if line.strip()]
    failed = rows[-1]
    assert failed["status"] == "failed"
    assert failed["failure"]["recovery_required"] is True
    assert failed["failure"]["recovery_status"] == "not_attempted"
    assert failed["failure"]["failed_step"] == failed["step"]
    os.remove(trace_path)


def test_relic_fixture_preserves_owner_and_combat_state():
    responses = run_commands([
        {"cmd": "start_run", "character": "Ironclad", "seed": "relic-owner-contract", "ascension": 0},
        {"cmd": "set_player", "relics": ["ANCHOR"]},
        {"cmd": "enter_room", "type": "combat", "encounter": "SHRINKER_BEETLE_WEAK"},
        {"cmd": "get_combat_snapshot", "view": "public"},
    ])
    combat = responses[-1]
    assert combat["type"] == "combat_snapshot"
    observation = combat["public_observation"]
    assert observation["decision"] == "combat_play"
    assert observation["player"]["relics"][0]["id"] == "ANCHOR"
    assert observation["player"]["block"] == 10
    assert all(candidate.get("source_instance_id") for candidate in observation["action_candidates"] if candidate["kind"] == "PlayCard")


def test_structured_power_and_relic_training_state():
    setup = [
        {"cmd": "start_run", "character": "Ironclad", "seed": "structured-power-contract", "ascension": 0},
        {"cmd": "set_player", "deck": ["BASH", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD"]},
        {"cmd": "enter_room", "type": "combat", "encounter": "SHRINKER_BEETLE_WEAK"},
        {"cmd": "get_combat_snapshot", "view": "public"},
    ]
    initial = run_commands(setup)
    observation = initial[-1]["public_observation"]
    bash = next(card for card in observation["hand"] if card["id"].endswith("BASH"))
    enemy = observation["enemies"][0]

    responses = run_commands(setup + [
        {"cmd": "action", "action": "play_card", "args": {"card_index": bash["index"], "target_index": enemy["index"]}},
        {"cmd": "get_combat_snapshot", "view": "public"},
        {"cmd": "get_combat_snapshot", "view": "teacher"},
    ])
    public = responses[-2]["public_observation"]
    teacher = responses[-1]["teacher_snapshot"]
    vulnerable = next(power for power in public["enemies"][0]["powers"] if power["id"] == "VULNERABLE_POWER")

    assert vulnerable["owner_id"] == public["enemies"][0]["instance_id"]
    assert "applier_id" in vulnerable
    assert vulnerable["amount"] == 2
    assert isinstance(vulnerable["dynamic_vars"], dict)
    assert isinstance(vulnerable["internal_counters"], dict)
    assert isinstance(vulnerable["trigger_phases"], list)
    assert vulnerable["source"] == "game_runtime"
    assert vulnerable["support"] == "state_captured"
    assert vulnerable["evidence"] == "LiveObserved"
    assert vulnerable["source_version"] == "v0.111.0"

    teacher_enemy = next(item for item in teacher["enemy_powers"] if item["enemy_id"] == public["enemies"][0]["instance_id"])
    teacher_vulnerable = next(power for power in teacher_enemy["powers"] if power["id"] == "VULNERABLE_POWER")
    assert teacher_vulnerable["amount"] == vulnerable["amount"]
    assert teacher_vulnerable["owner_id"] == vulnerable["owner_id"]

    relic = public["player"]["relics"][0]
    assert relic["id"] == "BURNING_BLOOD"
    assert "counter" in relic
    assert isinstance(relic["dynamic_vars"], dict)
    assert relic["source"] == "game_runtime"
    assert relic["evidence"] == "LiveObserved"
    assert relic["source_version"] == "v0.111.0"
