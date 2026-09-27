using System;
using HarmonyLib;
using LootClicker.Entities.Characters;
using LootClicker.Entities.Skills;
using LootClicker.Services;

namespace LootunCoop.Game
{
	/// <summary>
	/// While a player's character is in a co-op mission (their <see cref="CoopMirror.Own"/>), it is busy: the game already keeps it
	/// out of other missions. On top of that, its skills, passives, ascendancy and loadout are locked so the build the host
	/// simulates can't drift mid-mission. Gear can still be changed; those updates reach the host automatically.
	/// </summary>
	internal static class CharacterLock
	{
		static float lastMessage = -10f;

		public static bool IsLocked(Character c) =>
			c != null && !CoopRewards.Applying && CoopMirror.IsRunning && c == CoopMirror.Own && !c.Loading;

		/// <returns>true to let the original run.</returns>
		internal static bool Allow(Character c)
		{
			if (!IsLocked(c))
				return true;
			if (UnityEngine.Time.unscaledTime - lastMessage > 1f)
			{
				lastMessage = UnityEngine.Time.unscaledTime;
				GameData.GameController.DialogBox.ShowSimpleMenu(
					c.Name + " is in a co-op mission: skills, passives and loadouts are locked until it ends. Gear can still be changed.",
					Locale.GetText("ok"));
			}
			return false;
		}

		internal static Character OwnerOf(Passive p)
		{
			switch (p)
			{
				case CharacterPassive cp when cp.Character != null:
					return cp.Character;
				case AscendancyGenericPassive gp:
					return gp.Ascendancy?.CurrentCharacter;
				case AscendancyPassive ap:
					return ap.Ascendancy?.CurrentCharacter;
				case MasteryPassive mp:
					return mp.MasterySkill?.Character;
				case FactionCooldownPassive fp:
					return fp.FactionCooldown?.Character;
				default:
					return null;
			}
		}
	}

	[HarmonyPatch]
	static class LockSkills
	{
		static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
		{
			foreach (var t in new[] { typeof(DefaultAttack), typeof(OffensiveCooldown), typeof(DefensiveCooldown), typeof(FactionCooldown) })
				yield return AccessTools.Method(typeof(Character), nameof(Character.LoadSkill), new[] { t });
			// UpdateLoadout(int) calls this one
			yield return AccessTools.Method(typeof(Character), nameof(Character.UpdateLoadout), new[] { typeof(CharacterLoadout) });
			yield return AccessTools.Method(typeof(Character), nameof(Character.ResetPassives));
			yield return AccessTools.Method(typeof(Character), nameof(Character.UpdateAutoTargetMode));
		}

		static bool Prefix(Character __instance) => CharacterLock.Allow(__instance);
	}

	[HarmonyPatch]
	static class LockPassiveAllocation
	{
		internal static readonly Type[] PassiveTypes =
		{
			typeof(CharacterPassive), typeof(AscendancyGenericPassive), typeof(AscendancyPassive), typeof(MasteryPassive), typeof(FactionCooldownPassive),
		};

		static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
		{
			foreach (var t in PassiveTypes)
				yield return AccessTools.DeclaredMethod(t, nameof(Passive.AllocatePassive));
		}

		static bool Prefix(Passive __instance, ref bool __result)
		{
			if (CharacterLock.Allow(CharacterLock.OwnerOf(__instance)))
				return true;
			__result = false;
			return false;
		}
	}

	[HarmonyPatch]
	static class LockPassiveReset
	{
		static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
		{
			// only types that override it (GetDeclaredMethods doesn't log a warning for the others)
			foreach (var t in LockPassiveAllocation.PassiveTypes)
			{
				foreach (var m in AccessTools.GetDeclaredMethods(t))
				{
					if (m.Name == nameof(Passive.ResetPassive))
						yield return m;
				}
			}
		}

		static bool Prefix(Passive __instance) => CharacterLock.Allow(CharacterLock.OwnerOf(__instance));
	}

	[HarmonyPatch(typeof(AscendancySelectionController), nameof(AscendancySelectionController.SelectAscendancy))]
	static class LockAscendancy
	{
		static bool Prefix() => CharacterLock.Allow(GameData.CurrentCharacter);
	}
}
