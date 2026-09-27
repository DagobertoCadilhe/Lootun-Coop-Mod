using System.Collections.Generic;
using System.IO;

namespace LootunCoop.Net
{
	/// <summary>One character in the co-op party and who owns it (0 = host, &gt;0 = client id, &lt;0 = host test clone).</summary>
	public sealed class PartyMember
	{
		public int OwnerId;
		public string Owner;
		/// <summary>SaveFile.SaveCharacter bytes.</summary>
		public byte[] Character;
	}

	/// <summary><see cref="MessageType.CoopStart"/>.</summary>
	public sealed class CoopStartMessage
	{
		public int MapId;
		public int MonsterLevel;
		public bool LevelScaling;
		/// <summary>In the host encounter's character order; snapshots refer to characters by this index.</summary>
		public List<PartyMember> Party = new List<PartyMember>();

		public byte[] Write() => Payload.Build(w =>
		{
			w.Write(MapId);
			w.Write(MonsterLevel);
			w.Write(LevelScaling);
			w.Write(Party.Count);
			foreach (var p in Party)
			{
				w.Write(p.OwnerId);
				w.Write(p.Owner ?? "");
				w.Write(p.Character.Length);
				w.Write(p.Character);
			}
		});

		public static CoopStartMessage Read(byte[] payload) => Payload.Parse(payload, r =>
		{
			var m = new CoopStartMessage { MapId = r.ReadInt32(), MonsterLevel = r.ReadInt32(), LevelScaling = r.ReadBoolean() };
			int n = r.ReadInt32();
			for (int i = 0; i < n; i++)
				m.Party.Add(new PartyMember { OwnerId = r.ReadInt32(), Owner = r.ReadString(), Character = r.ReadBytes(r.ReadInt32()) });
			return m;
		});
	}

	public class EntityState
	{
		public float Health, MaxHealth, Barrier, MaxBarrier;
		public int AttackTime, CurrentAttackTime;

		internal void WriteState(BinaryWriter w)
		{
			w.Write(Health);
			w.Write(MaxHealth);
			w.Write(Barrier);
			w.Write(MaxBarrier);
			w.Write(AttackTime);
			w.Write(CurrentAttackTime);
		}

		internal void ReadState(BinaryReader r)
		{
			Health = r.ReadSingle();
			MaxHealth = r.ReadSingle();
			Barrier = r.ReadSingle();
			MaxBarrier = r.ReadSingle();
			AttackTime = r.ReadInt32();
			CurrentAttackTime = r.ReadInt32();
		}
	}

	public sealed class MonsterState : EntityState
	{
		/// <summary>Host-assigned id, stable for the monster's lifetime.</summary>
		public int NetId;
		public int MonsterId;
		public int Level;
		public byte Rarity;
	}

	/// <summary><see cref="MessageType.CoopSnapshot"/>.</summary>
	public sealed class CoopSnapshotMessage
	{
		public int Stage;
		public int BossStage;
		public bool IsBossStage;
		public List<EntityState> Characters = new List<EntityState>();
		public List<MonsterState> Monsters = new List<MonsterState>();

		public byte[] Write() => Payload.Build(w =>
		{
			w.Write(Stage);
			w.Write(BossStage);
			w.Write(IsBossStage);
			w.Write((byte)Characters.Count);
			foreach (var c in Characters)
				c.WriteState(w);
			w.Write((byte)Monsters.Count);
			foreach (var m in Monsters)
			{
				w.Write(m.NetId);
				w.Write(m.MonsterId);
				w.Write(m.Level);
				w.Write(m.Rarity);
				m.WriteState(w);
			}
		});

		public static CoopSnapshotMessage Read(byte[] payload) => Payload.Parse(payload, r =>
		{
			var s = new CoopSnapshotMessage { Stage = r.ReadInt32(), BossStage = r.ReadInt32(), IsBossStage = r.ReadBoolean() };
			int nc = r.ReadByte();
			for (int i = 0; i < nc; i++)
			{
				var c = new EntityState();
				c.ReadState(r);
				s.Characters.Add(c);
			}
			int nm = r.ReadByte();
			for (int i = 0; i < nm; i++)
			{
				var m = new MonsterState { NetId = r.ReadInt32(), MonsterId = r.ReadInt32(), Level = r.ReadInt32(), Rarity = r.ReadByte() };
				m.ReadState(r);
				s.Monsters.Add(m);
			}
			return s;
		});
	}

	public enum RewardKind : byte
	{
		Experience = 1,
		SkillExperience = 2,
		Currency = 3,
		Item = 4,
	}

	/// <summary>
	/// Arguments for the game's <c>Equipment.GenerateItemDrop</c>: the receiving player rolls the item locally, so their own
	/// loot filter, auto-scrap and inventory rules apply.
	/// </summary>
	public sealed class ItemDropArgs
	{
		public int ItemId;
		public int Rarity;
		public int ItemLevel;
		public bool OverrideFilter;
		public int NemesisChance;
		public int EnchantId;
		public int ParagonChance;
		public int ParagonUpgradeChance;
		public byte MaxParagonLevel;
		public bool ScrapOverride;
	}

	/// <summary><see cref="MessageType.CoopReward"/>: one reward for the receiving player's co-op character or inventory.</summary>
	public sealed class CoopRewardMessage
	{
		public RewardKind Kind;
		/// <summary>Experience / SkillExperience.</summary>
		public long Amount;
		/// <summary>Currency.</summary>
		public int Currency;
		public double CurrencyAmount;
		/// <summary>Item.</summary>
		public ItemDropArgs Item;

		public byte[] Write() => Payload.Build(w =>
		{
			w.Write((byte)Kind);
			switch (Kind)
			{
				case RewardKind.Experience:
				case RewardKind.SkillExperience:
					w.Write(Amount);
					break;
				case RewardKind.Currency:
					w.Write(Currency);
					w.Write(CurrencyAmount);
					break;
				case RewardKind.Item:
					w.Write(Item.ItemId);
					w.Write(Item.Rarity);
					w.Write(Item.ItemLevel);
					w.Write(Item.OverrideFilter);
					w.Write(Item.NemesisChance);
					w.Write(Item.EnchantId);
					w.Write(Item.ParagonChance);
					w.Write(Item.ParagonUpgradeChance);
					w.Write(Item.MaxParagonLevel);
					w.Write(Item.ScrapOverride);
					break;
			}
		});

		public static CoopRewardMessage Read(byte[] payload) => Payload.Parse(payload, r =>
		{
			var m = new CoopRewardMessage { Kind = (RewardKind)r.ReadByte() };
			switch (m.Kind)
			{
				case RewardKind.Experience:
				case RewardKind.SkillExperience:
					m.Amount = r.ReadInt64();
					break;
				case RewardKind.Currency:
					m.Currency = r.ReadInt32();
					m.CurrencyAmount = r.ReadDouble();
					break;
				case RewardKind.Item:
					m.Item = new ItemDropArgs
					{
						ItemId = r.ReadInt32(),
						Rarity = r.ReadInt32(),
						ItemLevel = r.ReadInt32(),
						OverrideFilter = r.ReadBoolean(),
						NemesisChance = r.ReadInt32(),
						EnchantId = r.ReadInt32(),
						ParagonChance = r.ReadInt32(),
						ParagonUpgradeChance = r.ReadInt32(),
						MaxParagonLevel = r.ReadByte(),
						ScrapOverride = r.ReadBoolean(),
					};
					break;
				default:
					throw new InvalidDataException("unknown reward kind " + m.Kind);
			}
			return m;
		});
	}
}
