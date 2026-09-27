using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LootClicker.Entities.Characters;
using TMPro;
using UnityEngine;

namespace LootunCoop.Game
{
	/// <summary>
	/// While hosting, Prepare Mission's last character slots belong to the other players: a guest's character shows there live
	/// (locked), and a player who hasn't picked one yet gets an empty slot reading "Waiting for Bot...". The host's selected
	/// character goes in the first slot; the host can still change or empty their own slots.
	/// </summary>
	internal static class PrepareMissionSlots
	{
		/// <summary>Slot indexes reserved for other players; the host can't pick characters into them.</summary>
		internal static readonly HashSet<int> Reserved = new HashSet<int>();
		static readonly Dictionary<TMP_Text, string> originalTexts = new Dictionary<TMP_Text, string>();

		public static void Fill(PrepareMissionController window)
		{
			if (window == null || window.CharacterPreviews == null)
				return;
			var previews = window.CharacterPreviews;
			bool hosting = Plugin.Session.IsHost;
			var guests = hosting ? CoopMission.Guests.Values.Select(g => g.Character).ToList() : new List<Character>();
			var waiting = CoopMission.Waiting();

			for (int i = 0; i < previews.Count; i++)
			{
				var c = previews[i]?.GetCharacter();
				if (c != null && CoopMission.IsGuest(c) && !guests.Contains(c))
					previews[i].UnloadCharacter();
			}
			Reserved.Clear();
			RestoreSlotTexts();

			int slots = System.Math.Min(previews.Count, window.GetCharacterCount());
			int first = System.Math.Max(0, slots - guests.Count - waiting.Count);
			int slot = first;
			foreach (var g in guests)
			{
				if (slot >= slots)
					break;
				Reserved.Add(slot);
				var p = previews[slot++];
				if (p != null && p.GetCharacter() != g)
					p.LoadCharacter(g);
			}
			foreach (var name in waiting)
			{
				if (slot >= slots)
					break;
				Reserved.Add(slot);
				previews[slot]?.UnloadCharacter();
				SetSlotText(window, slot++, "<color=#8cf>Waiting for\n" + name + "...</color>");
			}
			if (Reserved.Count == 0)
				return;

			var own = GameData.CurrentCharacter;
			if (first > 0 && own != null && own.ActiveEncounter == null && !CoopMission.IsGuest(own)
				&& previews.Take(first).All(p => p == null || p.GetCharacter() == null) && !window.IsCharacterInUse(own))
				previews[0]?.LoadCharacter(own);
		}

		/// <summary>Text of the empty slot ("Select Character"), outside the character preview.</summary>
		static void SetSlotText(PrepareMissionController window, int index, string text)
		{
			if (window.CharacterSlots == null || index >= window.CharacterSlots.Count || window.CharacterSlots[index] == null)
				return;
			var preview = window.CharacterPreviews[index]?.PreviewPanel;
			var label = window.CharacterSlots[index].GetComponentsInChildren<TMP_Text>(true)
				.FirstOrDefault(t => preview == null || !t.transform.IsChildOf(preview.transform));
			if (label == null)
				return;
			if (!originalTexts.ContainsKey(label))
				originalTexts[label] = label.text;
			label.text = text;
		}

		static void RestoreSlotTexts()
		{
			foreach (var kv in originalTexts)
			{
				if (kv.Key != null)
					kv.Key.text = kv.Value;
			}
			originalTexts.Clear();
		}
	}

	/// <summary>Guest slots in Prepare Mission can't be clicked (no swap, no empty).</summary>
	[HarmonyPatch(typeof(CharacterPreviewController), nameof(CharacterPreviewController.OnPointerClick))]
	static class GuestPreviewLocked
	{
		static bool Prefix(CharacterPreviewController __instance) =>
			__instance.Type != CharacterPreviewType.MissionSelector || !CoopMission.IsGuest(__instance.GetCharacter());
	}

	/// <summary>Slots reserved for players who are still choosing can't be picked into by the host.</summary>
	[HarmonyPatch(typeof(PrepareMissionController), nameof(PrepareMissionController.BeginCharacterSelection))]
	static class ReservedSlotLocked
	{
		static bool Prefix(int id) => !Plugin.Session.IsHost || !PrepareMissionSlots.Reserved.Contains(id);
	}
}
