"""W2 end-to-end: Tab completion, the completion menu and Ctrl+R history search in a real terminal."""

import time

from pty_harness import PickleSession


def _tab_until(s, expected, attempts=3, timeout=6.0):
    # The first completion in a fresh process may hit the 1.5 s budget while PowerShell builds its command cache;
    # a later Tab is then fast.
    for attempt in range(attempts):
        s.press("tab")
        try:
            return s.wait_for(expected, timeout=timeout)
        except AssertionError:
            if attempt == attempts - 1:
                raise
    return None


def test_w2_tab_completes_command(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.type("Get-ChildI")
        _tab_until(s, "Get-ChildItem")
        s.press("ctrl+c")


def test_w2_tab_completes_pk_subcommand(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.type("pk hist")
        _tab_until(s, "pk history")
        s.type(" list\r")
        s.wait_for("pk history list", timeout=10)


def test_w2_tab_opens_menu_and_accepts(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.type("Get-ChildItem -Fo")
        _tab_until(s, "FollowSymlink")
        s.wait_for("[switch]")
        s.press("down", "enter")
        s.wait_for("Get-ChildItem -FollowSymlink")
        s.press("ctrl+c")


def test_w2_ctrl_r_finds_previous_command(p):
    marker = f"w2-marker-{int(time.time() * 1000) % 100000}"
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.run(f"Write-Output '{marker}'", marker)
        s.run("Write-Output 'something else'", "something else")
        s.press("ctrl+r")
        s.wait_for("history ❯")
        s.type("mark")
        s.wait_for(f"Write-Output '{marker}'")
        s.press("enter")
        time.sleep(0.3)
        s.press("enter")
        s.wait_for_count(marker, 4, timeout=10)


def test_tab_keeps_completing_inside_a_quoted_directory(p):
    with PickleSession(p) as s:
        import os

        os.makedirs(os.path.join(s.home, "My Folder", "Sub Dir"), exist_ok=True)
        s.wait_for_prompt()
        s.type(f"Get-ChildItem '{s.home}/My Fo")
        _tab_until(s, "My Folder/'")
        s.press("tab")
        s.wait_for("My Folder/Sub Dir/'")
        s.type("\r")
        s.wait_for_prompt()
        s.run("Write-Output 'quoted-ok'", "quoted-ok")
