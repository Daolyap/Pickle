"""Animated themes in a real pty: the prompt keeps changing color while idle, typing still works, and "off" holds still."""

import time

from pty_harness import PickleSession, alt


def _prompt_colors(s, samples=12, interval=0.25):
    """Distinct (foreground, background) patterns of the prompt row, sampled while the shell sits idle."""
    seen = set()
    for _ in range(samples):
        with s.lock:
            row = next((y for y in range(s.rows) if "❯" in "".join(c.data for c in s.screen.buffer[y].values())), None)
            if row is not None:
                cells = s.screen.buffer[row]
                seen.add(tuple((cells[x].fg, cells[x].bg) for x in range(s.cols)))
        time.sleep(interval)
    return seen


def test_animated_theme_moves_while_idle_and_typing_still_works(p):
    with PickleSession(p, config={"theme": "prism", "prompt": {"animation": "on"}}) as s:
        s.wait_for_prompt()
        patterns = _prompt_colors(s)
        assert len(patterns) >= 3, f"prompt barely changed: {len(patterns)} patterns"
        s.run("Write-Output ('anim' + 'ok')", "animok")
        s.run("pk theme list", "aurora")


def test_animation_off_keeps_the_prompt_still(p):
    with PickleSession(p, config={"theme": "prism", "prompt": {"animation": "off"}}) as s:
        s.wait_for_prompt()
        time.sleep(0.5)
        patterns = _prompt_colors(s, samples=6)
        assert len(patterns) == 1, f"prompt changed {len(patterns)} times with animation off"


def test_tab_progress_reaches_the_terminal_without_touching_the_screen(p):
    with PickleSession(p, config={"terminal": {"tabProgress": "on"}}) as s:
        s.wait_for_prompt()
        s.run("Start-Sleep -Milliseconds 300; Write-Output ('tab' + 'done')", "tabdone")
        s.run("Get-Item -LiteralPath /definitely/missing", "Cannot find path")
        time.sleep(0.5)
        with s.lock:
            raw = bytes(s.raw)
        assert b"\x1b]9;4;3;0\x07" in raw, "no spinner sequence"
        assert b"\x1b]9;4;0;0\x07" in raw, "no clear sequence"
        assert b"\x1b]9;4;2;100\x07" in raw, "no error sequence"
        assert "9;4" not in s.text(), "the sequence leaked onto the screen"


def test_theme_gallery_opens_with_alt_e_and_enter_applies(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        time.sleep(0.3)
        s.press(alt("e"))
        s.wait_for("Themes  ·  Esc to close", timeout=20)
        s.wait_for("aurora", timeout=10)
        s.type("nord")
        time.sleep(0.5)
        s.press("enter")
        time.sleep(0.8)
        s.run("(Get-PickleTheme).Name + '-applied'", "nord-applied", timeout=15)
