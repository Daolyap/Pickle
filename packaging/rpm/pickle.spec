# Binary RPM around the self-contained single-file build (scripts/publish.sh linux-x64 or the release artifact).
# Build with packaging/rpm/build-rpm.sh; nothing is compiled here.
%global debug_package %{nil}
%global _build_id_links none
# Stripping or otherwise post-processing a .NET single-file bundle corrupts it.
%global __os_install_post %{nil}
%global __requires_exclude_from ^/usr/lib/pickle/.*$
%global __provides_exclude_from ^/usr/lib/pickle/.*$

Name:           pickle
Version:        %{pickle_version}
Release:        1
Summary:        A PowerShell 7 shell with a modern editor, panels and Linux-friendly commands
License:        MIT
URL:            https://github.com/Daolyap/Pickle
Source0:        pickle
Source1:        LICENSE
Source2:        README.md
ExclusiveArch:  x86_64
AutoReqProv:    no

# What the .NET runtime inside the single file loads dynamically (same list as Fedora's dotnet-runtime-deps).
Requires:       glibc
Requires:       libgcc
Requires:       libstdc++
Requires:       libicu
Requires:       openssl-libs
Requires:       krb5-libs
Requires:       zlib

%description
Pickle hosts the real PowerShell 7 engine and replaces the interactive experience: syntax highlighting,
autosuggestions, fuzzy history, a completion menu, a themeable prompt, full-screen panels (files, git,
jobs, settings, command wizards), aliases, plugins, sync and Linux-syntax translation.

%prep

%build

%install
install -Dm0755 %{SOURCE0} %{buildroot}/usr/lib/pickle/pickle
install -d %{buildroot}%{_bindir}
ln -s ../lib/pickle/pickle %{buildroot}%{_bindir}/pickle
install -Dm0644 %{SOURCE1} %{buildroot}%{_licensedir}/%{name}/LICENSE
install -Dm0644 %{SOURCE2} %{buildroot}%{_docdir}/%{name}/README.md

%files
/usr/lib/pickle/pickle
%{_bindir}/pickle
%license %{_licensedir}/%{name}/LICENSE
%doc %{_docdir}/%{name}/README.md
