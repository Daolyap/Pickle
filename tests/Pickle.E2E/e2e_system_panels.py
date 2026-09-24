"""System panels in a real pty: Processes (Alt+P), Network (Alt+N) and Disks (Alt+D) open, load real data and close."""

import os
import shutil
import tempfile
import time

from pty_harness import PickleSession, alt


def _open_and_close(s, key, title, loaded, marker):
    s.press(alt(key))
    s.wait_for(f"{title}  ·  Esc to close", timeout=20)
    s.wait_for(loaded, timeout=20)
    time.sleep(0.5)
    s.press("esc")
    time.sleep(0.5)
    s.run(f"Write-Output ('{marker}' + '-closed')", f"{marker}-closed", timeout=15)


def test_system_panels_open_with_chords_and_close_with_esc(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        time.sleep(0.3)
        _open_and_close(s, "p", "Processes", "threads", "processes")
        _open_and_close(s, "n", "Network", "Interfaces", "network")
        _open_and_close(s, "d", "Disks", "Volumes", "disks")


def test_pk_disks_analyzes_a_folder(p):
    root = tempfile.mkdtemp(prefix="pickle-e2e-disks-")
    try:
        os.makedirs(os.path.join(root, "e2e-big-folder"))
        with open(os.path.join(root, "e2e-big-folder", "blob.bin"), "wb") as f:
            f.write(b"\0" * 200_000)
        with PickleSession(p) as s:
            s.wait_for_prompt()
            s.type(f"pk disks '{root}'\r")
            s.wait_for("Disks  ·  Esc to close", timeout=20)
            s.wait_for("e2e-big-folder", timeout=20)
            time.sleep(0.5)
            s.press("esc")
            time.sleep(0.5)
            s.run("Write-Output ('pk-disks' + '-closed')", "pk-disks-closed", timeout=15)
    finally:
        shutil.rmtree(root, ignore_errors=True)
