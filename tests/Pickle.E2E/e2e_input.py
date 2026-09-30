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


def _click(s, x, y):
    """Left click at 0-based screen cell (x, y) as SGR mouse reports."""
    s.press(f"\x1b[<0;{x + 1};{y + 1}M")
    time.sleep(0.05)
    s.press(f"\x1b[<0;{x + 1};{y + 1}m")


def _find(s, needle):
    for y, line in enumerate(s.text().splitlines()):
        if needle in line:
            return line.index(needle), y
    raise AssertionError(f"{needle!r} not on screen")


def test_clicking_a_network_tools_text_box_lets_you_type_in_it(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        time.sleep(0.3)
        s.press(alt("t"))
        s.wait_for("Network tools", timeout=20)
        s.wait_for("top100", timeout=10)
        time.sleep(0.5)
        x, y = _find(s, "│top100")
        _click(s, x + 9, y)
        time.sleep(0.5)
        s.press(",8080")
        s.wait_for("top100,8080", timeout=5)
        s.press("\x1b")


def _empty_the_targets_box(s):
    s.press(alt("t"))
    s.wait_for("Network tools", timeout=20)
    s.wait_for("127.0.0.1", timeout=10)
    time.sleep(0.5)
    x, y = _find(s, "127.0.0.1")
    _click(s, x + 20, y)
    time.sleep(0.5)
    s.press("end")
    for _ in range(12):
        s.press("backspace")
    deadline = time.time() + 5
    while "127.0.0" in s.text() and time.time() < deadline:
        time.sleep(0.1)
    assert "127.0.0" not in s.text(), "the box did not empty"


def test_a_paste_into_a_network_tools_box_shows_without_another_key(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        time.sleep(0.3)
        _empty_the_targets_box(s)
        s.press(PASTE_START + "12.12.12.12" + PASTE_END)
        s.wait_for("│12.12.12.12", timeout=5)
        s.press("\x1b")


def test_a_paste_typed_as_a_burst_of_keys_shows_without_another_key(p):
    # Where the terminal doesn't bracket pastes (or ConPTY strips the markers) a paste is just fast typing.
    with PickleSession(p) as s:
        s.wait_for_prompt()
        time.sleep(0.3)
        _empty_the_targets_box(s)
        s.press("10.20.30.40")
        s.wait_for("│10.20.30.40", timeout=5)
        s.press("\x1b")


def test_the_wheel_scrolls_a_wizard_form(p):
    with PickleSession(p, cols=120, rows=35) as s:
        s.wait_for_prompt()
        time.sleep(0.3)
        s.press("pk wizard nmap\r")
        s.wait_for("── Targets ──", timeout=20)
        time.sleep(1.0)
        _, y = _find(s, "── Targets ──")
        for _ in range(3):
            s.press(f"\x1b[<65;40;{y + 3}M")
            time.sleep(0.1)
        deadline = time.time() + 5
        while "── Targets ──" in s.text() and time.time() < deadline:
            time.sleep(0.1)
        assert "── Targets ──" not in s.text(), "the form did not scroll"
        s.press("\x1b")
