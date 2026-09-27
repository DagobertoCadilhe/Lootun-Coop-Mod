using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LootClicker.Entities;
using LootClicker.Entities.Characters;
using LootClicker.Maps;
using LootClicker.Services;

namespace LootunCoop.Game
{
	/// <summary>
	/// Host side of the phase 1 spike: guest characters received over the network and the co-op encounter that runs them.
	/// The mission is started from the game's own Prepare Mission window ("Begin Co-op"), which appends the guests to the party.
	/// It uses a normal free mission slot for now and is never saved.
	/// </summary>
	internal static class CoopMission
	{
		internal sealed class Guest
		{
			public Character Character;
			public string Owner;
		}

		/// <summary>Player id -> that player's character. Negative ids are local test clones.</summary>
		public static readonly SortedDictionary<int, Guest> Guests = new SortedDictionary<int, Guest>();

		/// <summary>Raised when the guest list or the running state changes, so UI can refresh.</summary>
		public static event Action Changed;

		public static void NotifyChanged() => Changed?.Invoke();

		/// <summary>Player id -> the guest character object currently inside the running co-op encounter.</summary>
		static readonly Dictionary<int, Character> InMission = new Dictionary<int, Character>();

		public static Encounter Encounter { get; private set; }

		/// <summary>Guests to append to the next <see cref="LootClicker.Entities.Encounter"/> built by the game. Consumed once.</summary>
		internal static List<Character> PendingGuests;

		public static bool IsRunning => Encounter != null && GameData.Encounters != null && GameData.Encounters.Contains(Encounter);

		public static bool IsGuest(Character c) => c != null && (Guests.Values.Any(g => g.Character == c)
			|| IsRunning && Encounter.Characters.Contains(c) && !GameData.Characters.Contains(c));

		/// <summary>Name of the player who owns this guest character, or null.</summary>
		public static string OwnerOf(Character c)
		{
			if (c == null)
				return null;
			foreach (var kv in Guests)
			{
				if (kv.Value.Character == c || InMission.TryGetValue(kv.Key, out var m) && m == c)
					return kv.Value.Owner;
			}
			return null;
		}

		/// <summary>Host: joined players who have not sent a character yet.</summary>
		public static List<string> Waiting() => Plugin.Session.IsHost
			? Plugin.Session.Players.Where(p => p.Id != Net.Protocol.HostPlayerId && !Guests.ContainsKey(p.Id)).Select(p => p.Name).ToList()
			: new List<string>();

		/// <summary>Slots taken by guests plus slots reserved for players still choosing.</summary>
		public static int Reserved => Guests.Count + Waiting().Count;

		/// <summary>Max own characters the host can add next to the current guests.</summary>
		public static int OwnSlotsLeft(PrepareMissionController window) =>
			Math.Max(0, Math.Min(3, window != null && window.Map != null ? window.GetCharacterCount() : 3) - Reserved);

		/// <summary>Replaces the guest's character. A running mission keeps the old one until it is restarted.</summary>
		public static void SetGuest(int playerId, string owner, Character character)
		{
			Guests[playerId] = new Guest { Character = character, Owner = owner };
			Changed?.Invoke();
		}

		public static void RemoveGuest(int playerId)
		{
			if (!Guests.TryGetValue(playerId, out var g))
				return;
			Guests.Remove(playerId);
			if (IsRunning && InMission.ContainsKey(playerId))
			{
				Plugin.Log.LogInfo("[coop] guest left, ending co-op mission");
				End();
			}
			Changed?.Invoke();
		}

		public static void Reset()
		{
			End();
			Guests.Clear();
			Changed?.Invoke();
		}

		/// <summary>Runs the window's own Begin Mission with the guests added to the selected characters.</summary>
		public static void BeginFromPrepare(PrepareMissionController window)
		{
			string error = CheckBegin(window);
			if (error != null)
			{
				GameData.GameController.DialogBox.ShowSimpleMenu(error, Locale.GetText("ok"));
				return;
			}
			var guests = Guests.Values.Select(g => g.Character).ToList();
			InMission.Clear();
			foreach (var kv in Guests)
				InMission[kv.Key] = kv.Value.Character;
			PendingGuests = guests;
			try
			{
				window.ButtonBeginMission_Click();
			}
			finally
			{
				PendingGuests = null;
			}
			Encounter = GameData.Encounters.FirstOrDefault(e => e.Characters != null && e.Characters.Contains(guests[0]));
			if (Encounter != null)
				Plugin.Log.LogInfo("[coop] started on " + Encounter.Map.Name + " with " + string.Join(", ", Encounter.Characters.Select(CharacterCodec.Describe)));
			Changed?.Invoke();
		}

		static string CheckBegin(PrepareMissionController window)
		{
			if (IsRunning)
				return "A co-op mission is already running. End it first (F7).";
			if (Guests.Count == 0)
				return "No guest characters yet. A player's character arrives automatically once they select one in their game.";
			var map = window.Map;
			if (!IsSupported(map))
				return "Co-op only works on normal missions for now (no raids, bounties, endless, dummies or faction missions).";
			int left = OwnSlotsLeft(window);
			int own = window.GetCharacters().Count(c => !IsGuest(c));
			if (own == 0)
				return null; // let the game say a character is required
			if (own > left)
				return "Co-op: " + Guests.Count + " guest(s) join you, so pick at most " + left + " of your own characters.";
			return null;
		}

		public static void End()
		{
			if (IsRunning)
			{
				Encounter.EndEncounter();
				Plugin.Log.LogInfo("[coop] mission ended");
			}
			Encounter = null;
			Changed?.Invoke();
		}

		/// <summary>
		/// Puts guests' updated characters (new gear, passives, skills...) into the running co-op encounter. Called between stages,
		/// never mid-fight. The replaced copy's combat progress (XP) is dropped until results flow back to owners (phase 3).
		/// </summary>
		internal static void SwapUpdatedGuests(Encounter encounter)
		{
			if (encounter == null || encounter != Encounter || InMission.Count == 0)
				return;
			bool swapped = false;
			foreach (var id in InMission.Keys.ToList())
			{
				if (!Guests.TryGetValue(id, out var g) || g.Character == InMission[id])
					continue;
				var old = InMission[id];
				int index = encounter.Characters.IndexOf(old);
				if (index < 0)
					continue;
				// can run inside the tick's action loop: the old copy must not act after leaving
				encounter.RemoveEntityFromActionList(old);
				old.ActiveEncounter = null;
				old.CombatReset();
				encounter.Characters[index] = g.Character;
				g.Character.ActiveEncounter = encounter;
				g.Character.CombatReset();
				g.Character.TriggerOnEncounterStart(encounter);
				InMission[id] = g.Character;
				swapped = true;
				Plugin.Log.LogInfo("[coop] " + g.Owner + "'s update applied: " + CharacterCodec.Describe(g.Character));
			}
			if (!swapped)
				return;
			if (GameData.CurrentEncounter == encounter)
				GameData.GameController.Menu.CombatMenu.LoadEncounter(encounter);
			encounter.EncounterPreview?.MissionPreview?.UpdateCharacterStatus(encounter);
		}

		static bool IsSupported(MissionBase map) =>
			map is Map && !(map is EndlessMap || map is Raid || map is TargetDummyMap || map is FactionDungeon || map is FactionFrenzy);
	}

	[HarmonyPatch(typeof(Encounter), MethodType.Constructor, new[] { typeof(List<Character>), typeof(MissionBase), typeof(bool), typeof(int) })]
	static class AppendGuestsToEncounter
	{
		/// <summary>Begin Co-op: own characters + all guests, once each. Any other start (normal Begin): guests are removed.</summary>
		static void Prefix(ref List<Character> characters)
		{
			if (characters == null)
				return;
			var guests = CoopMission.PendingGuests;
			CoopMission.PendingGuests = null;
			if (guests != null)
				characters = characters.Where(c => !guests.Contains(c) && !CoopMission.IsGuest(c)).Concat(guests).ToList();
			else if (characters.Any(CoopMission.IsGuest))
				characters = characters.Where(c => !CoopMission.IsGuest(c)).ToList();
		}
	}

	[HarmonyPatch(typeof(Encounter), nameof(Encounter.GetNextStage))]
	static class SwapGuestsBetweenStages
	{
		static void Prefix(Encounter __instance) => CoopMission.SwapUpdatedGuests(__instance);
	}

	/// <summary>Keeps the co-op encounter (and so the guests) out of the host's save file.</summary>
	[HarmonyPatch(typeof(SaveFile), nameof(SaveFile.SaveEncounters))]
	static class HideCoopMissionFromSave
	{
		static void Prefix(out int __state)
		{
			__state = -1;
			var e = CoopMission.Encounter;
			if (e == null || GameData.Encounters == null)
				return;
			__state = GameData.Encounters.IndexOf(e);
			if (__state >= 0)
				GameData.Encounters.RemoveAt(__state);
		}

		static void Finalizer(int __state)
		{
			if (__state >= 0)
				GameData.Encounters.Insert(Math.Min(__state, GameData.Encounters.Count), CoopMission.Encounter);
		}
	}
}
