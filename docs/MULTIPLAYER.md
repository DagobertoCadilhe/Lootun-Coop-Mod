# Lootun co-op — design notes

Goal: 3 players in normal missions (raids later), direct IP over a VPN (Radmin etc.).

## Decisions
- **One character per player.** Each player brings exactly one character into the co-op party.
- **Co-op is its own mission slot.** Online play runs in a dedicated co-op mission slot; the host's other characters keep running
  their normal single-player missions untouched.
- **Player cap:** 3 by default (normal mission = 1-3 characters). The net layer supports up to 4 (`Protocol.MaxPlayersLimit`),
  but 4 characters make the game treat the encounter as a raid (`GetRaidStatus()`), so 4 players is tied to raid support.
  How a 4-player normal mission would work (patch `GetRaidStatus`, or only allow raids with 4) is still open.

## What the game already gives us (from the decompile)
- `Encounter(List<Character> characters, MissionBase map, bool levelScaling, int monsterLevel)` takes a list of characters.
  1-3 characters = normal mission; `Encounter.GetRaidStatus()` returns true for 4+.
- `Map.GetMonsterCount(Characters.Count)` and `GetBonusStageMonsters(..., Characters.Count, ...)` already scale with party size.
- Combat is auto-cast (skills/cooldowns fire on their own), so we don't need to sync player inputs, only builds and results.
- Characters are serialized by `SaveFile` (`SaveCharacters`/`LoadCharacters`) — candidate for sending a character to the host.
- No networking code exists. There are ~289 RNG call sites, so lockstep/deterministic simulation is not an option.

### Mission slots (`GameData`, `CombatMenuController`, `EncounterPreviewController`)
- There is no slot manager class. Running missions are `GameData.Encounters` (`List<Encounter>`), the selected one is
  `GameData.CurrentEncounter`.
- The 5 slots are UI: `CombatMenuController` setup instantiates `EncounterPreviewPrefab` 5 times (hard-coded `for m < 5`) under
  `EncounterSelectPanel` into `CombatMenu.EncounterPreviews`. Each `EncounterPreviewController` has `SlotID`, `SlotUnlocked`,
  `Encounter`. Unlocked count = `Upgrades.EncounterSlotsPurchased`; `Upgrades.EncounterSlotCost` has 6 entries (last unused).
- `GameData.AddNewEncounter(encounter)` puts it in the first unlocked empty preview, adds it to `Encounters`, selects it and
  calls `StartEncounter()`. `CombatMenu.UpdateEncounterPreviews()` re-binds `Encounters[i]` to preview `i` by index.
- Starting a mission: `PrepareMissionController` (~line 1170) builds `new Encounter(characters, Map, levelScaling, monsterLevel)`,
  then `SetupFlasks`, `LoadIdols`, `SetupPassiveGroup`, `RestartMode`/`RestartTime`, then `GameData.AddNewEncounter`.
- Tick: `GameController.UpdateEncounter()` calls `DoEncounterTick()` on every encounter (20 ms per tick). Ending:
  `Encounter.EndEncounter()` removes it from `GameData.Encounters` and unloads the preview.
- `Encounter` ctor sets `character.ActiveEncounter = this`; `EndEncounter` clears it.

### Characters over the wire (`SaveFile`)
- `SaveFile` is a static byte writer/reader: `WriteData` (`List<byte>`), `ReadData` (`byte[]`) + `LoadIndex`, `Write(...)`/`ReadX()`.
- `SaveCharacter(Character)` writes one full character (class, name, avatar, level/xp, skill masteries, all 3 loadouts with gear,
  gems and passives, ascendancies). `LoadCharacter()` rebuilds it and does not touch `GameData.Characters`
  (`LoadCharacters()` is the one that adds to the roster/UI). Plan: set `WriteData`, call `SaveCharacter`, send the bytes;
  on the host set `ReadData`/`LoadIndex = 0` and call `LoadCharacter()`. Save/restore those statics around the call.
- Caveats: `LoadCharacter` only knows the 4 vanilla classes; faction skills are only written if the *sender* has
  `Upgrades.WarCamp.FactionsUnlocked`, and `AutoSocket` depends on the host's `Upgrades.GemcuttersWorkshop`.

### Side effects a guest character would have on the host
- `Monster.GrantExperience` gives XP to every living character in the encounter; a level-up calls `GameData.UpdateMaxLevel`,
  may grant Soul Orbs (`Character` ~line 1160) and shows host toasts.
- `Monster.DropLoot`/`DropBossLoot` and `Equipment.GenerateItemDrop(s)` put items, gold and rubies in the host's global
  inventory/currencies. `Tasks.TriggerOnMonsterSlain`, `Stats.*` count for the host.
- Save: `SaveEncounters` writes each encounter's characters as `GameData.Characters.IndexOf(c)`; a guest writes `-1` and
  is dropped on load, but character/buff states are also saved. The co-op encounter must be hidden from `SaveEncounters`
  (Harmony prefix/postfix that temporarily removes it from `GameData.Encounters`).

## Architecture
- **Host-authoritative.** The host runs the `Encounter` with all party characters. Clients do not simulate combat.
- **Transport:** plain TCP, length-prefixed messages, direct IP (works over Radmin/VPN).
- **Client -> host:** serialized character (build, gear, skills) on join; changes between fights.
- **Host -> clients:** snapshots ~5 Hz (monsters, HP/barrier, buffs/DoT stacks), rendered by the game's own combat UI
  from a "shadow" encounter that does not tick locally.
- **Rewards:** host generates drops/XP; each owner receives their own items/progress.
- Everyone needs identical mod lists and versions (handshake check).

## Wire protocol (implemented, `src/Net/`)
- Frame: `int32 LE length` + `byte type` + payload; length counts type + payload, max 8 MB. Strings are `BinaryWriter` UTF-8.
- Handshake: client sends `Hello` (protocol version, mod version, name, `guid@version` of every loaded BepInEx plugin).
  Host answers `Welcome` (your id + player list) or `Reject` (reason) and closes. Protocol and mod list must match exactly.
- Host is player `#0`; client ids start at 1 and are not reused within a session.
- `PlayerJoined`/`PlayerLeft` broadcast by the host. `Ping`/`Pong` every 2 s both ways (RTT); 10 s of silence = drop.
  `Goodbye` = leaving / kicked / host stopped.
- Types `>= 32` are game messages, passed to the game layer untouched (`Chat` = 32 for now). Bump `Protocol.Version` on any
  incompatible change.
- Threading: one reader + one writer thread per connection; everything reaches game code through `Poll()` on Unity's main thread.

## Phases
0. **Transport (done, tested on loopback):** host/join, handshake, version + mod-list check, heartbeats, chat, F7 test panel.
1. **Spike (log-only):** host receives a character over TCP and starts an encounter with it alongside the host's own.
   Steps: (a) `CharacterData` message: client serializes its selected idle character with `SaveCharacter`, host rebuilds it
   with `LoadCharacter` and logs name/class/level/HP/damage. (b) Start from Prepare Mission (see Start below).
   (c) Keep it out of the save. (d) Co-op slot: instantiate a 6th `EncounterPreviewPrefab` only while hosting,
   so co-op never uses a normal slot. Loot/XP side effects stay as-is until phase 3.
   Status: (a)-(c) implemented (`src/Game/`, `CharacterData` = 33, protocol v2), untested in game; (d) not started.
   Start: Prepare Mission gets a "Begin Co-op" button (host only) that runs the game's own Begin Mission with the guests
   appended via an `Encounter` ctor prefix, so flasks/idols/restart settings apply. Own + guests must fit the 3 slots.
   Guests are protected: `GameData.CurrentCharacter` can't be set to them, their combat panel ignores clicks and auto-cast toggles.
   Slots: each joined player has a reserved slot in Prepare Mission ("Waiting for X..." until their character arrives).
   Updates: the client re-serializes its selected character every 2 s and resends it if the bytes changed (gear, gems,
   passives, skills, loadout, other character). The host swaps it into the running encounter at the next stage
   (`Encounter.GetNextStage` prefix), never mid-fight; the replaced copy's combat XP is dropped until phase 3.
   Solo test: F7 host -> Testing tools -> clone / "Update clones from my selected character".
2. **State mirror (implemented, untested in game):** on `CoopStart` (map, level, party bytes + owners) the client builds a real
   `Encounter` in a free mission slot (`CoopMirror`) with its own real character plus read-only copies of the others. It never
   ticks or spawns (`DoEncounterTick`/`GetNextStage` prefixes); `CoopSnapshot` (~5 Hz: stage, per character and monster
   HP/barrier/attack timers, monsters by host net id) drives it through the entities' own setters, so the combat UI updates.
   `CoopEnd` / disconnect ends it. Hidden from saves like the host's encounter. Needs one free slot on the client.
3. **Progression (implemented, untested in game):** host-side, inside the co-op encounter (`ProcessSlainMonster` scope):
   guest `AddExperience`/`AddSkillExperience` are forwarded to the owner (`CoopReward`) and skipped on the host; each
   `Equipment.GenerateItemDrop` goes round-robin to host/guests (a guest's drop is re-rolled on their side with their filters;
   the host rolls duplication); gold and rubies are split evenly. Other drop kinds (enchant scrolls, tools, materials,
   Heart of the Abyss) still go to the host. The owner's character is locked meanwhile (`CharacterLock`): skills, passives,
   ascendancy, loadout switch, respec; gear stays editable. Auto-send ignores XP-only changes (`CharacterCodec.Fingerprint`).
4. **Polish:** disconnects, version/mod checks, separate save flag so co-op runs can't corrupt normal saves.

## Open questions / risks
- Can the combat UI render a shadow encounter cleanly without it ticking? (hardest part)
- Mission flow, offline progress and save integrity in a shared session.
- Which `Character` state must be sent vs rebuilt on the host?
