# Trying client 2.0.13 (experimental)

Released **2026-11-25**. Support is **optional** and **off the default path**: the server and launcher
still pin **2.0.9.0** (Steam build **26262663**) as the supported private build.

## Launcher

The Play page lists **2.0.13 (experimental, unverified)** only when a pak AES key is present in
`tools/data/client-build-profiles.json` (this tree does not ship that key). When listed, it uses your
Steam install the same way `steam-latest` does (read-only; no patch paks or private-build download).
The default selection remains **2.0.9.0**.

Host operators can also hide the entry with `Aion:Launcher:OfferClient2013=false` in appsettings.

## Pak export / tooling

**UE dumper offsets** for 2.0.13 live in `tools/data/client-build-profiles.json`
(also embedded for the server and launcher). The **AES pak key** is not published here;
`python tools/client_build_profiles.py key-report 2.0.13` exits until `aesPakKeyHex` is set locally.

```bash
python tools/client_build_profiles.py key-report 2.0.13
python tools/client_build_profiles.py dumper-offsets 2.0.13
```

For the pinned 2.0.9.0 build, keep using `research/local/archive-key-validated.json` locally (gitignored).

## Still unverified

| Item | Status |
|------|--------|
| Steam depot **build id** for 2.0.13 | **TODO** — not guessed; manifest unknown |
| **Opcodes** and per-packet wire layouts | Reuse pinned **26262663** tables (`protocolProfileId`); marked `protocolVerified: false` |
| In-game login / handshake with 2.0.13 | Not regression-tested on this repo yet |
| Usmap / dat export field decoding | Needs a 2.0.13 usmap when available |

Server-side behavior for a 2.0.13 session intentionally follows the pinned protocol profile until
captures prove otherwise. That does **not** change behavior for players on **2.0.9.0**.