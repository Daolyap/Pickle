"""W6 panels in a real pty: command palette, file picker, and terminal state after a panel closes."""

import os
import shutil
import tempfile
import time

from pty_harness import PickleSession


def _assert_terminal_restored(raw: bytes, since: int):
    """Every mode Terminal.Gui turned on after `since` must be turned off again (pyte has no alt screen, so check bytes)."""
    tail = raw[since:]
    for on, off in [
        (b"\x1b[?1049h", b"\x1b[?1049l"),
        (b"\x1b[?25l", b"\x1b[?25h"),
        (b"\x1b[?1003h", b"\x1b[?1003l"),
        (b"\x1b[?1006h", b"\x1b[?1006l"),
    ]:
        if on in tail:
            assert tail.rfind(off) > tail.rfind(on), f"{on!r} was not undone by {off!r} after the panel closed"


def test_w6_palette_opens_and_closes(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        time.sleep(0.3)
        since = len(s.raw)
        s.press("f1")
        s.wait_for("Command palette", timeout=15)
        s.wait_for("Panel", timeout=10)
        s.press("esc")
        time.sleep(0.5)
        with s.lock:
            raw = bytes(s.raw)
        _assert_terminal_restored(raw, since)
        assert not s.screen.cursor.hidden, "cursor still hidden after the palette closed"
        assert b"\x1b]0;Command palette" not in raw[since:], "panel title leaked into the terminal title"
        s.run("Write-Output ('pal' + 'ette-closed')", "palette-closed", timeout=15)


def test_w6_ctrl_t_inserts_selected_file(p):
    root = tempfile.mkdtemp(prefix="pickle-e2e-w6-")
    try:
        with open(os.path.join(root, "w6-target-file.txt"), "w") as f:
            f.write("hello from w6\n")
        os.makedirs(os.path.join(root, "other"))
        with open(os.path.join(root, "other", "unrelated.md"), "w") as f:
            f.write("x\n")
        with PickleSession(p) as s:
            s.wait_for_prompt()
            s.run(f"Set-Location -LiteralPath '{root}'; 'cd-done'", "cd-done", timeout=15)
            s.type("Get-Content ")
            time.sleep(0.2)
            since = len(s.raw)
            s.press("ctrl+t")
            s.wait_for("w6-target-file.txt", timeout=15)
            s.type("target")
            time.sleep(0.5)
            s.press("enter")
            s.wait_for("Get-Content w6-target-file.txt", timeout=15)
            with s.lock:
                raw = bytes(s.raw)
            _assert_terminal_restored(raw, since)
            s.press("enter")
            s.wait_for("hello from w6", timeout=15)
    finally:
        shutil.rmtree(root, ignore_errors=True)
