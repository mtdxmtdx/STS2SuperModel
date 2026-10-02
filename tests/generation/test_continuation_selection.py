"""Continuation identity wiring; mock outputs are not simulator evidence."""
from argparse import Namespace
from contextlib import redirect_stdout
from copy import deepcopy
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from test_generate_pilot_data import ROOT, m, fixture, outcomes


def record(policy, tree=False):
    row = fixture()
    family = ("nosl-public-uct-frozen-v2-rules-v2" if policy == m.REVIEWED_CONTINUATION
              else "nosl-public-uct-frozen-v1") if tree else policy
    row["audit_only"]["continuation_version"] = family + ":0123456789abcdef" if tree else family
    row["audit_only"]["versions"]["continuation"] = family
    if policy == m.REVIEWED_CONTINUATION:
        row["audit_only"]["dataset_version"] = m.REVIEWED_DATASET
        row["audit_only"]["versions"]["dataset"] = m.REVIEWED_DATASET
    return row


class ContinuationSelectionTests(unittest.TestCase):
    def test_positive_capability_required_before_opt_in(self):
        class Worker:
            def __init__(self, reply): self.reply, self.commands = reply, []
            def request(self, command): self.commands.append(command); return self.reply
        for reply in ({"status": "invalid_operation", "message": "reset required"},
                      {"status": "available", "version": "unknown", "supportedPolicyIds": [m.REVIEWED_CONTINUATION]},
                      {"status": "available", "version": "nosl.continuation-policies.v1", "supportedPolicyIds": m.REVIEWED_CONTINUATION},
                      {"status": "available", "version": "nosl.continuation-policies.v1", "supportedPolicyIds": [m.LEGACY_CONTINUATION]}):
            with self.subTest(reply=reply), self.assertRaisesRegex(ValueError, "worker_continuation_policy_unavailable"):
                m.require_worker_continuation(Worker(reply), m.REVIEWED_CONTINUATION)
        worker = Worker({"status": "available", "version": "nosl.continuation-policies.v1", "supportedPolicyIds": [m.LEGACY_CONTINUATION,m.REVIEWED_CONTINUATION]})
        m.require_worker_continuation(worker, m.REVIEWED_CONTINUATION)
        self.assertEqual(worker.commands, [{"op": "continuation_policies"}])
        untouched = Worker(None)
        m.require_worker_continuation(untouched, m.LEGACY_CONTINUATION)
        self.assertEqual([], untouched.commands)

    def test_source_continuation_receives_explicit_policy_without_root_peeking(self):
        class Worker:
            def __init__(self): self.commands = []
            def request(self, command):
                self.commands.append(command)
                return {"status": "player_decision", "observation": {"turn": len(self.commands)}}
        worker = Worker()
        _, count, phase = m.collect_source_snapshot(worker, {"seed":"audit-only"}, "public-phase-v1", 1, 10, m.REVIEWED_CONTINUATION)
        self.assertEqual((1,"first_player_turn_2"),(count,phase))
        self.assertEqual({"op":"continue","continuationPolicyId":m.REVIEWED_CONTINUATION},worker.commands[1])
        self.assertEqual(2,len(worker.commands))

    def test_teacher_identity_and_dataset_lock_cannot_be_substituted(self):
        for policy in (m.LEGACY_CONTINUATION,m.REVIEWED_CONTINUATION):
            for tree in (False,True):
                row=record(policy,tree)
                m.validate_continuation_record(row,policy,"T1" if tree else "T0")
                bad=deepcopy(row);bad["audit_only"]["versions"]["continuation"]="other"
                with self.assertRaisesRegex(ValueError,"teacher_continuation_mismatch"):
                    m.validate_continuation_record(bad,policy,"T1" if tree else "T0")
        with self.assertRaisesRegex(ValueError,"teacher_continuation_mismatch"):
            m.validate_continuation_record(record(m.LEGACY_CONTINUATION),m.REVIEWED_CONTINUATION,"T0")
        for key in ("dataset_version","versions"):
            bad=record(m.REVIEWED_CONTINUATION)
            if key=="versions":bad["audit_only"][key].pop("dataset")
            else:bad["audit_only"].pop(key)
            with self.assertRaisesRegex(ValueError,"teacher_dataset_version_mismatch"):
                m.validate_continuation_record(bad,m.REVIEWED_CONTINUATION,"T0")
        bad=record(m.REVIEWED_CONTINUATION,True);bad["audit_only"]["continuation_version"]="nosl-public-uct-frozen-v2-rules-v2:"
        with self.assertRaisesRegex(ValueError,"teacher_continuation_mismatch"):
            m.validate_continuation_record(bad,m.REVIEWED_CONTINUATION,"T1")

    def _args(self, output):
        return Namespace(repo=ROOT,output=output,teacher="T0",worlds=8,exploration_worlds=0,max_decisions=20,
            tree_depth=2,mode="pilot",seed_prefix="mock-v2",roots_per_battle=1,root_policy="opening-prefix-v1",
            max_source_decisions=20,shard_id=0,shard_count=1,target_roots=1,max_attempts=9,timeout=5,
            max_worker_mib=100,continuation_policy=m.REVIEWED_CONTINUATION)

    def _worker(self, capable=True, honest=True):
        class Worker:
            commands=[]
            def __init__(self,*args):pass
            def request(self,command):
                type(self).commands.append(deepcopy(command))
                if command["op"]=="continuation_policies":
                    return {"status":"available","version":"nosl.continuation-policies.v1","supportedPolicyIds":[m.REVIEWED_CONTINUATION]} if capable else {"status":"invalid_operation"}
                row=record(m.REVIEWED_CONTINUATION if honest else m.LEGACY_CONTINUATION)
                if command["op"]=="reset":
                    return {"status":"player_decision","observation":row["public_input"]["observation"],"actions":row["public_input"]["candidate_actions"]}
                if command["op"]=="teacher_record":
                    row["audit_only"].update(source_run_group=command["sourceRun"],source_combat_id=command["sourceCombat"],branch_family=command["branchFamily"])
                    row["audit_only"]["outcome_samples"]=outcomes(row)
                    return row
                raise AssertionError(command)
            def close(self):pass
        return Worker

    def test_v2_is_bound_to_generation_config_and_cannot_resume_as_v1(self):
        with tempfile.TemporaryDirectory() as directory:
            output=Path(directory);args=self._args(output);worker=self._worker()
            with patch.object(m,"Worker",worker),redirect_stdout(io.StringIO()):m.run_locked(args)
            cfg=json.loads((output/"generation_config.json").read_text())
            self.assertEqual(m.REVIEWED_CONTINUATION,cfg["source_continuation_policy"])
            self.assertEqual(m.REVIEWED_CONTINUATION,cfg["teacher_options"]["continuationPolicyId"])
            self.assertEqual(["continuation_policies","reset","teacher_record"],[x["op"] for x in worker.commands])
            teacher=worker.commands[-1]
            self.assertEqual(m.REVIEWED_CONTINUATION,teacher["options"]["continuationPolicyId"])
            self.assertEqual(m.REVIEWED_DATASET,next(m.existing_rows(output/"decisions.jsonl"))["audit_only"]["dataset_version"])
            self.assertEqual(m.REVIEWED_CONTINUATION,next(m.existing_rows(output/"decisions.jsonl"))["audit_only"]["source_policy_version"])
            args.continuation_policy=m.LEGACY_CONTINUATION
            with patch.object(m,"Worker",worker),redirect_stdout(io.StringIO()),self.assertRaisesRegex(ValueError,"Generation version/config changed"):
                m.run_locked(args)
            self.assertEqual(3,len(worker.commands))

    def test_incapable_or_mislabeling_worker_stops_without_accepting_records(self):
        for capable,honest,reason in [(False,True,"worker_continuation_policy_unavailable"),(True,False,"teacher_continuation_mismatch")]:
            with self.subTest(capable=capable),tempfile.TemporaryDirectory() as directory:
                output=Path(directory);worker=self._worker(capable,honest)
                with patch.object(m,"Worker",worker),redirect_stdout(io.StringIO()),self.assertRaisesRegex(RuntimeError,reason):m.run_locked(self._args(output))
                self.assertFalse((output/"decisions.jsonl").exists())
                journals=list(m.existing_rows(output/"attempts.jsonl"));self.assertEqual(1,len(journals))
                self.assertEqual("failed_attempt",journals[0]["status"])
                self.assertEqual(0 if not capable else 16,journals[0]["uncommitted_requested_action_worlds"])
                self.assertEqual("blocked_continuation_identity",json.loads((output/"progress.json").read_text())["status"])
                if not capable:self.assertEqual(["continuation_policies"],[x["op"] for x in worker.commands])

    def test_malformed_version_metadata_is_a_durable_identity_failure(self):
        for bad in (None,[],"text",42):
            row=record(m.REVIEWED_CONTINUATION);row["audit_only"]["versions"]=bad
            with self.subTest(bad=bad),self.assertRaisesRegex(ValueError,"teacher_continuation_mismatch"):
                m.validate_continuation_record(row,m.REVIEWED_CONTINUATION,"T0")
        with tempfile.TemporaryDirectory() as directory:
            output=Path(directory);worker=self._worker();original=worker.request
            def malformed(self,command):
                result=original(self,command)
                if command["op"]=="teacher_record":result["audit_only"]["versions"]=None
                return result
            worker.request=malformed
            with patch.object(m,"Worker",worker),redirect_stdout(io.StringIO()),self.assertRaisesRegex(RuntimeError,"teacher_continuation_mismatch"):
                m.run_locked(self._args(output))
            self.assertEqual(1,len(list(m.existing_rows(output/"attempts.jsonl"))))
            self.assertFalse((output/"inflight_attempt.json").exists())
            self.assertFalse((output/"decisions.jsonl").exists())

    def test_resume_rechecks_source_teacher_and_dataset_identity(self):
        for mutation in ("source","teacher","dataset"):
            with self.subTest(mutation=mutation),tempfile.TemporaryDirectory() as directory:
                output=Path(directory);worker=self._worker();args=self._args(output)
                with patch.object(m,"Worker",worker),redirect_stdout(io.StringIO()):m.run_locked(args)
                row=next(m.existing_rows(output/"decisions.jsonl"));audit=row["audit_only"]
                if mutation=="source":audit["source_policy_version"]=m.LEGACY_CONTINUATION
                elif mutation=="teacher":
                    audit["continuation_version"]=m.LEGACY_CONTINUATION;audit["versions"]["continuation"]=m.LEGACY_CONTINUATION
                else:audit.pop("dataset_version");audit["versions"].pop("dataset")
                (output/"decisions.jsonl").write_text(m.canonical(row)+"\n")
                previous_calls=len(worker.commands)
                with patch.object(m,"Worker",worker),redirect_stdout(io.StringIO()),self.assertRaisesRegex(ValueError,"continuation_mismatch|dataset_version_mismatch"):
                    m.run_locked(args)
                self.assertEqual(previous_calls,len(worker.commands))

    def test_invalid_capability_json_stops_one_attempt_before_reset(self):
        with tempfile.TemporaryDirectory() as directory:
            output=Path(directory)
            class Worker:
                calls=0
                def __init__(self,*args):pass
                def request(self,command):
                    self.assertion=command
                    assert command=={"op":"continuation_policies"}
                    type(self).calls+=1
                    raise json.JSONDecodeError("bad response","!",0)
                def close(self):pass
            with patch.object(m,"Worker",Worker),redirect_stdout(io.StringIO()),self.assertRaisesRegex(RuntimeError,"worker_continuation_policy_unavailable"):
                m.run_locked(self._args(output))
            self.assertEqual(1,Worker.calls)
            self.assertEqual(1,len(list(m.existing_rows(output/"attempts.jsonl"))))
            self.assertEqual("blocked_continuation_identity",json.loads((output/"progress.json").read_text())["status"])
            self.assertFalse((output/"decisions.jsonl").exists())
