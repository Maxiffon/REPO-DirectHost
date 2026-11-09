# REPO DirectHost 1.0.0

The Steam lobby host runs a Photon server inside the game. Friends connect to it by the best available path,
chosen automatically when they enter the Steam lobby (no configuration):

1. **Direct** — the host's public (white) IP, or the same home network. Fastest. Needs UDP 7777 + 7778 forwarded on the host's router.
2. **Direct, punched** — when the host cannot be reached by its address (provider CGNAT, no port forwarding):
   - **own NAT punch-through**: the host and the player exchange their public UDP endpoints through Steam lobby data
     and open a direct UDP link from both sides. It works even when only one side is "open", e.g. a host behind
     CGNAT and a player with a forwarded port (UDP 7779 is tried first on the player's side);
   - **Steam P2P**: Steam's own punch-through.
   The faster of the two is used.
3. **Virtual LAN** — Radmin VPN / ZeroTier / Hamachi / Tailscale, if both players are in the same network.
4. **Steam relay** — Valve's relays, only when nothing else works (or when it is clearly faster than the virtual LAN).

Paths 2 and 4 share one tunnel: if a direct link comes up later, or dies, the traffic moves over mid-game without
reconnecting. The host's log lists every player's ping and path every 30 s (`players:` lines); the client's log
shows the chosen path in `route:` lines.

- Everyone in the lobby needs this mod (same version). Do NOT install together with Makfly REPO_Steam_P2P (the mod refuses to load next to it).
- Without a mod manager: run `REPO_DirectHost-<version>-installer.exe` — it finds the game through Steam and installs
  BepInEx 5 + this mod into the game folder (it can also remove both again).
- If the log says "UDP 777x is also used by another program": close that program (an old relay or another P2P tool) —
  it receives the game's packets instead of the game.
- For the Steam P2P path both players should allow it in Steam: Settings > In Game > Steam Networking ("Always" or "Friends only").
- Host: if the detected public IP is wrong (VPN), set [Network] PublicAddress.
- Client: [Network] HostAddress forces an address; [Network] Route = Auto / Direct / Steam.

Credits: the client redirect idea (UseNameServer=false + custom server address, "eu" region fallback) comes from
REPO-SelfHostedPhoton — Copyright (c) 2025 zyxterus, MIT License (https://github.com/viabiko/REPO-SelfHostedPhoton).
The server, transport and protocol code are original.