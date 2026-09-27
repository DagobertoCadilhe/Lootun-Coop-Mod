using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;
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
			h.PlayerJoined += p => AddChat("* " + p.Name + " joined");
			h.PlayerLeft += (p, r) => AddChat("* " + p.Name + " left (" + r + ")");
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
				host.Stop();
				host = null;
				AddChat("* stopped hosting");
			}
			if (client != null)
			{
				var c = client;
				client = null;
				c.Disconnect();
			}
		}

		public void Poll()
		{
			host?.Poll();
			client?.Poll();
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

		void OnHostMessage(int playerId, MessageType type, byte[] payload)
		{
			if (type != MessageType.Chat)
				return;
			ReadChat(payload, out _, out string text);
			text = text.Trim();
			if (text.Length == 0)
				return;
			var sender = host.Players.FirstOrDefault(p => p.Id == playerId);
			AddChat((sender?.Name ?? "#" + playerId) + ": " + text);
			host.Broadcast(MessageType.Chat, ChatPayload(playerId, text));
		}

		void OnClientMessage(MessageType type, byte[] payload)
		{
			if (type != MessageType.Chat)
				return;
			ReadChat(payload, out int senderId, out string text);
			var sender = client.Players.FirstOrDefault(p => p.Id == senderId);
			AddChat((sender?.Name ?? "#" + senderId) + ": " + text);
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
