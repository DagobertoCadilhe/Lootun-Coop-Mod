# Lootun co-op — design notes

Goal: 3 players in normal missions (raids later), direct IP over a VPN (Radmin etc.).

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

## Phases
1. **Spike (log-only):** host receives a character over TCP and starts an encounter with it alongside the host's own.
2. **State mirror:** clients see the fight.
3. **Progression:** drops, XP, result returned to each owner.
4. **Polish:** disconnects, version/mod checks, separate save flag so co-op runs can't corrupt normal saves.

## Open questions / risks
- Can the combat UI render a shadow encounter cleanly without it ticking? (hardest part)
- Mission flow, offline progress and save integrity in a shared session.
- Which `Character` state must be sent vs rebuilt on the host?
