"""W1 line editor end-to-end tests: multi-line input, highlighting, Ctrl+C, paste bursts, autosuggestions."""

import time

from pty_harness import PickleSession

UNKNOWN_RED = "ff7a72"  # pickle theme syntax.unknownCommand
COMMAND_GREEN = "b5e36b"  # pickle theme syntax.command


def _lines(s):
    return [line.strip() for line in s.text().split("\n")]


def _wait_line(s, expected, timeout=20.0, count=1):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if _lines(s).count(expected) >= count:
            return
        time.sleep(0.05)
    raise AssertionError(f"Timed out waiting for a line {expected!r} (x{count}). Screen:\n{s.text()}")


def _fg_of(s, needle):
    with s.lock:
        for y, line in enumerate(s.screen.display):
            x = line.find(needle)
            if x >= 0:
                return s.screen.buffer[y][x].fg
    return None


def _wait_color(s, needle, color, timeout=30.0):
    deadline = time.time() + timeout
    fg = None
    while time.time() < deadline:
        fg = _fg_of(s, needle)
        if fg is not None and fg.lower() == color:
            return
        time.sleep(0.1)
    raise AssertionError(f"{needle!r} has color {fg!r}, expected {color}. Screen:\n{s.text()}")


def test_w1_multiline_if(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.type("if ($true) {\r")
        s.wait_for("∙")
        s.type("'yes' }\r")
        _wait_line(s, "yes")


def test_w1_unknown_command_is_red(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.type("definitely-not-a-cmd-w1")
        _wait_color(s, "definitely-not-a-cmd-w1", UNKNOWN_RED)
        s.press("ctrl+c")
        s.type("Get-Date")
        _wait_color(s, "Get-Date", COMMAND_GREEN)


def test_w1_ctrl_c_clears_line(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.type("Write-Output 'nope-w1'")
        s.wait_for("nope-w1")
        s.press("ctrl+c")
        s.wait_for("^C")
        s.run("Write-Output 'after-w1'", "after-w1")
        _wait_line(s, "after-w1")
        assert "nope-w1" not in _lines(s), s.text()


def test_w1_paste_burst_does_not_run(p):
    """Newlines inside a burst are inserted literally; only the user's own Enter runs the pasted lines."""
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.type("Write-Output 'pa-w1'\nWrite-Output 'pb-w1'")
        s.wait_for("pb-w1")
        time.sleep(1.0)
        lines = _lines(s)
        assert "pa-w1" not in lines and "pb-w1" not in lines, s.text()
        s.press("enter")
        _wait_line(s, "pa-w1")
        _wait_line(s, "pb-w1")


def test_w1_autosuggestion_accept(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.run("Write-Output 'ghost-w1'", "ghost-w1")
        _wait_line(s, "ghost-w1")
        s.type("Write-Output 'gh")
        s.wait_for_count("Write-Output 'ghost-w1'", 2)
        s.press("right")
        s.press("enter")
        _wait_line(s, "ghost-w1", count=2)
