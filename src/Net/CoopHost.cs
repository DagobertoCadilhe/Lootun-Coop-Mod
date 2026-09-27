using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace LootunCoop.Net
{
	public sealed class HostOptions
	{
		public int Port = Protocol.DefaultPort;
		/// <summary>Total players including the host.</summary>
		public int MaxPlayers = 3;
		public string HostName = "Host";
		public string ModVersion = "";
		/// <summary>"guid@version" of every loaded plugin; clients must match exactly.</summary>
		public List<string> Mods = new List<string>();
		public int HandshakeTimeoutMs = 5000;
		public int PingIntervalMs = 2000;
		public int TimeoutMs = 10000;
	}

	/// <summary>
	/// Host side of a session. Socket I/O runs on background threads; everything observable (events, <see cref="Players"/>)
	/// changes only inside <see cref="Poll"/>, which must be called from one thread (Unity's main thread in game).
	/// </summary>
	public sealed class CoopHost : IDisposable
	{
		readonly HostOptions options;
		readonly ConcurrentQueue<NetEvent> inbox = new ConcurrentQueue<NetEvent>();
		readonly ConcurrentQueue<Connection> accepted = new ConcurrentQueue<Connection>();
		readonly List<Connection> pending = new List<Connection>();
		readonly Dictionary<int, Connection> byPlayer = new Dictionary<int, Connection>();
		readonly List<PlayerInfo> players = new List<PlayerInfo>();
		TcpListener listener;
		int nextPlayerId = 1;
		long lastPingMs;

		public event Action<PlayerInfo> PlayerJoined;
		public event Action<PlayerInfo, string> PlayerLeft;
		/// <summary>Game-level message (type >= <see cref="Protocol.FirstGameMessage"/>) from a joined player.</summary>
		public event Action<int, MessageType, byte[]> MessageReceived;
		public event Action<string> Log;

		public IReadOnlyList<PlayerInfo> Players => players;
		public bool IsRunning => listener != null;
		public int Port { get; private set; }

		public CoopHost(HostOptions options)
		{
			this.options = options;
			if (options.MaxPlayers < 1 || options.MaxPlayers > Protocol.MaxPlayersLimit)
				throw new ArgumentOutOfRangeException(nameof(options.MaxPlayers), "1.." + Protocol.MaxPlayersLimit);
			players.Add(new PlayerInfo(Protocol.HostPlayerId, CleanName(options.HostName, Protocol.HostPlayerId)) { RttMs = 0 });
		}

		/// <summary>Starts listening on all interfaces. Throws <see cref="SocketException"/> if the port is taken.</summary>
		public void Start()
		{
			if (listener != null)
				return;
			var l = new TcpListener(IPAddress.Any, options.Port);
			l.Start();
			listener = l;
			Port = ((IPEndPoint)l.LocalEndpoint).Port;
			new Thread(() => AcceptLoop(l)) { IsBackground = true, Name = "LootunCoop accept" }.Start();
			Log?.Invoke("hosting on port " + Port + " (max " + options.MaxPlayers + " players)");
		}

		void AcceptLoop(TcpListener l)
		{
			while (true)
			{
				TcpClient client;
				try
				{
					client = l.AcceptTcpClient();
				}
				catch (Exception)
				{
					return; // listener stopped
				}
				try
				{
					var c = new Connection(client, inbox);
					accepted.Enqueue(c);
					c.Start();
				}
				catch (Exception)
				{
					client.Close();
				}
			}
		}

		public void Poll()
		{
			while (accepted.TryDequeue(out var c))
			{
				if (listener == null)
				{
					c.Close("host stopped");
					continue;
				}
				pending.Add(c);
				Log?.Invoke("connection from " + c.RemoteAddress);
			}

			while (inbox.TryDequeue(out var e))
			{
				if (e.Closed)
					OnClosed(e.Connection, e.Reason);
				else
					OnMessage(e.Connection, e.Type, e.Payload);
			}

			long now = Clock.NowMs;
			foreach (var c in pending.ToArray())
				if (now - c.CreatedMs > options.HandshakeTimeoutMs)
					Reject(c, "handshake timed out");
			foreach (var kv in byPlayer.ToArray())
				if (now - kv.Value.LastReceivedMs > options.TimeoutMs)
					kv.Value.Close("timed out");
			if (now - lastPingMs >= options.PingIntervalMs)
			{
				lastPingMs = now;
				foreach (var c in byPlayer.Values)
					c.Send(MessageType.Ping, Payload.Long(now));
			}
		}

		void OnMessage(Connection c, MessageType type, byte[] payload)
		{
			if (c.IsClosed)
				return;
			try
			{
				if (!(c.Tag is int id))
				{
					if (type == MessageType.Hello)
						HandleHello(c, HelloMessage.Parse(payload));
					else
						Reject(c, "expected Hello, got " + type);
					return;
				}
				switch (type)
				{
					case MessageType.Ping:
						c.Send(MessageType.Pong, payload);
						break;
					case MessageType.Pong:
						var p = players.FirstOrDefault(x => x.Id == id);
						if (p != null)
							p.RttMs = (int)(Clock.NowMs - Payload.ReadLong(payload));
						break;
					case MessageType.Goodbye:
						c.Close("left: " + Payload.ReadString(payload));
						break;
					default:
						if ((byte)type >= Protocol.FirstGameMessage)
							MessageReceived?.Invoke(id, type, payload);
						break;
				}
			}
			catch (Exception ex) when (!(ex is OutOfMemoryException))
			{
				c.Close("bad message " + type + ": " + ex.Message);
			}
		}

		void HandleHello(Connection c, HelloMessage hello)
		{
			if (hello.ProtocolVersion != Protocol.Version)
			{
				Reject(c, "protocol mismatch (host " + Protocol.Version + ", you " + hello.ProtocolVersion + "); use the same LootunCoop build");
				return;
			}
			string modDiff = DiffMods(options.Mods, hello.Mods);
			if (modDiff != null)
			{
				Reject(c, "mod list mismatch: " + modDiff);
				return;
			}
			if (players.Count >= options.MaxPlayers)
			{
				Reject(c, "session is full (" + options.MaxPlayers + " players)");
				return;
			}

			int id = nextPlayerId++;
			var info = new PlayerInfo(id, CleanName(hello.PlayerName, id));
			pending.Remove(c);
			c.Tag = id;
			byPlayer[id] = c;
			players.Add(info);

			var welcome = new WelcomeMessage { PlayerId = id };
			welcome.Players.AddRange(players);
			c.Send(MessageType.Welcome, welcome.ToPayload());
			var joined = Payload.Build(info.Write);
			foreach (var kv in byPlayer)
				if (kv.Key != id)
					kv.Value.Send(MessageType.PlayerJoined, joined);

			Log?.Invoke(info + " joined from " + c.RemoteAddress);
			PlayerJoined?.Invoke(info);
		}

		void Reject(Connection c, string reason)
		{
			pending.Remove(c);
			Log?.Invoke("rejected " + c.RemoteAddress + ": " + reason);
			c.Send(MessageType.Reject, Payload.String(reason));
			c.CloseAfterFlush();
		}

		void OnClosed(Connection c, string reason)
		{
			pending.Remove(c);
			if (!(c.Tag is int id) || !byPlayer.Remove(id))
				return;
			var info = players.First(x => x.Id == id);
			players.Remove(info);
			var left = PlayerLeftMessage.ToPayload(id, reason);
			foreach (var other in byPlayer.Values)
				other.Send(MessageType.PlayerLeft, left);
			Log?.Invoke(info + " left: " + reason);
			PlayerLeft?.Invoke(info, reason);
		}

		public void Send(int playerId, MessageType type, byte[] payload)
		{
			if (byPlayer.TryGetValue(playerId, out var c))
				c.Send(type, payload);
		}

		public void Broadcast(MessageType type, byte[] payload, int exceptPlayerId = -1)
		{
			foreach (var kv in byPlayer)
				if (kv.Key != exceptPlayerId)
					kv.Value.Send(type, payload);
		}

		public void Kick(int playerId, string reason)
		{
			if (!byPlayer.TryGetValue(playerId, out var c))
				return;
			c.Send(MessageType.Goodbye, Payload.String("kicked: " + reason));
			c.CloseAfterFlush();
		}

		/// <summary>Tells everyone the session ended and stops listening. Players are removed by the next <see cref="Poll"/>.</summary>
		public void Stop(string reason = "host closed the session")
		{
			if (listener == null)
				return;
			listener.Stop();
			listener = null;
			foreach (var c in byPlayer.Values)
			{
				c.Send(MessageType.Goodbye, Payload.String(reason));
				c.CloseAfterFlush();
			}
			foreach (var c in pending)
				c.Close("host stopped");
			pending.Clear();
			Log?.Invoke("stopped hosting: " + reason);
		}

		public void Dispose() => Stop();

		internal static string CleanName(string name, int id)
		{
			name = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
			if (name.Length > Protocol.MaxNameLength)
				name = name.Substring(0, Protocol.MaxNameLength);
			return name.Length == 0 ? "Player" + id : name;
		}

		/// <summary>Null if identical, otherwise a human-readable difference from the client's point of view.</summary>
		internal static string DiffMods(IEnumerable<string> host, IEnumerable<string> client)
		{
			var h = new HashSet<string>(host, StringComparer.OrdinalIgnoreCase);
			var c = new HashSet<string>(client, StringComparer.OrdinalIgnoreCase);
			var missing = h.Except(c).OrderBy(x => x).ToList();
			var extra = c.Except(h).OrderBy(x => x).ToList();
			if (missing.Count == 0 && extra.Count == 0)
				return null;
			var parts = new List<string>();
			if (missing.Count > 0)
				parts.Add("you are missing " + string.Join(", ", missing));
			if (extra.Count > 0)
				parts.Add("host does not have " + string.Join(", ", extra));
			return string.Join("; ", parts);
		}
	}
}
