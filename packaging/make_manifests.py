#!/usr/bin/env python3
"""
Generate package-manager manifests for a release from the built artifacts in dist/.

    python3 packaging/make_manifests.py --version 0.2.0 --dist dist --repo Daolyap/Pickle --out dist/manifests

Outputs:
  scoop/pickle.json                                  Scoop manifest (also committed to bucket/pickle.json)
  winget/Daolyap.Pickle.yaml (+ .installer/.locale)  winget multi-file manifest (portable exe + MSI)
"""

import argparse
import hashlib
import json
import os

PACKAGE_ID = "Daolyap.Pickle"
DESCRIPTION = "A PowerShell 7 shell with superpowers: autosuggestions, syntax highlighting, themes, panels, winget and Windows Update management."


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--version", required=True)
    p.add_argument("--dist", required=True)
    p.add_argument("--repo", required=True)
    p.add_argument("--out", required=True)
    a = p.parse_args()
    v = a.version
    base = f"https://github.com/{a.repo}/releases/download/v{v}"

    def asset(name):
        path = os.path.join(a.dist, name)
        return (f"{base}/{name}", sha256(path)) if os.path.exists(path) else (None, None)

    x64_zip = asset(f"pickle-{v}-win-x64.zip")
    arm_zip = asset(f"pickle-{v}-win-arm64.zip")
    x64_exe = asset(f"pickle-{v}-win-x64.exe")
    arm_exe = asset(f"pickle-{v}-win-arm64.exe")
    x64_msi = asset(f"pickle-{v}-win-x64.msi")
    arm_msi = asset(f"pickle-{v}-win-arm64.msi")

    # Scoop
    scoop = {
        "version": v,
        "description": DESCRIPTION,
        "homepage": f"https://github.com/{a.repo}",
        "license": "MIT",
        "architecture": {},
        "bin": "pickle.exe",
        "post_install": ["& \"$dir\\pickle.exe\" --install-terminal-profile | Out-Null"],
        "checkver": {"github": f"https://github.com/{a.repo}"},
        "autoupdate": {"architecture": {
            "64bit": {"url": f"https://github.com/{a.repo}/releases/download/v$version/pickle-$version-win-x64.zip"},
            "arm64": {"url": f"https://github.com/{a.repo}/releases/download/v$version/pickle-$version-win-arm64.zip"},
        }},
    }
    if x64_zip[0]:
        scoop["architecture"]["64bit"] = {"url": x64_zip[0], "hash": x64_zip[1].lower()}
    if arm_zip[0]:
        scoop["architecture"]["arm64"] = {"url": arm_zip[0], "hash": arm_zip[1].lower()}
    os.makedirs(os.path.join(a.out, "scoop"), exist_ok=True)
    with open(os.path.join(a.out, "scoop", "pickle.json"), "w") as f:
        json.dump(scoop, f, indent=2)
        f.write("\n")

    # winget (manifest schema 1.9)
    wdir = os.path.join(a.out, "winget")
    os.makedirs(wdir, exist_ok=True)
    schema = "1.9.0"
    installers = []
    for arch, (url, digest) in (("x64", x64_exe), ("arm64", arm_exe)):
        if url:
            installers.append(
                f"- Architecture: {arch}\n  InstallerType: portable\n  InstallerUrl: {url}\n  InstallerSha256: {digest}\n"
                f"  Commands:\n  - pickle\n")
    for arch, (url, digest) in (("x64", x64_msi), ("arm64", arm_msi)):
        if url:
            installers.append(
                f"- Architecture: {arch}\n  InstallerType: wix\n  Scope: machine\n  InstallerUrl: {url}\n  InstallerSha256: {digest}\n")
    with open(os.path.join(wdir, f"{PACKAGE_ID}.installer.yaml"), "w") as f:
        f.write(f"# yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.{schema}.schema.json\n"
                f"PackageIdentifier: {PACKAGE_ID}\nPackageVersion: {v}\nMinimumOSVersion: 10.0.17763.0\n"
                f"Installers:\n{''.join(installers)}ManifestType: installer\nManifestVersion: {schema}\n")
    with open(os.path.join(wdir, f"{PACKAGE_ID}.locale.en-US.yaml"), "w") as f:
        f.write(f"# yaml-language-server: $schema=https://aka.ms/winget-manifest.defaultLocale.{schema}.schema.json\n"
                f"PackageIdentifier: {PACKAGE_ID}\nPackageVersion: {v}\nPackageLocale: en-US\n"
                f"Publisher: Pickle contributors\nPublisherUrl: https://github.com/{a.repo}\n"
                f"PackageName: Pickle\nPackageUrl: https://github.com/{a.repo}\nLicense: MIT\n"
                f"LicenseUrl: https://github.com/{a.repo}/blob/main/LICENSE\n"
                f"ShortDescription: {DESCRIPTION}\nMoniker: pickle\nTags:\n- shell\n- powershell\n- terminal\n- winget\n"
                f"ManifestType: defaultLocale\nManifestVersion: {schema}\n")
    with open(os.path.join(wdir, f"{PACKAGE_ID}.yaml"), "w") as f:
        f.write(f"# yaml-language-server: $schema=https://aka.ms/winget-manifest.version.{schema}.schema.json\n"
                f"PackageIdentifier: {PACKAGE_ID}\nPackageVersion: {v}\nDefaultLocale: en-US\n"
                f"ManifestType: version\nManifestVersion: {schema}\n")
    print(f"manifests written to {a.out}")


if __name__ == "__main__":
    main()
