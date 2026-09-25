#!/usr/bin/env bash
# Build the Pickle RPM from a linux-x64 single-file binary.
#   packaging/rpm/build-rpm.sh <version> <path/to/pickle> [out-dir]      (needs rpmbuild: dnf install rpm-build)
# Example: scripts/publish.sh linux-x64 && packaging/rpm/build-rpm.sh 0.2.0 artifacts/publish/linux-x64/pickle dist
set -euo pipefail
version="${1:?version}" binary="${2:?path to the linux-x64 pickle binary}" out="${3:-dist}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
# RPM versions can't contain '-': 1.2.0-beta.1 becomes 1.2.0~beta.1 (sorts before 1.2.0, as intended).
rpm_version="${version//-/\~}"
top="$(mktemp -d)"
trap 'rm -rf "$top"' EXIT
mkdir -p "$top/SOURCES" "$out"
cp "$binary" "$top/SOURCES/pickle"
cp "$root/LICENSE" "$root/README.md" "$top/SOURCES/"
rpmbuild -bb "$root/packaging/rpm/pickle.spec" \
  --define "_topdir $top" \
  --define "pickle_version $rpm_version" \
  --target x86_64
find "$top/RPMS" -name '*.rpm' -exec cp {} "$out/" \;
ls -1 "$out"/*.rpm
