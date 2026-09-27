using HarmonyLib;
using LootClicker.Entities.Characters;
using UnityEngine.EventSystems;

namespace LootunCoop.Game
{
	/// <summary>
	/// Guest characters belong to other players: the host must not select, equip, respec or change settings on them.
	/// Selecting is the gateway to the equipment menu, so blocking <c>GameData.CurrentCharacter</c> covers most paths.
	/// </summary>
	[HarmonyPatch(typeof(GameData), nameof(GameData.CurrentCharacter), MethodType.Setter)]
	static class GuestCannotBeSelected
	{
		static bool Prefix(Character value) => !CoopMission.IsGuest(value);
	}

	/// <summary>No context menu (passives, equipment, tactics) and no click-to-select on a guest's combat panel.</summary>
	[HarmonyPatch(typeof(CharacterPanelController), nameof(CharacterPanelController.OnPointerClick))]
	static class GuestPanelClick
	{
		static bool Prefix(CharacterPanelController __instance) => !CoopMission.IsGuest(__instance.Character);
	}

	[HarmonyPatch(typeof(RaidCharacterPanelController), nameof(RaidCharacterPanelController.OnPointerClick))]
	static class GuestRaidPanelClick
	{
		static bool Prefix(RaidCharacterPanelController __instance) => !CoopMission.IsGuest(__instance.Character);
	}

	/// <summary>The auto-cast toggles on a guest's panel snap back instead of changing the guest's settings.</summary>
	[HarmonyPatch(typeof(CharacterPanelController))]
	static class GuestAutoCastToggles
	{
		[HarmonyPrefix, HarmonyPatch(nameof(CharacterPanelController.OffensiveCooldownToggle_OnValueChanged))]
		static bool Offensive(CharacterPanelController __instance)
		{
			if (!CoopMission.IsGuest(__instance.Character))
				return true;
			__instance.OffensiveCooldownToggle.SetIsOnWithoutNotify(__instance.Character.AutoCastOffensiveSkill);
			return false;
		}

		[HarmonyPrefix, HarmonyPatch(nameof(CharacterPanelController.DefensiveCooldownToggle_OnValueChanged))]
		static bool Defensive(CharacterPanelController __instance)
		{
			if (!CoopMission.IsGuest(__instance.Character))
				return true;
			__instance.DefensiveCooldownToggle.SetIsOnWithoutNotify(__instance.Character.AutoCastDefensiveSkill);
			return false;
		}

		[HarmonyPrefix, HarmonyPatch(nameof(CharacterPanelController.FactionCooldownToggle_OnValueChanged))]
		static bool Faction(CharacterPanelController __instance)
		{
			if (!CoopMission.IsGuest(__instance.Character))
				return true;
			__instance.FactionCooldownToggle.SetIsOnWithoutNotify(__instance.Character.AutoCastFactionSkill);
			return false;
		}
	}
}
