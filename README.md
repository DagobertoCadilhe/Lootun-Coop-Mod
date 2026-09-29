# LootunCoop

Work-in-progress co-op multiplayer mod for **Lootun** ([BepInEx 5](https://github.com/BepInEx/BepInEx)): up to 3 players, direct IP over a VPN (Radmin etc.),
normal missions first, raids later. Personal / friends use.

**Status: playable, rough.** One player hosts; the others bring one character each into a co-op mission that runs on the host's game and is
shown live on theirs, with XP, loot and currency split between players. First real playtests done; see [`docs/MULTIPLAYER.md`](docs/MULTIPLAYER.md)
for the architecture (host-authoritative simulation, TCP transport, character upload, interpolated state snapshots), phases and open risks.

## Requirements
- Everyone: Lootun (Steam, Windows) and BepInEx 5.4.x x64 Mono (easiest: [r2modman](https://thunderstore.io/package/ebkr/r2modman/) with the **BepInExPack** package).
- Only whoever builds the DLL: Visual Studio 2022 (.NET desktop workload, .NET Framework 4.7.2 targeting pack) or the .NET SDK (`dotnet build`).

## Build & install (developers)
1. Game path: defaults to `D:\SteamLibrary\steamapps\common\Lootun`. If yours differs, put it in `GameDir.user.txt` in the repo root (or pass `-p:GameDir=...`).
2. BepInEx path: defaults to `<GameDir>\BepInEx`. **With r2modman, BepInEx lives in the profile instead**, so put the profile's BepInEx folder in
   `BepInExDir.user.txt`, e.g. `C:\Users\<you>\AppData\Roaming\r2modmanPlus-local\Lootun\profiles\Default\BepInEx`.
   Without it the build fails with "type or namespace `BepInEx`/`HarmonyLib` not found".
3. Open `LootunCoop.csproj` in Visual Studio (make sure it's *this* folder, not an old copy) and build. The DLL is copied to
   `<BepInExDir>\plugins\LootunCoop\` automatically. To overwrite a specific file instead, put its full path in `DeployPath.user.txt`.
   **Close the game first**: while it runs the DLL is locked and the copy step fails (`MSB3027`), even though the code compiled.
4. Start the game; `BepInEx\LogOutput.log` should contain `LootunCoop 0.0.1 loaded`.

All `*.user.txt` files are personal and git-ignored.

## Install without building (friends)
Nobody else needs Visual Studio. Whoever built it sends `LootunCoop.dll` (from `bin\Debug\net472\` or `bin\Release\net472\`); the friend:
1. Installs BepInExPack for Lootun (r2modman: create a profile, install BepInExPack).
2. Puts the DLL in `<profile>\BepInEx\plugins\LootunCoop\LootunCoop.dll` (create the folder).
3. Starts the game through that profile and checks `LogOutput.log` as above.

Every time the DLL changes, **everyone** needs the new one: the host refuses players on a different build (see below).

## Playing co-op
**Before connecting**
- Same LootunCoop build on every machine, and the **same BepInEx plugins and versions** (e.g. all with or all without RoguePoison).
  The host rejects any mismatch and says why. Before a session, agree on the commit/DLL you're all using.
- Network: the joiners must reach the host's TCP port **28777**. Easiest is a VPN like Radmin VPN, ZeroTier or Hamachi (use the host's VPN IP).
  Without a VPN, the host must forward TCP 28777 on their router and give out their public IP. Either way, allow the game through the host's
  Windows firewall when asked.

**Connecting**
1. Host: press **F7** (or the **Co-op** button in the left menu), set a name, click **Host**.
2. Others: F7, enter the host's IP, click **Join**. The panel lists players with their **ping**, plus a chat.
3. Each joiner selects the character they want to bring. It's sent automatically (and re-sent when you change gear, skills, passives or level);
   it must not be running in one of your own missions. Keep **one mission slot free**: that's where you watch the co-op fight.

**Starting a mission (host)**
Mission board -> pick a normal mission -> Prepare Mission -> pick your own character(s) -> **Begin Co-op**. Your characters plus the guests must fit
the 3 party slots; slots show "Waiting for X..." until that player's character arrives. Raids, bounties, endless, dummies and faction missions aren't
supported yet.

**Testing alone:** host, then join `127.0.0.1` from a second game instance; or use F7 -> *Testing tools* to add a clone of your own character as a guest.

## Settings
`BepInEx\config\personal.lootuncoop.cfg` (created on first launch):

| Setting | Default | What it does |
|---|---|---|
| `PlayerName` | `Player` | Your name in the session. |
| `HostAddress` | *(empty)* | Last host IP you joined. |
| `Port` | `28777` | TCP port (host listens on it, joiners connect to it). |
| `MaxPlayers` | `3` | Players per session including the host (host only). |
| `InterpolationDelayMs` | `100` | Joiners only: how far behind the host the fight is shown, so it can be smoothed. Raise to 150-200 if bars still stutter on a bad connection; `0` turns smoothing off. |

## Troubleshooting
| You see | Meaning / fix |
|---|---|
| `protocol mismatch (host X, you Y); use the same LootunCoop build` | Different LootunCoop builds. Everyone installs the same DLL. |
| `mod list mismatch: ...` (`version differs`, `you are missing ...`, `host does not have ...`) | Your BepInEx plugins differ from the host's. Match them. |
| `session is full` | Host's `MaxPlayers` reached. |
| `could not connect: no answer after 5 s ...` | Host unreachable: wrong IP, host not hosting yet, VPN not connected, firewall, or port not forwarded. |
| "free one mission slot to watch the co-op mission" | End one of your missions; the co-op fight needs a slot on your side. |
| Fight looks choppy on the joiner's side | Check ping in the F7 panel; raise `InterpolationDelayMs`. |
| Build error `MSB3027` / file in use | The game is open. Close it and build again. |

## Tests (no game needed)
`dotnet run --project tests/LootunCoop.NetTests -f net8.0` runs host/client scenarios over localhost (handshake, full session,
mod/protocol mismatch, 1 MB messages, leave/kick/timeout, garbage input), message round-trips, and the snapshot playback timing
under simulated jitter and stalls. It builds `src/Net` for net472 too, to catch APIs the game's Mono lacks.

## Layout
```
src/Plugin.cs         BepInEx entry point, config
src/CoopRunner.cs     persistent GameObject driving Update/OnGUI
src/CoopSession.cs    owns the host or client for this game instance; chat, character upload
src/Game/             game side: guest characters and the host's co-op mission, the client's mirror, snapshots,
                      rewards, character lock, UI patches (Harmony)
src/Net/              transport and wire messages, snapshot playback timing (no Unity/game types, so it is testable outside the game)
src/UI/               F7 panel (IMGUI) and the Co-op menu button
src/AccessChecks.cs   SkipVerification attribute (lets us use internal game members)
tests/                tests for src/Net
docs/MULTIPLAYER.md   design, phases, protocol
CLAUDE.md             notes for AI assistants
```

## Contributing
- `main` always builds and is what you playtest from. Small, confirmed fixes can go straight to `main`.
- New or risky behavior (especially anything untested in a real session) goes on a `feature/<name>` branch and is merged once a playtest shows it works.
- Before playing together, agree on the exact commit and all use it: everyone in a session must run the **same build**.
- Don't leave branches unmerged and forgotten; merge or delete them.
- Never commit game DLLs or decompiled game code (decompile locally with ILSpy into `game-decompile/`).

## Related
[RoguePoison](../RoguePoison) - the custom Rogue class mod. The two mods are independent and can run together.
