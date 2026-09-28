using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LootClicker.Entities;
using LootClicker.Entities.Characters;
using LootClicker.Entities.Monsters;
using LootClicker.Maps;
using LootunCoop.Net;

namespace LootunCoop.Game
{
	/// <summary>
	/// Client side of a co-op mission: a real <see cref="Encounter"/> in a mission slot, so the game's own combat screen shows
	/// it, but it never ticks. Monsters and every health/barrier/action bar come from the host's snapshots.
	/// The player's own character takes part for real (so it is busy and can't be put in another mission); the other players'
	/// characters are read-only copies.
	/// </summary>
	internal static class CoopMirror
	{
		public static Encounter Encounter { get; private set; }

		/// <summary>This player's real character inside the mirror, or null.</summary>
		public static Character Own { get; private set; }

		/// <summary>Why the mirror could not be shown, for the panel.</summary>
		public static string Problem;

		static readonly Dictionary<Character, string> owners = new Dictionary<Character, string>();
		static readonly Dictionary<int, Monster> monsters = new Dictionary<int, Monster>();

		public static bool IsRunning => Encounter != null && GameData.Encounters != null && GameData.Encounters.Contains(Encounter);

		/// <summary>Another player's character shown in the mirror.</summary>
		public static bool IsForeign(Character c) => c != null && Encounter != null && owners.ContainsKey(c) && c != Own;

		public static string OwnerOf(Character c) => c != null && owners.TryGetValue(c, out var o) ? o : null;

		public static void Start(CoopStartMessage msg, int localPlayerId, Character own)
		{
			End();
			Problem = null;
			if (!Atlas.MapDictionary.TryGetValue((MapID)msg.MapId, out var map) || map == null)
			{
				Problem = "unknown map " + msg.MapId + " (different game version?)";
				Plugin.Log.LogError("[mirror] " + Problem);
				return;
			}
			if (!GameData.GameController.Menu.CombatMenu.EncounterPreviews.Any(p => p.SlotUnlocked && p.Encounter == null))
			{
				Problem = "free one mission slot to watch the co-op mission";
				Plugin.Log.LogWarning("[mirror] " + Problem);
				return;
			}

			var characters = new List<Character>();
			foreach (var member in msg.Party)
			{
				Character c = null;
				if (member.OwnerId == localPlayerId && own != null && own.ActiveEncounter == null && GameData.Characters.Contains(own))
				{
					c = own;
					Own = own;
				}
				else
				{
					try
					{
						c = CharacterCodec.Deserialize(member.Character);
					}
					catch (FormatException e)
					{
						Plugin.Log.LogError("[mirror] could not load " + member.Owner + "'s character: " + e.Message);
					}
				}
				if (c == null)
					continue;
				owners[c] = member.Owner;
				characters.Add(c);
			}
			if (characters.Count != msg.Party.Count)
			{
				Problem = "could not load every party character";
				owners.Clear();
				Own = null;
				return;
			}

			var encounter = new Encounter(characters, map, msg.LevelScaling, msg.MonsterLevel);
			encounter.SetupFlasks(null, false, false);
			encounter.LoadIdols(new Dictionary<IdolSlotType, IdolID>());
			Encounter = encounter;
			GameData.AddNewEncounter(encounter);
			if (!GameData.Encounters.Contains(encounter))
			{
				Problem = "the game did not accept the co-op mission";
				Clear();
				return;
			}
			Plugin.Log.LogInfo("[mirror] watching co-op on " + map.Name + " with " + string.Join(", ", characters.Select(CharacterCodec.Describe)));
		}

		public static void Apply(CoopSnapshotMessage s)
		{
			var e = Encounter;
			if (e == null || !IsRunning)
				return;
			bool stageChanged = e.CurrentStage != s.Stage || e.IsBossStage != s.IsBossStage;
			e.CurrentStage = s.Stage;
			e.IsBossStage = s.IsBossStage;

			for (int i = 0; i < s.Characters.Count && i < e.Characters.Count; i++)
				ApplyCharacterState(e.Characters[i], s.Characters[i]);

			bool monstersChanged = s.Monsters.Count != e.Monsters.Count
				|| s.Monsters.Where((m, i) => !monsters.TryGetValue(m.NetId, out var mon) || e.Monsters[i] != mon).Any();
			if (monstersChanged)
				RebuildMonsters(e, s.Monsters);
			for (int i = 0; i < s.Monsters.Count && i < e.Monsters.Count; i++)
				ApplyState(e.Monsters[i], s.Monsters[i]);

			var menu = GameData.GameController.Menu.CombatMenu;
			if (monstersChanged)
				menu.UpdateMonsterPanel(e);
			if (stageChanged)
				menu.UpdateBossProgressBar(e);
			e.EncounterPreview?.MissionPreview?.UpdateCharacterStatus(e);
		}

		static void RebuildMonsters(Encounter e, List<MonsterState> states)
		{
			var list = new List<Monster>();
			var alive = new HashSet<int>();
			foreach (var st in states)
			{
				alive.Add(st.NetId);
				if (!monsters.TryGetValue(st.NetId, out var m))
				{
					m = MonsterStore.GetNewMonster((MonsterID)st.MonsterId, st.Level);
					if (m == null)
						continue;
					m.Rarity = (MonsterRarity)st.Rarity;
					m.ActiveEncounter = e;
					if (m.CurrentAbility == null)
						m.GetNextAbility();
					monsters[st.NetId] = m;
				}
				list.Add(m);
			}
			foreach (var id in monsters.Keys.Where(id => !alive.Contains(id)).ToList())
				monsters.Remove(id);
			e.Monsters = list;
		}

		static void ApplyState(Entity entity, EntityState st)
		{
			if (entity == null)
				return;
			entity.MaxHealth = st.MaxHealth;
			entity.MaxBarrier = st.MaxBarrier;
			entity.CurrentHealth = st.Health;
			entity.CurrentBarrier = st.Barrier;
			entity.AttackTime = st.AttackTime;
			entity.CurrentAttackTime = st.CurrentAttackTime;
		}

		/// <summary>
		/// Same as <see cref="ApplyState"/>, plus: the mirror never ticks, so nothing ever plays the game's own "back to life"
		/// transition on these read-only copies (own or foreign) after a party wipe and auto-restart on the host. Without this,
		/// a revived character's health bar updates but its panel can be left showing the death pose until the player switches
		/// tabs and the whole panel rebuilds from scratch. Call the game's own reset here instead, same as a guest swap does.
		/// </summary>
		static void ApplyCharacterState(Character c, EntityState st)
		{
			if (c == null)
				return;
			bool reviving = c.CurrentHealth <= 0 && st.Health > 0;
			ApplyState(c, st);
			if (reviving)
				c.CombatReset();
		}

		public static void End()
		{
			if (IsRunning)
			{
				Encounter.EndEncounter();
				Plugin.Log.LogInfo("[mirror] co-op mission ended");
			}
			Clear();
		}

		static void Clear()
		{
			Encounter = null;
			Own = null;
			owners.Clear();
			monsters.Clear();
		}
	}

	/// <summary>The mirror never simulates: no ticks and no locally spawned stages.</summary>
	[HarmonyPatch(typeof(Encounter), nameof(Encounter.DoEncounterTick))]
	static class MirrorDoesNotTick
	{
		static bool Prefix(Encounter __instance) => __instance != CoopMirror.Encounter;
	}

	[HarmonyPatch(typeof(Encounter), nameof(Encounter.GetNextStage))]
	static class MirrorDoesNotSpawn
	{
		static bool Prefix(Encounter __instance) => __instance != CoopMirror.Encounter;
	}
}
