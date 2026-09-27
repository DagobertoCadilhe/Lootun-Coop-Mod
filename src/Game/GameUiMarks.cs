using HarmonyLib;
using LootClicker.Entities;

namespace LootunCoop.Game
{
	/// <summary>Combat panel name of a guest shows whose character it is, e.g. "Lvl 12 TEST (Bot's)".</summary>
	[HarmonyPatch(typeof(CharacterPanelController), nameof(CharacterPanelController.UpdateCharacterName))]
	static class GuestNameTag
	{
		static void Postfix(CharacterPanelController __instance)
		{
			var c = __instance.Character;
			if (c == null || __instance.NameText == null || !CoopMission.IsGuest(c))
				return;
			string owner = CoopMission.OwnerOf(c) ?? "guest";
			__instance.NameText.text += " <color=#8cf>(" + owner + "'s)</color>";
		}
	}

	/// <summary>The mission slot running the co-op encounter is labelled CO-OP.</summary>
	[HarmonyPatch(typeof(MissionPreviewController), nameof(MissionPreviewController.Setup), typeof(Encounter))]
	static class CoopSlotTag
	{
		static void Postfix(MissionPreviewController __instance, Encounter encounter)
		{
			if (encounter?.Characters == null || __instance.MissionNameText == null)
				return;
			foreach (var c in encounter.Characters)
			{
				if (CoopMission.IsGuest(c))
				{
					__instance.MissionNameText.text = "<color=#8cf>CO-OP</color> " + encounter.Map.Name;
					return;
				}
			}
		}
	}
}
