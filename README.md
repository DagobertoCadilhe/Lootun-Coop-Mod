# LootunCoop

Work-in-progress co-op multiplayer mod for **Lootun** ([BepInEx 5](https://github.com/BepInEx/BepInEx)): up to 3 players, direct IP over a VPN (Radmin etc.),
normal missions first, raids later. Personal / friends use.

**Status: skeleton.** The plugin loads and logs; there is no networking yet. See [`docs/MULTIPLAYER.md`](docs/MULTIPLAYER.md) for the architecture
(host-authoritative simulation, TCP transport, character upload, state snapshots), phases and open risks.

## Requirements
Lootun (Steam, Windows) - BepInEx 5.4.x x64 Mono (or r2modman) - Visual Studio 2022 (.NET desktop workload, .NET Framework 4.7.2 targeting pack).

## Build & install
1. Create `GameDir.user.txt` in the repo root (git-ignored) with your game path, e.g. `D:\SteamLibrary\steamapps\common\Lootun` (or pass `-p:GameDir=...`).
2. Open `LootunCoop.csproj` in Visual Studio and build. The DLL is copied to `<GameDir>\BepInEx\plugins\LootunCoop\` if that folder exists
   (r2modman: copy it into your profile's `BepInEx\plugins`).
3. Start the game; `BepInEx\LogOutput.log` should contain `LootunCoop 0.0.1 loaded`.

## Layout
```
src/Plugin.cs         BepInEx entry point
src/AccessChecks.cs   SkipVerification attribute (lets us use internal game members)
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
