"""Animated themes in a real pty: the prompt keeps changing color while idle, typing still works, and "off" holds still."""

import time

from pty_harness import PickleSession


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
