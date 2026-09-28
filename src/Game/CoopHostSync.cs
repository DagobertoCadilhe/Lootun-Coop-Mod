using System.Collections.Generic;
using System.Linq;
using LootClicker.Entities;
using LootClicker.Entities.Characters;
using LootClicker.Entities.Monsters;
using LootunCoop.Net;
using UnityEngine;

namespace LootunCoop.Game
{
	/// <summary>
	/// Host side of phase 2: tells clients when the co-op mission starts/ends and streams snapshots of it (~5 Hz) so their
	/// <see cref="CoopMirror"/> can show the fight.
	/// </summary>
	internal static class CoopHostSync
	{
		const float SnapshotSeconds = 0.1f;
		static Encounter announced;
		static float nextSnapshot;
		static int nextMonsterId = 1;
		static readonly Dictionary<Monster, int> monsterIds = new Dictionary<Monster, int>();

		public static void Tick(CoopHost host)
		{
			var running = CoopMission.IsRunning ? CoopMission.Encounter : null;
			if (running != announced)
			{
				if (announced != null)
					host.Broadcast(MessageType.CoopEnd, Payload.String("mission ended"));
				announced = running;
				monsterIds.Clear();
				if (running != null)
					host.Broadcast(MessageType.CoopStart, BuildStart(running));
			}
			if (running == null || Time.unscaledTime < nextSnapshot)
				return;
			nextSnapshot = Time.unscaledTime + SnapshotSeconds;
			host.Broadcast(MessageType.CoopSnapshot, BuildSnapshot(running).Write());
		}

		/// <summary>A player joined while the mission runs: let them watch it too.</summary>
		public static void OnPlayerJoined(CoopHost host, int playerId)
		{
			if (announced != null && CoopMission.IsRunning)
				host.Send(playerId, MessageType.CoopStart, BuildStart(announced));
		}

		public static void Reset()
		{
			announced = null;
			monsterIds.Clear();
		}

		static byte[] BuildStart(Encounter e)
		{
			var msg = new CoopStartMessage { MapId = (int)e.Map.ID, MonsterLevel = e.MonsterLevel, LevelScaling = e.LevelScaling };
			foreach (var c in e.Characters)
			{
				int owner = CoopMission.IsGuest(c) ? CoopMission.PlayerIdOf(c) : Protocol.HostPlayerId;
				CoopMission.Guests.TryGetValue(owner, out var g);
				msg.Party.Add(new PartyMember
				{
					OwnerId = owner,
					Owner = owner == Protocol.HostPlayerId ? Plugin.Session.Players[0].Name : CoopMission.OwnerOf(c) ?? "guest",
					Character = g != null && g.Character == c && g.Data != null ? g.Data : CharacterCodec.Serialize(c),
				});
			}
			return msg.Write();
		}

		static CoopSnapshotMessage BuildSnapshot(Encounter e)
		{
			var s = new CoopSnapshotMessage { Stage = e.CurrentStage, BossStage = e.BossStage, IsBossStage = e.IsBossStage };
			foreach (Character c in e.Characters)
				s.Characters.Add(State(new EntityState(), c));
			foreach (var m in e.Monsters.Where(m => m != null))
			{
				if (!monsterIds.TryGetValue(m, out int id))
					monsterIds[m] = id = nextMonsterId++;
				s.Monsters.Add((MonsterState)State(new MonsterState { NetId = id, MonsterId = (int)m.ID, Level = m.Level, Rarity = (byte)m.Rarity }, m));
			}
			if (monsterIds.Count > 64)
			{
				foreach (var dead in monsterIds.Keys.Where(m => !e.Monsters.Contains(m)).ToList())
					monsterIds.Remove(dead);
			}
			return s;
		}

		static EntityState State(EntityState st, Entity en)
		{
			if (en == null)
				return st;
			st.Health = en.CurrentHealth;
			st.MaxHealth = en.MaxHealth;
			st.Barrier = en.CurrentBarrier;
			st.MaxBarrier = en.MaxBarrier;
			st.AttackTime = en.AttackTime;
			st.CurrentAttackTime = en.CurrentAttackTime;
			return st;
		}
	}
}
