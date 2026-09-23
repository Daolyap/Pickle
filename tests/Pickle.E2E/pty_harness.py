"""
Real-terminal end-to-end harness for Pickle (Linux/macOS).

Spawns the pickle binary inside a pseudo-terminal, feeds its output through pyte (a VT100 emulator) and lets tests
type keys and assert on the rendered screen — exactly what a user would see. Requires: pip install pyte

    from pty_harness import PickleSession
    with PickleSession(pickle_path) as s:
        s.wait_for_prompt()
        s.type("Write-Output hi\r")
        s.wait_for("hi")
"""

import os
import pty
import select
import shutil
import signal
import tempfile
import threading
import time
import json
import struct
import fcntl
import termios

import pyte

KEYS = {
    "enter": "\r",
    "esc": "\x1b",
    "tab": "\t",
    "backspace": "\x7f",
    "up": "\x1b[A",
    "down": "\x1b[B",
    "right": "\x1b[C",
    "left": "\x1b[D",
    "home": "\x1b[H",
    "end": "\x1b[F",
    "f1": "\x1bOP",
    "f2": "\x1bOQ",
    "ctrl+c": "\x03",
    "ctrl+d": "\x04",
    "ctrl+r": "\x12",
    "ctrl+t": "\x14",
    "ctrl+l": "\x0c",
}


def alt(ch: str) -> str:
    return "\x1b" + ch


class PickleSession:
    def __init__(self, pickle_path, cols=100, rows=30, config=None, args=None, env=None):
        self.pickle_path = pickle_path
        self.cols = cols
        self.rows = rows
        self.home = tempfile.mkdtemp(prefix="pickle-e2e-")
        self.args = args or ["--no-logo"]
        self.env = dict(os.environ)
        self.env.update({"PICKLE_HOME": self.home, "TERM": "xterm-256color", "COLUMNS": str(cols), "LINES": str(rows)})
        if env:
            self.env.update(env)
        cfg = {"shell": {"showStartupBanner": False}}
        if config:
            _deep_merge(cfg, config)
        os.makedirs(os.path.join(self.home, "config"), exist_ok=True)
        with open(os.path.join(self.home, "config", "config.json"), "w") as f:
            json.dump(cfg, f)
        self.screen = pyte.Screen(cols, rows)
        self.stream = pyte.ByteStream(self.screen)
        self.lock = threading.Lock()
        self.raw = bytearray()
        self.pid = None
        self.fd = None
        self.alive = False

    def __enter__(self):
        self.start()
        return self

    def __exit__(self, *exc):
        self.close()

    def start(self):
        pid, fd = pty.fork()
        if pid == 0:
            os.execve(self.pickle_path, [self.pickle_path] + self.args, self.env)
        self.pid, self.fd = pid, fd
        fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", self.rows, self.cols, 0, 0))
        self.alive = True
        threading.Thread(target=self._reader, daemon=True).start()

    def _reader(self):
        while self.alive:
            try:
                r, _, _ = select.select([self.fd], [], [], 0.05)
                if not r:
                    continue
                data = os.read(self.fd, 65536)
                if not data:
                    break
                with self.lock:
                    self.raw.extend(data)
                    self.stream.feed(data)
            except OSError:
                break
        self.alive = False

    def type(self, text: str, delay=0.0):
        for ch in text:
            os.write(self.fd, ch.encode())
            if delay:
                time.sleep(delay)
        if not delay:
            pass

    def press(self, *keys):
        for k in keys:
            os.write(self.fd, KEYS.get(k.lower(), k).encode())
            time.sleep(0.05)

    def text(self) -> str:
        with self.lock:
            return "\n".join(line.rstrip() for line in self.screen.display).rstrip()

    def wait_for(self, needle: str, timeout=20.0) -> str:
        deadline = time.time() + timeout
        while time.time() < deadline:
            t = self.text()
            if needle in t:
                return t
            if not self.alive:
                break
            time.sleep(0.05)
        raise AssertionError(f"Timed out waiting for {needle!r}. Screen:\n{self.text()}")

    def wait_for_count(self, needle: str, count: int, timeout=20.0) -> str:
        deadline = time.time() + timeout
        while time.time() < deadline:
            t = self.text()
            if t.count(needle) >= count:
                return t
            time.sleep(0.05)
        raise AssertionError(f"Timed out waiting for {count}x {needle!r}. Screen:\n{self.text()}")

    def wait_for_prompt(self, timeout=30.0) -> str:
        return self.wait_for("❯", timeout)

    def run(self, command: str, expect: str, timeout=20.0) -> str:
        self.type(command + "\r")
        return self.wait_for(expect, timeout)

    def clear(self):
        """Clear the screen with Ctrl+L-independent approach: Clear-Host."""
        self.type("Clear-Host\r")
        time.sleep(0.3)

    def exit_code(self, timeout=10.0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            pid, status = os.waitpid(self.pid, os.WNOHANG)
            if pid:
                return os.waitstatus_to_exitcode(status)
            time.sleep(0.05)
        return None

    def close(self):
        self.alive = False
        try:
            os.kill(self.pid, signal.SIGKILL)
            os.waitpid(self.pid, 0)
        except (ProcessLookupError, ChildProcessError, TypeError):
            pass
        try:
            os.close(self.fd)
        except (OSError, TypeError):
            pass
        shutil.rmtree(self.home, ignore_errors=True)


def _deep_merge(a, b):
    for k, v in b.items():
        if isinstance(v, dict) and isinstance(a.get(k), dict):
            _deep_merge(a[k], v)
        else:
            a[k] = v
