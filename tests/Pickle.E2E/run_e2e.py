#!/usr/bin/env python3
"""
Pickle end-to-end tests in a real pseudo-terminal.

    python3 tests/Pickle.E2E/run_e2e.py [--pickle PATH] [-k NAME]

Default binary: src/Pickle/bin/Debug/net10.0/pickle (build first). Tests are functions named test_*(pickle_path)
in this file and in any sibling module named e2e_*.py (one module per feature area). Tests print the screen on
failure. Use unique temp file names — other runs may execute concurrently.
"""

import argparse
import glob
import importlib.util
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
        path = os.path.join(s.home, "vim-test.txt")
        s.type(f"vim -u NONE -N {path}\r")
        s.wait_for("vim-test.txt", timeout=15)
        s.type("ihello from vim")
        s.press("esc")
        s.type(":wq\r")
        s.run(f"Get-Content {path}", "hello from vim")
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


def test_pk_command_opens_panel_after_pipeline(p):
    """`pk git` runs inside a pipeline; the panel must open once the prompt is back, load, and close cleanly."""
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.type("pk git\r")
        s.wait_for("Esc to close", timeout=20)
        time.sleep(0.5)
        s.press("esc")
        time.sleep(0.4)
        s.run("Write-Output 'after-pk-git'", "after-pk-git", timeout=15)


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
    for path in sorted(glob.glob(os.path.join(os.path.dirname(__file__), "e2e_*.py"))):
        name = os.path.splitext(os.path.basename(path))[0]
        spec = importlib.util.spec_from_file_location(name, path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        tests += [(f"{name}.{n}", f) for n, f in sorted(vars(module).items()) if n.startswith("test_") and callable(f)]
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
