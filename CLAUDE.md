# CLAUDE.md

Guidance for Claude Code (and other AI assistants) working in this repo. Humans: see `README.md`.

## What this is
**LootunCoop** — co-op multiplayer (up to 3 players, direct IP over VPN, normal missions first, raids later) as a BepInEx 5 + HarmonyLib
mod for **Lootun** (Unity 2021.3, Mono, game code in `Assembly-CSharp`, namespaces `LootClicker.*`). Currently a **skeleton**: it only logs on load.
Design, phases and risks live in `docs/MULTIPLAYER.md` — read it first and keep it updated.
Answer the user in the language they use (Brazilian Portuguese or English). Be concise.
A sibling repo, **RoguePoison**, adds a custom class. Both mods may run together; keep this one independent of it (no shared IDs, no reference to it).

## Build
- Windows / Visual Studio: open `LootunCoop.csproj`. `GameDir` comes from `GameDir.user.txt` (git-ignored) or `-p:GameDir=`.
  Default: `D:\SteamLibrary\steamapps\common\Lootun`.
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
3. If you can: launch, create a Rogue, check `BepInEx\LogOutput.log` for `[Error]` lines and `[ROGUE]` messages.
Ask the user for `LogOutput.log` after any risky change.

## Status / next steps
- Phase 1 spike (not started): host receives a serialized character over TCP and starts an encounter with it next to the host's own.
- Suggested split between developers: networking layer (TCP, framing, handshake, version/mod-list check) vs. game side (serialization, joining an `Encounter`, state mirror).

## Style
- Match existing code style (tabs, braces on new lines in framework files). No comments explaining the obvious.
- Small commits; branch `feature/<name>`; PR into `main`. Commit messages: what + why.
