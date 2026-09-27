using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LootClicker.Entities;
using LootClicker.Entities.Characters;
using LootClicker.Items;
using LootunCoop.Net;

namespace LootunCoop.Game
{
	/// <summary>
	/// Phase 3. On the host, rewards from the co-op encounter are split:
	/// - XP the game gives a guest character is forwarded to its owner (and not applied to the host's copy);
	/// - items are dealt round-robin to the players (host included); a guest's item is rolled on the guest's side;
	/// - gold and rubies are divided evenly.
	/// On the client, <see cref="Apply"/> puts the reward on the player's own character and inventory.
	/// </summary>
	internal static class CoopRewards
	{
		/// <summary>True while the host processes a slain monster of the co-op encounter.</summary>
		internal static bool InSlainScope;
		/// <summary>True while a client applies a received reward (bypasses the co-op character lock).</summary>
		internal static bool Applying;
		static int nextRecipient;

		static bool HostCoop(Encounter encounter) =>
			Plugin.Session.IsHost && CoopMission.IsRunning && (InSlainScope || encounter != null && encounter == CoopMission.Encounter);

		/// <summary>Players sharing the loot: host (0) and every guest in the mission (test clones included).</summary>
		static List<int> Recipients() => new[] { Protocol.HostPlayerId }.Concat(CoopMission.PlayersInMission).ToList();

		internal static int NextRecipient()
		{
			var r = Recipients();
			return r[nextRecipient++ % r.Count];
		}

		internal static void Send(int playerId, CoopRewardMessage reward, string what)
		{
			if (playerId > 0)
				Plugin.Session.Host?.Send(playerId, MessageType.CoopReward, reward.Write());
			else
				Plugin.Log.LogInfo("[coop] (test clone share, dropped) " + what);
		}

		public static void Apply(CoopRewardMessage m, Character own)
		{
			Applying = true;
			try
			{
				switch (m.Kind)
				{
					case RewardKind.Experience:
						own?.AddExperience(m.Amount);
						break;
					case RewardKind.SkillExperience:
						own?.AddSkillExperience(m.Amount);
						break;
					case RewardKind.Currency:
						if (GameData.GameController.Currencies.TryGetValue((Currency)m.Currency, out var cc))
							cc.UpdateValue(m.CurrencyAmount);
						break;
					case RewardKind.Item:
						var a = m.Item;
						Equipment.GenerateItemDrop((Equipment.EquipmentID)a.ItemId, (Rarity)a.Rarity, a.ItemLevel, null, a.OverrideFilter,
							a.NemesisChance, (EnchantID)a.EnchantId, a.ParagonChance, a.ParagonUpgradeChance, a.MaxParagonLevel, a.ScrapOverride,
							skipDuplication: true);
						break;
				}
			}
			finally
			{
				Applying = false;
			}
		}

		[HarmonyPatch(typeof(Encounter), nameof(Encounter.ProcessSlainMonster))]
		static class SlainScope
		{
			static void Prefix(Encounter __instance, out bool __state)
			{
				__state = InSlainScope;
				if (Plugin.Session.IsHost && __instance == CoopMission.Encounter)
					InSlainScope = true;
			}

			static void Finalizer(bool __state) => InSlainScope = __state;
		}

		/// <summary>XP for a guest goes to its owner. Skipping it here also avoids host side effects (Soul Orbs, max level, toasts).</summary>
		[HarmonyPatch(typeof(Character), nameof(Character.AddExperience))]
		static class ForwardExperience
		{
			static bool Prefix(Character __instance, long expToAdd, bool fromSave)
			{
				if (fromSave || !Plugin.Session.IsHost || !CoopMission.IsGuest(__instance))
					return true;
				Send(CoopMission.PlayerIdOf(__instance), new CoopRewardMessage { Kind = RewardKind.Experience, Amount = expToAdd },
					expToAdd + " XP for " + __instance.Name);
				return false;
			}
		}

		[HarmonyPatch(typeof(Character), nameof(Character.AddSkillExperience))]
		static class ForwardSkillExperience
		{
			static bool Prefix(Character __instance, long expToAdd)
			{
				if (!Plugin.Session.IsHost || !CoopMission.IsGuest(__instance))
					return true;
				if (CoopMission.PlayerIdOf(__instance) > 0)
					Send(CoopMission.PlayerIdOf(__instance), new CoopRewardMessage { Kind = RewardKind.SkillExperience, Amount = expToAdd }, null);
				return false;
			}
		}

		/// <summary>Items are dealt round-robin; the host rolls duplication so the receiver gets duplicates as separate drops.</summary>
		[HarmonyPatch(typeof(Equipment), nameof(Equipment.GenerateItemDrop))]
		static class SplitItems
		{
			static bool Prefix(Equipment.EquipmentID itemID, Rarity rarity, int itemLevel, Encounter encounter, bool overrideFilter,
				int nemesisChance, EnchantID enchantID, int paragonChance, int paragonUpgrageChance, byte maxParagonLevel, bool scrapOverride,
				bool skipDuplication, ref bool __result)
			{
				if (!HostCoop(encounter))
					return true;
				int to = NextRecipient();
				if (to == Protocol.HostPlayerId)
					return true;
				var args = new ItemDropArgs
				{
					ItemId = (int)itemID,
					Rarity = (int)rarity,
					ItemLevel = itemLevel,
					OverrideFilter = overrideFilter,
					NemesisChance = nemesisChance,
					EnchantId = (int)enchantID,
					ParagonChance = paragonChance,
					ParagonUpgradeChance = paragonUpgrageChance,
					MaxParagonLevel = maxParagonLevel,
					ScrapOverride = scrapOverride,
				};
				int copies = 1;
				if (!skipDuplication && encounter != null && UnityEngine.Random.Range(1, 1001) <= encounter.GetDuplicationChance())
					copies = 2;
				for (int i = 0; i < copies; i++)
					Send(to, new CoopRewardMessage { Kind = RewardKind.Item, Item = args }, "item " + itemID + " (" + rarity + ", lvl " + itemLevel + ")");
				__result = true;
				return false;
			}
		}

		/// <summary>Gold and rubies from co-op kills are divided evenly between the players.</summary>
		[HarmonyPatch(typeof(CurrencyController), nameof(CurrencyController.UpdateValue))]
		static class SplitCurrency
		{
			static void Prefix(CurrencyController __instance, ref double valueDiff)
			{
				if (!InSlainScope || valueDiff <= 0 || !Plugin.Session.IsHost
					|| __instance.Currency != Currency.GoldCoin && __instance.Currency != Currency.Ruby)
					return;
				var others = CoopMission.PlayersInMission.ToList();
				if (others.Count == 0)
					return;
				double share = valueDiff / (others.Count + 1);
				foreach (int id in others)
					Send(id, new CoopRewardMessage { Kind = RewardKind.Currency, Currency = (int)__instance.Currency, CurrencyAmount = share },
						share.ToString("0") + " " + __instance.Currency);
				valueDiff = share;
			}
		}
	}
}
