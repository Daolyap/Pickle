# Pickle.Network

Built-in network tools that work without installing anything (nmap-style port scans and host discovery, a DNS client,
traceroute, whois, TLS certificate inspection, subnet maths, Wake-on-LAN, HTTP timing). Pure .NET on every OS;
depends only on Abstractions.

- Engines are static classes/records returning data (`IAsyncEnumerable` for long scans); `Commands/` turns them into
  `pk` commands that emit objects (`Display.Columns`) and the Tui "Network tools" panel formats them as lines.
- Everything takes a `CancellationToken` and a timeout; scans bound their concurrency. Never shell out.
- Tests stay offline: loopback listeners (`TcpListener` on port 0, a UDP socket answering canned DNS replies) and
  parsers over captured bytes/text.
