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
2. **State mirror:** clients see the fight.
3. **Progression:** drops, XP, result returned to each owner.
4. **Polish:** disconnects, version/mod checks, separate save flag so co-op runs can't corrupt normal saves.

## Open questions / risks
- Can the combat UI render a shadow encounter cleanly without it ticking? (hardest part)
- Mission flow, offline progress and save integrity in a shared session.
- Which `Character` state must be sent vs rebuilt on the host?
