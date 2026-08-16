# Security policy

If you believe you've found a security vulnerability in SwBridge, please report it privately
rather than opening a public issue: use
[GitHub's private security advisory reporting](https://github.com/meirka8/swbridge/security/advisories/new)
for this repository.

This project is maintained on a best-effort basis — no particular response time is guaranteed,
but security reports will be taken seriously and credited unless you ask otherwise.

## Scope

SwBridge is a local automation library: it attaches to an already-running SolidWorks instance on
the same machine and has no network-facing surface of its own. The most relevant class of report
is likely to be about the COM interop boundary (e.g. a way a malformed document or a crafted COM
response could cause unsafe behavior) rather than a classic remote-attack surface.
