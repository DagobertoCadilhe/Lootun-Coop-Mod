# CLAUDE.md

Guidance for Claude Code (and other AI assistants) working in this repo. Humans: see `README.md`.

## What this is
**LootunCoop** — co-op multiplayer (up to 3 players, direct IP over VPN, normal missions first, raids later) as a BepInEx 5 + HarmonyLib
mod for **Lootun** (Unity 2021.3, Mono, game code in `Assembly-CSharp`, namespaces `LootClicker.*`). Host-authoritative: guests upload one
character each, the host runs the co-op mission, clients watch it through a non-ticking mirror fed by interpolated snapshots; rewards are split.
Playtested for real, still rough. `README.md` is the player/developer guide (install, connecting, settings, troubleshooting): keep it in sync
with anything that changes how people install, connect or play.
Design, phases and risks live in `docs/MULTIPLAYER.md` — read it first and keep it updated.
Answer the user in the language they use (Brazilian Portuguese or English). Be concise.
A sibling repo, **RoguePoison**, adds a custom class. Both mods may run together; keep this one independent of it (no shared IDs, no reference to it).

## Build
- Windows / Visual Studio: open `LootunCoop.csproj`. `GameDir` comes from `GameDir.user.txt` (git-ignored) or `-p:GameDir=`.
  Default: `D:\SteamLibrary\steamapps\common\Lootun`. With r2modman, BepInEx is in the profile, not the game folder: set
  `BepInExDir.user.txt` (else BepInEx/HarmonyLib types aren't found). `DeployPath.user.txt` overrides where the DLL is copied.
- The build copies the DLL into `<BepInExDir>\plugins\LootunCoop\`. If the game is running the copy fails (`MSB3027`, file locked)
  although compilation succeeded; to check compilation only, pass `-p:DeployPath=<some temp file>`.
- Beware of stale copies of this repo elsewhere on the user's disk (e.g. `Repos do Lootun\LootunCoop-repo`); confirm which folder they build.
- Target `net472`, `LangVersion latest`, nullable off. `Assembly-CSharp` is referenced with `Publicize="true"`
  (BepInEx.AssemblyPublicizer.MSBuild) because we override/call internal & protected members.
- Without the game installed you can still compile by pointing the references at a copy of `Lootun_Data\Managed` + `BepInEx\core`.
- You usually **cannot run the game**. Say clearly what is verified (compiles) vs. unverified (runtime behavior).

## Critical runtime gotcha
Unity's Mono ignores `IgnoresAccessChecksTo`. Access to internal members only works because each plugin declares
`[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]` + `[module: UnverifiableCode]`
(`src/AccessChecks.cs`). Keep them. A `MethodAccessException` in
`LogOutput.log` means this is missing or broken. Fallback if it ever fails: a BepInEx preloader patcher that publicizes
`Assembly-CSharp` at load time.

## Never commit
Game DLLs, decompiled game source (`game-decompile/`), `bin/`, `obj/`, `GameDir.user.txt`. `.gitignore` covers these; don't override it.
To read game code, decompile with ILSpy locally. Prefer reading real game classes (e.g. `PoisonSting`, `Assassin`, `AssassinPower`,
`Encounter`, `Character.SetupPassives`) before inventing API usage.

## Game facts relevant to co-op
- `Encounter(List<Character> characters, MissionBase map, bool levelScaling, int monsterLevel)`; 1–3 characters = normal mission, 4+ = raid (`GetRaidStatus()`).
- `Map.GetMonsterCount(Characters.Count)` already scales with party size.
- Combat is auto-cast, so only builds/results need syncing, not inputs. ~289 RNG call sites: no lockstep; host-authoritative simulation.
- `SaveFile.SaveCharacters/LoadCharacters` serialize characters (candidate for sending a character to the host).
- No networking code exists in the game.
- Character events: `OnSkillCompleted`, `OnMonsterSlain`, `OnTickUpdate(ms)`, `OnEncounterStart`, `OnCombatReset`.

## Verifying changes
1. Build the whole solution with 0 errors.
2. If you can't launch the game, state that runtime behavior is unverified.
3. If you can: launch, host (F7) and join `127.0.0.1` from a second instance or machine, check `BepInEx\LogOutput.log` for `[Error]` lines and `[host]`/`[client]` messages.
Ask the user for `LogOutput.log` after any risky change.

- Keep `src/Net/` free of Unity/BepInEx/game types so `tests/LootunCoop.NetTests` can compile it. Run those tests after any change there.
- Without the game, build the mod against a stand-in `GameDir` (BepInEx 5 `core/` from its GitHub release + `UnityEngine.Modules` 2021.3 from nuget.org
  in `Lootun_Data/Managed/`); it compiles as long as nothing references `Assembly-CSharp` types yet.

## Status / next steps
- Done: transport and phases 1-3 from `docs/MULTIPLAYER.md` (guest characters, co-op mission start, client mirror, reward/XP
  split, character lock). Two real playtests (host + friend over VPN) ran; mod loads cleanly (no `MethodAccessException`).
- Playtest fixes on `main`: level-ups now resend the guest character; revived characters no longer stuck in the death pose.
- Snapshot interpolation on `main`, playtested 2026-09-29 ("much smoother" for the joiner): protocol v4, 20 Hz timestamped
  snapshots, client playback delayed by `InterpolationDelayMs` and interpolated; timing in `src/Net/SnapshotPlayback.cs`, unit tested.
- On a branch, waiting for a playtest: `feature/live-guest-build-swap` (guest build changes applied every tick instead of at
  the next stage).
- Open from playtests: host's skill-cast visuals aren't shown to joiners (snapshots carry no cast events); guests can't
  meaningfully act on their character mid-fight. `origin/claude/compassionate-darwin-lem96t` has an unmerged console test client.
- Next: phase 4 polish (see `docs/MULTIPLAYER.md`). `game-decompile/` (local, ILSpy export) is excluded from the build.
- Both developers work on everything (no fixed split).

## Style
- Match existing code style (tabs, braces on new lines in framework files). No comments explaining the obvious.
- Small commits; commit messages: what + why. Small confirmed fixes may go straight to `main`; new or untested-in-game behavior goes
  on `feature/<name>` and is merged after a playtest. Everyone in a session must run the same build, so bump `Protocol.Version`
  on any wire change (old builds then get a clear "protocol mismatch" instead of misbehaving).
