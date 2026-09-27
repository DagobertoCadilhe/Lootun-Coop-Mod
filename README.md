# LootunCoop

Work-in-progress co-op multiplayer mod for **Lootun** ([BepInEx 5](https://github.com/BepInEx/BepInEx)): up to 3 players, direct IP over a VPN (Radmin etc.),
normal missions first, raids later. Personal / friends use.

**Status: transport only.** Players can host/join over TCP and chat through a test panel (F7); no game state is shared yet. See [`docs/MULTIPLAYER.md`](docs/MULTIPLAYER.md) for the architecture
(host-authoritative simulation, TCP transport, character upload, state snapshots), phases and open risks.

## Requirements
Lootun (Steam, Windows) - BepInEx 5.4.x x64 Mono (or r2modman) - Visual Studio 2022 (.NET desktop workload, .NET Framework 4.7.2 targeting pack).

## Build & install
1. Create `GameDir.user.txt` in the repo root (git-ignored) with your game path, e.g. `D:\SteamLibrary\steamapps\common\Lootun` (or pass `-p:GameDir=...`).
2. Open `LootunCoop.csproj` in Visual Studio and build. The DLL is copied to `<GameDir>\BepInEx\plugins\LootunCoop\` if that folder exists
   (r2modman: copy it into your profile's `BepInEx\plugins`).
3. Start the game; `BepInEx\LogOutput.log` should contain `LootunCoop 0.0.1 loaded`.

## Testing a connection
1. Both players: same LootunCoop commit and the **same BepInEx plugins** (the host rejects any mismatch and says which mod differs).
2. Host: press **F7**, set a name, click **Host**. Allow the game through the Windows firewall (TCP port 28777 by default,
   `BepInEx\config\personal.lootuncoop.cfg`).
3. Others: F7, enter the host's VPN IP (e.g. Radmin), click **Join**. The panel shows players, ping and a test chat.
   To test alone, run the host and join `127.0.0.1`.

## Network tests (no game needed)
`dotnet run --project tests/LootunCoop.NetTests -f net8.0` runs host/client scenarios over localhost (handshake, full session,
mod/protocol mismatch, 1 MB messages, leave/kick/timeout, garbage input). It builds `src/Net` for net472 too, to catch APIs the game's Mono lacks.

## Layout
```
src/Plugin.cs         BepInEx entry point, config, Update/OnGUI hooks
src/CoopSession.cs    owns the host or client for this game instance; chat
src/UI/CoopPanel.cs   F7 test panel (IMGUI)
src/Net/              transport: framing, handshake, heartbeats (no Unity/game types, so it is testable outside the game)
src/AccessChecks.cs   SkipVerification attribute (lets us use internal game members)
tests/                loopback tests for src/Net
docs/MULTIPLAYER.md   design + phases
CLAUDE.md             notes for AI assistants
```

## Contributing
- `main` always builds; branch as `feature/<name>`, merge via PR. Claim a task from the phases list before starting.
- Everyone in a co-op session must run the **same commit**.
- Never commit game DLLs or decompiled game code (decompile locally with ILSpy into `game-decompile/`).
- Suggested split: networking layer (TCP, framing, handshake, version check) vs. game side (character serialization, joining an `Encounter`, state mirror).

## Related
[RoguePoison](../RoguePoison) - the custom Rogue class mod. The two mods are independent and can run together.
