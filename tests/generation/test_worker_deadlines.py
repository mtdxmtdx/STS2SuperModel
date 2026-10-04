"""Real short-lived subprocess transport checks, with no game or training."""
from contextlib import contextmanager
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch
from test_generate_pilot_data import m, ROOT


@contextmanager
def child(code, timeout=1):
    real_popen=subprocess.Popen
    with tempfile.TemporaryDirectory() as directory:
        def launch(_command,**kwargs):return real_popen([sys.executable,'-u','-c',code],**kwargs)
        with patch.object(m.subprocess,'Popen',side_effect=launch):worker=m.Worker(ROOT,Path(directory),timeout)
        try:yield worker
        finally:worker.close()


class WorkerDeadlineTests(unittest.TestCase):
    def test_partial_line_cannot_bypass_response_deadline(self):
        with child("import sys,time;sys.stdin.readline();sys.stdout.write('{\\\"x\\\":');sys.stdout.flush();time.sleep(3)",.1) as worker:
            started=time.monotonic()
            with self.assertRaisesRegex(TimeoutError,'worker_response_deadline'):worker.request({'op':'test'})
            self.assertLess(time.monotonic()-started,1.5)
            self.assertTrue(worker.buffer)

    def test_multiple_fast_requests_share_attempt_deadline(self):
        code="import sys,time\nfor line in sys.stdin:\n time.sleep(.08);print('{\\\"status\\\":\\\"ok\\\"}',flush=True)"
        with child(code,2) as worker:
            worker.attempt_deadline=time.monotonic()+.22
            completed=0;started=time.monotonic()
            with self.assertRaisesRegex(TimeoutError,'source_attempt_deadline'):
                for i in range(4):worker.request({'step':i});completed+=1
            self.assertLess(completed,4)
            self.assertLess(time.monotonic()-started,1.5)

    def test_expired_attempt_does_not_send_another_command(self):
        with child("import sys;sys.stdin.readline();print('{\\\"sent\\\":true}',flush=True)") as worker:
            worker.attempt_deadline=time.monotonic()-1
            with self.assertRaisesRegex(TimeoutError,'source_attempt_deadline'):worker.request({'op':'forbidden-after-budget'})
            self.assertEqual(bytearray(),worker.buffer)

    def test_large_split_response_is_complete(self):
        code="import sys,json;sys.stdin.readline();print(json.dumps({'value':'x'*200000}),flush=True)"
        with child(code) as worker:
            self.assertEqual({'value':'x'*200000},worker.request({'op':'large'}))
