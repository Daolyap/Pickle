#!/usr/bin/env python3
"""
Pickle end-to-end tests in a real pseudo-terminal.

    python3 tests/Pickle.E2E/run_e2e.py [--pickle PATH] [-k NAME]

Default binary: src/Pickle/bin/Debug/net10.0/pickle (build first). Each test is a function named test_*; add new
ones at the bottom. Tests print the screen on failure.
"""

import argparse
import os
import sys
import time
import traceback

sys.path.insert(0, os.path.dirname(__file__))
from pty_harness import PickleSession, alt  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PICKLE = os.path.join(ROOT, "src", "Pickle", "bin", "Debug", "net10.0", "pickle")


def test_prompt_and_command(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.run("Write-Output ('pick' + 'led')", "pickled")


def test_native_command_exit_code(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.run("bash -c 'exit 3'; \"code=$LASTEXITCODE\"", "code=3")


def test_ctrl_c_interrupts_running_command(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.type("Start-Sleep -Seconds 30\r")
        time.sleep(1.0)
        s.press("ctrl+c")
        s.run("Write-Output 'still-alive'", "still-alive", timeout=10)


def test_vim_round_trip(p):
    """Spike (b): a full-screen native program gets the console and gives it back."""
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.type("vim -u NONE -N /tmp/pickle-e2e-vim.txt\r")
        s.wait_for("pickle-e2e-vim.txt", timeout=15)
        s.type("ihello from vim")
        s.press("esc")
        s.type(":wq\r")
        s.run("Get-Content /tmp/pickle-e2e-vim.txt", "hello from vim")
        s.run("Write-Output 'after-vim-ok'", "after-vim-ok")


def test_panel_open_close_repeatedly(p):
    """Spike (c): REPL -> Terminal.Gui panel -> REPL, ten times, and the shell still works."""
    with PickleSession(p, config={"keyBindings": {"Alt+A": "panel.about"}}) as s:
        s.wait_for_prompt()
        for i in range(10):
            s.press(alt("a"))
            s.wait_for("About Pickle", timeout=15)
            s.press("esc")
            time.sleep(0.4)
            s.run(f"Write-Output 'back-{i}'", f"back-{i}", timeout=15)


def test_exit(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.type("exit 4\r")
        code = s.exit_code()
        assert code == 4, f"exit code {code}"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--pickle", default=PICKLE)
    parser.add_argument("-k", default=None, help="only run tests whose name contains this")
    args = parser.parse_args()
    if not os.path.exists(args.pickle):
        print(f"pickle binary not found: {args.pickle} (run: dotnet build)")
        return 2

    tests = [(n, f) for n, f in sorted(globals().items()) if n.startswith("test_") and callable(f)]
    if args.k:
        tests = [(n, f) for n, f in tests if args.k in n]
    failed = 0
    for name, fn in tests:
        start = time.time()
        try:
            fn(args.pickle)
            print(f"PASS {name} ({time.time() - start:.1f}s)")
        except Exception:  # noqa: BLE001
            failed += 1
            print(f"FAIL {name} ({time.time() - start:.1f}s)")
            traceback.print_exc()
    print(f"\n{len(tests) - failed}/{len(tests)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
