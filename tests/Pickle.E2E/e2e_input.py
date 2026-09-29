"""Paste and mouse input in a real pty: bracketed paste at the prompt and in panels, clicks and the scroll wheel."""

import time

from pty_harness import PickleSession, alt

PASTE_START = "\x1b[200~"
PASTE_END = "\x1b[201~"


def _row(s, needle):
    with s.lock:
        return next((y for y in range(s.rows) if needle in "".join(c.data for c in s.screen.buffer[y].values())), None)


def test_bracketed_paste_at_the_prompt_is_inserted_whole(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        time.sleep(0.3)
        s.press(PASTE_START + "Write-Output ('pas' + 'ted')" + PASTE_END)
        s.wait_for("Write-Output ('pas' + 'ted')", timeout=10)
        assert "[201~" not in s.text()
        s.press("\r")
        s.wait_for("pasted", timeout=10)


def test_bracketed_paste_shows_at_once_in_a_panel_filter(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        time.sleep(0.3)
        s.press(alt("e"))
        s.wait_for("Themes  ·  Esc to close", timeout=20)
        s.wait_for("aurora", timeout=10)
        s.press(PASTE_START + "nord" + PASTE_END)
        s.wait_for("❯ nord", timeout=5)
        s.press("\x1b")
