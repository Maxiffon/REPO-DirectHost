<img src="package/icon.png" width="128" align="right" alt="">

# REPO DirectHost

A BepInEx mod for **R.E.P.O.**: the Steam lobby host runs its own Photon server inside the game, and friends
connect to it by the fastest path available — direct UDP, NAT punch-through, Steam P2P, a virtual LAN or the Steam
relay — picked automatically, with no Photon cloud in between.

**[Download the latest release](https://github.com/Maxiffon/REPO-DirectHost/releases/latest)**

| File | For |
|---|---|
| `REPO_DirectHost-<version>-installer.exe` | Players without a mod manager. Finds the game through Steam and installs BepInEx 5 + the mod into the game folder; it can also remove both again. |
| `REPO_DirectHost-<version>-r2modman.zip` | r2modman / Thunderstore Mod Manager: *Settings → Import local mod*. |

- **Everyone in the lobby needs the mod, same version.**
- Do not install together with Makfly REPO_Steam_P2P — the mod refuses to load next to it.
- The installer is not code-signed, so Windows SmartScreen may warn: *More info → Run anyway*.

## How players connect

Chosen automatically when a player enters the Steam lobby:

1. **Direct** — the host's public IP, or the same home network. Fastest. Needs UDP 7777 + 7778 forwarded on the host's router.
2. **Direct, punched** — when the host cannot be reached by its address (provider CGNAT, no port forwarding):
   - **own NAT punch-through** — host and player exchange their public UDP endpoints through Steam lobby data and
     open a direct UDP link from both sides (works even when only one side is "open");
   - **Steam P2P** — Steam's own punch-through.

   The faster of the two is used.
3. **Virtual LAN** — Radmin VPN / ZeroTier / Hamachi / Tailscale, if both players are in the same network.
4. **Steam relay** — Valve's relays, only when nothing else works.

Paths 2 and 4 share one tunnel: if a direct link comes up later, or dies, the traffic moves over mid-game without
reconnecting. The host's `BepInEx\LogOutput.log` lists every player's ping and path every 30 s (`players:` lines);
the player's log shows the chosen path in `route:` lines.

### Settings (`BepInEx\config\kot.repo.directhost.cfg`)

- Host: if the detected public IP is wrong (VPN), set `[Network] PublicAddress`.
- Player: `[Network] HostAddress` forces an address; `[Network] Route` = `Auto` / `Direct` / `Steam`.
- For the Steam P2P path both players should allow it in Steam: *Settings → In Game → Steam Networking*
  ("Always" or "Friends only").
- If the log says "UDP 777x is also used by another program": close that program (an old relay or another P2P tool).

## Building

Needs the .NET SDK 8, R.E.P.O. and BepInExPack 5.4.2305 (e.g. an r2modman profile).

```powershell
dotnet build src/RepoDirectHost.csproj -c Release `
  -p:GameManaged="<game folder>\REPO_Data\Managed" -p:BepInExCore="<BepInEx folder>\core"
```

Tests run the real server against the game's own Photon client DLLs over simulated lossy links
(rooms, tunnel, NAT punch, link switching):

```powershell
dotnet run --project tests/DirectHost.Tests -c Release -p:GameManaged="<game folder>\REPO_Data\Managed"
```

`installer/build.ps1` builds the standalone installer from the release zip and the BepInExPack in the r2modman cache.

## По-русски

Хост лобби запускает свой Photon-сервер прямо в игре, друзья подключаются к нему самым быстрым путём: напрямую,
через пробивание NAT / Steam P2P, виртуальную сеть или ретранслятор Steam. Выбор автоматический, настраивать ничего не нужно.
Скачать — в [Releases](https://github.com/Maxiffon/REPO-DirectHost/releases/latest): `installer.exe` — без r2modman
(сам находит игру и ставит BepInEx + мод), `r2modman.zip` — импорт в r2modman. Мод нужен всем в лобби, одной версии.

## License

MIT — see [LICENSE](LICENSE). The client redirect idea (UseNameServer=false + custom server address) comes from
[REPO-SelfHostedPhoton](https://github.com/viabiko/REPO-SelfHostedPhoton) (MIT, see `package/THIRD-PARTY-NOTICES.md`);
the server, transport and protocol code are original. The installer bundles BepInEx and its components under their
own licenses (`installer/BEPINEX-NOTICES.md`, `installer/licenses/`). Not affiliated with semiwork or Photon.
