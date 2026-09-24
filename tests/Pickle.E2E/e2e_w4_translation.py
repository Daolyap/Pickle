"""W4 end-to-end: Linux-syntax translation and aliases in a real terminal."""

from pty_harness import PickleSession


def test_w4_export_sets_env_var(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.run("export FOO=bar", "$env:FOO = 'bar'")
        s.run("Write-Output \"foo=$env:FOO\"", "foo=bar")


def test_w4_dev_null_redirection(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.run("Write-Output hi 2>/dev/null", "2>$null")
        s.wait_for_count("hi", 2)
        s.run("Get-Item /definitely/missing 2>/dev/null; Write-Output after-null", "after-null")
        assert "Cannot find path" not in s.text(), s.text()


def test_w4_env_prefix_and_alias(p):
    with PickleSession(p) as s:
        s.wait_for_prompt()
        s.run("W4VAR=scoped pwsh-noop 2>$null; Write-Output \"w4=[$env:W4VAR]\"", "w4=[]")
        s.run("pk alias add greet 'Write-Output hello-{who=world}'", "Alias greet")
        s.run("greet pickle", "hello-pickle")
