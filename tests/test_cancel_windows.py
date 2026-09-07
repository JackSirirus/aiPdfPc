# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (C) 2026 AIPDF contributors
"""Windows cancel semantics: stop() must take down the whole helper process tree.

Upstream test_cancel_stops_children exercises the POSIX killpg path through
/usr/bin/pgrep and /bin/sleep. This file pins the equivalent contract for
Windows: after stop(), taskkill must have removed the engine's helper and
every descendant it spawned, within seconds.
"""
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path

ROOT=Path(__file__).resolve().parent.parent
sys.path.insert(0,str(ROOT/"backend"))
import engine


def alive(pid):
    """True while the given PID still shows up in tasklist."""
    info=subprocess.run(["tasklist","/NH","/FI",f"PID eq {pid}"],capture_output=True)
    return str(pid) in info.stdout.decode(errors="replace")


@unittest.skipUnless(sys.platform=="win32","Windows-only cancellation semantics")
class CancelWindowsTests(unittest.TestCase):
    def test_stop_kills_whole_process_tree(self):
        """① Python-built tree ② stop() removes helper and grandchild ③ cancel ≤ 5 s."""
        (ROOT/"tmp").mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="aipdf-cancel-",dir=ROOT/"tmp") as temp:
            root=Path(temp)
            helper=root/"slow-tree.py"
            pidfile=root/"grandchild.pid"
            # The helper spawns a grandchild of its own: a naive single-process
            # kill would leave the grandchild behind; taskkill /T must not.
            helper.write_text(
                "import subprocess,sys,time\n"
                "grand=subprocess.Popen([sys.executable,'-c','import time;time.sleep(120)'])\n"
                "open(sys.argv[1],'w').write(str(grand.pid))\n"
                "time.sleep(120)\n",
                encoding="utf-8")
            outcome={}
            def run():
                try:
                    engine.run_process([sys.executable,str(helper),str(pidfile)])
                    outcome["result"]="completed"
                except engine.UserError as error:
                    outcome["result"]="cancelled"
                    outcome["error"]=str(error)
                except BaseException as error:
                    outcome["result"]=f"unexpected: {error!r}"
            worker=threading.Thread(target=run,daemon=True)
            worker.start()
            # Wait until the helper exists and has published its grandchild.
            child_pid=grand_pid=None
            deadline=time.monotonic()+10
            while time.monotonic()<deadline:
                if engine.CHILDREN and pidfile.is_file():
                    try:
                        grand_pid=int(pidfile.read_text())
                    except ValueError:
                        grand_pid=None
                    if grand_pid:
                        child_pid=engine.CHILDREN[0].pid
                        if alive(child_pid) and alive(grand_pid): break
                time.sleep(.05)
            try:
                self.assertIsNotNone(child_pid,"helper process never appeared")
                self.assertIsNotNone(grand_pid,"grandchild process never appeared")
                self.assertTrue(alive(child_pid) and alive(grand_pid),"tree died before cancel")
                start=time.monotonic()
                with self.assertRaisesRegex(engine.UserError,"取消"):
                    engine.stop()
                # Poll tasklist until every tree member is gone; time the cancellation.
                while time.monotonic()-start<5 and (alive(child_pid) or alive(grand_pid)):
                    time.sleep(.05)
                elapsed=time.monotonic()-start
                self.assertFalse(alive(child_pid),f"helper {child_pid} survived stop()")
                self.assertFalse(alive(grand_pid),f"grandchild {grand_pid} survived stop()")
                self.assertLessEqual(elapsed,5,f"cancellation took {elapsed:.2f}s")
                worker.join(timeout=5)
                self.assertFalse(worker.is_alive(),"run_process did not unwind after stop()")
                self.assertEqual(outcome.get("result"),"cancelled")
            finally:
                # Never leak sleepers on assertion failure.
                for pid in {child_pid,grand_pid}:
                    if pid: subprocess.run(["taskkill","/T","/F","/PID",str(pid)],capture_output=True)


if __name__=="__main__":
    unittest.main(verbosity=2)
