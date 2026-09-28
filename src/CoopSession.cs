using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;
using LootClicker.Entities.Characters;
using LootunCoop.Game;
using LootunCoop.Net;

namespace LootunCoop
{
	/// <summary>Game-facing wrapper around <see cref="CoopHost"/>/<see cref="CoopClient"/>. Main thread only.</summary>
	internal sealed class CoopSession
	{
		public const int MaxChatLines = 8;

		CoopHost host;
		CoopClient client;

		public readonly List<string> Chat = new List<string>();
		public string LastError;
		/// <summary>Client: description of the character last sent to the host, or null.</summary>
		public string SentCharacter;
		/// <summary>Client: why the selected character is not being sent, or null.</summary>
		public string SendProblem;

		public bool IsHost => host != null;
		public bool IsClient => client != null;
		public bool IsActive => IsHost || IsClient;
		public CoopClient Client => client;
		public CoopHost Host => host;

		public IReadOnlyList<PlayerInfo> Players =>
			host != null ? host.Players : client != null ? client.Players : (IReadOnlyList<PlayerInfo>)Array.Empty<PlayerInfo>();

		public int LocalPlayerId => host != null ? Protocol.HostPlayerId : client?.LocalPlayerId ?? -1;

		public void StartHost(string name, int port, int maxPlayers)
		{
			Leave();
			LastError = null;
			var h = new CoopHost(new HostOptions
			{
				Port = port,
				MaxPlayers = maxPlayers,
				HostName = name,
				ModVersion = Plugin.PluginVersion,
				Mods = LoadedMods(),
			});
			h.Log += m => Plugin.Log.LogInfo("[host] " + m);
			h.PlayerJoined += p =>
			{
				AddChat("* " + p.Name + " joined");
				CoopMission.NotifyChanged();
				CoopMission.MakeRoomForPlayers();
				CoopHostSync.OnPlayerJoined(h, p.Id);
			};
			h.PlayerLeft += (p, r) =>
			{
				AddChat("* " + p.Name + " left (" + r + ")");
				CoopMission.RemoveGuest(p.Id);
				CoopMission.NotifyChanged();
			};
			h.MessageReceived += OnHostMessage;
			try
			{
				h.Start();
			}
			catch (Exception e)
			{
				LastError = "could not host on port " + port + ": " + e.Message;
				Plugin.Log.LogError(LastError);
				return;
			}
			host = h;
			AddChat("* hosting on port " + h.Port);
		}

		public void Join(string name, string address, int port)
		{
			Leave();
			LastError = null;
			SentCharacter = null;
			lastSent = null;
			lastFingerprint = null;
			sentCharacter = null;
			lastSentLevel = -1;
			var c = new CoopClient(new ClientOptions
			{
				PlayerName = name,
				ModVersion = Plugin.PluginVersion,
				Mods = LoadedMods(),
			});
			c.Log += m => Plugin.Log.LogInfo("[client] " + m);
			c.Connected += () => AddChat("* joined as #" + c.LocalPlayerId);
			c.Disconnected += r =>
			{
				AddChat("* disconnected: " + r);
				CoopMirror.End();
				if (client != c)
					return; // we called Leave()
				client = null;
				LastError = r;
			};
			c.PlayerJoined += p => AddChat("* " + p.Name + " joined");
			c.PlayerLeft += (p, r) => AddChat("* " + p.Name + " left (" + r + ")");
			c.MessageReceived += OnClientMessage;
			client = c;
			c.Connect(address, port);
		}

		public void Leave()
		{
			if (host != null)
			{
				CoopMission.Reset();
				CoopHostSync.Reset();
				host.Stop();
				host = null;
				AddChat("* stopped hosting");
			}
			if (client != null)
			{
				var c = client;
				client = null;
				c.Disconnect();
				CoopMirror.End();
			}
		}

		public void Poll()
		{
			host?.Poll();
			client?.Poll();
			if (host != null)
				CoopHostSync.Tick(host);
			AutoSendCharacter();
		}

		const float CharacterCheckSeconds = 2f;
		float nextCharacterCheck;
		byte[] lastSent;

		byte[] lastFingerprint;
		Character sentCharacter;
		/// <summary>Level last sent, so a level-up always resends even though the fingerprint ignores XP.</summary>
		int lastSentLevel = -1;

		/// <summary>Client: the character this player plays in co-op (the one in the mission, else the one last sent).</summary>
		public Character CoopCharacter => CoopMirror.Own ?? sentCharacter;

		/// <summary>
		/// Client: every few seconds, checks the co-op character and sends it if its build changed (other character, gear, gems,
		/// passives, skills, loadout, level...). XP alone doesn't count. The host swaps it in at the next stage.
		/// While a mission runs, the character in it is the one tracked, whatever is selected.
		/// </summary>
		void AutoSendCharacter()
		{
			if (client == null || client.State != ClientState.Connected || UnityEngine.Time.unscaledTime < nextCharacterCheck)
				return;
			nextCharacterCheck = UnityEngine.Time.unscaledTime + CharacterCheckSeconds;
			if ((CoopMirror.Own ?? GameData.CurrentCharacter) != null)
				SendCharacter(force: false);
		}

		public void SendChat(string text)
		{
			text = (text ?? "").Trim();
			if (text.Length == 0)
				return;
			if (host != null)
			{
				AddChat(host.Players[0].Name + ": " + text);
				host.Broadcast(MessageType.Chat, ChatPayload(Protocol.HostPlayerId, text));
			}
			else if (client != null && client.State == ClientState.Connected)
				client.Send(MessageType.Chat, ChatPayload(client.LocalPlayerId, text));
		}

		/// <summary>Client: sends the currently selected character to the host (unless unchanged and not forced).</summary>
		public void SendCharacter(bool force = true)
		{
			if (client == null || client.State != ClientState.Connected)
				return;
			var c = CoopMirror.Own ?? GameData.CurrentCharacter;
			if (c == null)
			{
				AddChat("* no character selected");
				return;
			}
			if (c != CoopMirror.Own && c.ActiveEncounter != null)
			{
				// playing it here too would earn twice; wait until it's free
				SendProblem = c.Name + " is in one of your missions. End that mission or select another character.";
				return;
			}
			SendProblem = null;
			byte[] data, fingerprint;
			try
			{
				fingerprint = CharacterCodec.Fingerprint(c);
				if (!force && c == sentCharacter && c.Level == lastSentLevel
					&& lastFingerprint != null && lastFingerprint.SequenceEqual(fingerprint))
					return;
				data = CharacterCodec.Serialize(c);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("[coop] could not serialize " + c.Name + ": " + e);
				AddChat("* could not send " + c.Name + ": " + e.Message);
				nextCharacterCheck = UnityEngine.Time.unscaledTime + 30f;
				return;
			}
			bool update = SentCharacter != null;
			client.Send(MessageType.CharacterData, data);
			lastSent = data;
			lastFingerprint = fingerprint;
			sentCharacter = c;
			lastSentLevel = c.Level;
			SentCharacter = CharacterCodec.Describe(c);
			AddChat("* " + (update ? "updated " : "sent ") + SentCharacter);
		}

		/// <summary>Host: adds a copy of the selected character as a fake guest, to test without a second player.</summary>
		public void AddTestClone()
		{
			var c = GameData.CurrentCharacter;
			if (c == null || !CoopMission.CanAddClone)
				return;
			byte[] data;
			try
			{
				data = CharacterCodec.Serialize(c);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("[coop] could not serialize " + c.Name + ": " + e);
				AddChat("* could not clone " + c.Name + ": " + e.Message);
				return;
			}
			if (TryLoadGuest(-1 - CoopMission.Guests.Keys.Count(k => k < 0), "Test", data))
				Plugin.Log.LogInfo("[coop] clone source was " + CharacterCodec.Describe(c));
		}

		/// <summary>Host: re-copies the selected character into every test clone, like a guest sending an update.</summary>
		public void RefreshTestClones()
		{
			var c = GameData.CurrentCharacter;
			if (c == null)
				return;
			foreach (int id in CoopMission.Guests.Keys.Where(k => k < 0).ToList())
				TryLoadGuest(id, "Test", CharacterCodec.Serialize(c));
		}

		void OnHostMessage(int playerId, MessageType type, byte[] payload)
		{
			var sender = host.Players.FirstOrDefault(p => p.Id == playerId);
			string name = sender?.Name ?? "#" + playerId;
			switch (type)
			{
				case MessageType.Chat:
					ReadChat(payload, out _, out string text);
					AddChat(name + ": " + text);
					host.Broadcast(MessageType.Chat, ChatPayload(playerId, text));
					break;
				case MessageType.CharacterData:
					TryLoadGuest(playerId, name, payload);
					break;
			}
		}

		bool TryLoadGuest(int playerId, string owner, byte[] data)
		{
			Character c;
			try
			{
				c = CharacterCodec.Deserialize(data);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("[coop] bad character from " + owner + " (" + data.Length + " bytes): " + e);
				AddChat("* could not load " + owner + "'s character: " + e.Message);
				return false;
			}
			CoopMission.SetGuest(playerId, owner, c, data);
			Plugin.Log.LogInfo("[coop] guest #" + playerId + " " + owner + ": " + CharacterCodec.Describe(c) + ", " + data.Length + " bytes");
			AddChat("* " + owner + " brings " + CharacterCodec.Describe(c));
			return true;
		}

		void OnClientMessage(MessageType type, byte[] payload)
		{
			try
			{
				switch (type)
				{
					case MessageType.Chat:
						ReadChat(payload, out int senderId, out string text);
						var sender = client.Players.FirstOrDefault(p => p.Id == senderId);
						AddChat((sender?.Name ?? "#" + senderId) + ": " + text);
						break;
					case MessageType.CoopStart:
						CoopMirror.Start(CoopStartMessage.Read(payload), client.LocalPlayerId, sentCharacter);
						AddChat(CoopMirror.IsRunning ? "* co-op mission started" : "* co-op mission started, can't show it: " + CoopMirror.Problem);
						break;
					case MessageType.CoopSnapshot:
						CoopMirror.Apply(CoopSnapshotMessage.Read(payload));
						break;
					case MessageType.CoopEnd:
						CoopMirror.End();
						AddChat("* co-op mission ended");
						break;
					case MessageType.CoopReward:
						var reward = CoopRewardMessage.Read(payload);
						CoopRewards.Apply(reward, CoopCharacter);
						if (reward.Kind == RewardKind.Currency || reward.Kind == RewardKind.Item)
							Plugin.Log.LogInfo("[coop] received " + reward.Kind + (reward.Kind == RewardKind.Item ? " " + reward.Item.ItemId : " " + reward.CurrencyAmount.ToString("0")));
						break;
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogError("[coop] failed to handle " + type + ": " + e);
			}
		}

		static byte[] ChatPayload(int senderId, string text) => Payload.Build(w =>
		{
			w.Write(senderId);
			w.Write(text.Length > 200 ? text.Substring(0, 200) : text);
		});

		static void ReadChat(byte[] payload, out int senderId, out string text)
		{
			using (var r = new BinaryReader(new MemoryStream(payload, false)))
			{
				senderId = r.ReadInt32();
				text = r.ReadString();
			}
		}

		void AddChat(string line)
		{
			Chat.Add(line);
			if (Chat.Count > MaxChatLines)
				Chat.RemoveAt(0);
			Plugin.Log.LogInfo("[chat] " + line);
		}

		/// <summary>"guid@version" for every loaded BepInEx plugin. Both sides must match exactly.</summary>
		static List<string> LoadedMods() =>
			Chainloader.PluginInfos.Values.Select(i => i.Metadata.GUID + "@" + i.Metadata.Version).OrderBy(x => x).ToList();
	}
}
