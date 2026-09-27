using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

namespace LootunCoop.Net
{
	public sealed class ClientOptions
	{
		public string PlayerName = "Player";
		public string ModVersion = "";
		public List<string> Mods = new List<string>();
		public int ConnectTimeoutMs = 5000;
		public int PingIntervalMs = 2000;
		public int TimeoutMs = 10000;
	}

	public enum ClientState
	{
		Disconnected,
		Connecting,
		Handshaking,
		Connected,
	}

	/// <summary>
	/// Client side of a session. Same threading rule as <see cref="CoopHost"/>: state and events change only inside <see cref="Poll"/>.
	/// </summary>
	public sealed class CoopClient : IDisposable
	{
		readonly ClientOptions options;
		readonly ConcurrentQueue<NetEvent> inbox = new ConcurrentQueue<NetEvent>();
		readonly ConcurrentQueue<Attempt> attempts = new ConcurrentQueue<Attempt>();
		readonly List<PlayerInfo> players = new List<PlayerInfo>();
		Connection connection;
		int attemptId;
		long lastPingMs;
		string pendingReject;

		sealed class Attempt
		{
			public int Id;
			public Connection Connection;
			public string Error;
		}

		public event Action Connected;
		public event Action<string> Disconnected;
		public event Action<PlayerInfo> PlayerJoined;
		public event Action<PlayerInfo, string> PlayerLeft;
		public event Action<MessageType, byte[]> MessageReceived;
		public event Action<string> Log;

		public ClientState State { get; private set; }
		public int LocalPlayerId { get; private set; } = -1;
		public int RttMs { get; private set; } = -1;
		public IReadOnlyList<PlayerInfo> Players => players;

		public CoopClient(ClientOptions options)
		{
			this.options = options;
		}

		/// <summary>Starts connecting in the background. Result arrives as <see cref="Connected"/> or <see cref="Disconnected"/>.</summary>
		public void Connect(string host, int port)
		{
			if (State != ClientState.Disconnected)
				Disconnect();
			State = ClientState.Connecting;
			int id = ++attemptId;
			Log?.Invoke("connecting to " + host + ":" + port);
			new Thread(() => ConnectWorker(id, host, port)) { IsBackground = true, Name = "LootunCoop connect" }.Start();
		}

		void ConnectWorker(int id, string host, int port)
		{
			var tcp = new TcpClient();
			try
			{
				var task = tcp.ConnectAsync(host, port);
				if (!task.Wait(options.ConnectTimeoutMs))
					throw new TimeoutException("no answer after " + options.ConnectTimeoutMs / 1000 + " s (wrong IP, host not hosting, or firewall)");
				var c = new Connection(tcp, inbox);
				attempts.Enqueue(new Attempt { Id = id, Connection = c });
			}
			catch (Exception e)
			{
				tcp.Close();
				var inner = e is AggregateException ae && ae.InnerException != null ? ae.InnerException : e;
				attempts.Enqueue(new Attempt { Id = id, Error = inner.Message });
			}
		}

		public void Poll()
		{
			while (attempts.TryDequeue(out var a))
			{
				if (a.Id != attemptId || State != ClientState.Connecting)
				{
					a.Connection?.Close("stale attempt");
					continue;
				}
				if (a.Connection == null)
				{
					Fail("could not connect: " + a.Error);
					continue;
				}
				connection = a.Connection;
				connection.Start();
				State = ClientState.Handshaking;
				var hello = new HelloMessage
				{
					ProtocolVersion = Protocol.Version,
					ModVersion = options.ModVersion,
					PlayerName = options.PlayerName,
				};
				hello.Mods.AddRange(options.Mods);
				connection.Send(MessageType.Hello, hello.ToPayload());
			}

			while (inbox.TryDequeue(out var e))
			{
				if (e.Connection != connection)
					continue;
				if (e.Closed)
					Fail(pendingReject ?? e.Reason);
				else
					OnMessage(e.Type, e.Payload);
			}

			if (State == ClientState.Handshaking && Clock.NowMs - connection.CreatedMs > options.TimeoutMs)
				connection.Close("no handshake reply (is that a LootunCoop host?)");
			else if (State == ClientState.Connected)
			{
				long now = Clock.NowMs;
				if (now - connection.LastReceivedMs > options.TimeoutMs)
					connection.Close("host timed out");
				else if (now - lastPingMs >= options.PingIntervalMs)
				{
					lastPingMs = now;
					connection.Send(MessageType.Ping, Payload.Long(now));
				}
			}
		}

		void OnMessage(MessageType type, byte[] payload)
		{
			try
			{
				switch (type)
				{
					case MessageType.Welcome:
						var w = WelcomeMessage.Parse(payload);
						LocalPlayerId = w.PlayerId;
						players.Clear();
						players.AddRange(w.Players);
						State = ClientState.Connected;
						lastPingMs = 0;
						Log?.Invoke("joined as #" + LocalPlayerId + " (" + players.Count + " players)");
						Connected?.Invoke();
						break;
					case MessageType.Reject:
						pendingReject = "rejected by host: " + Payload.ReadString(payload);
						break;
					case MessageType.Goodbye:
						pendingReject = "host ended the session: " + Payload.ReadString(payload);
						connection.Close(pendingReject);
						break;
					case MessageType.PlayerJoined:
						var p = Payload.Parse(payload, PlayerInfo.Read);
						players.Add(p);
						PlayerJoined?.Invoke(p);
						break;
					case MessageType.PlayerLeft:
						PlayerLeftMessage.Parse(payload, out int id, out string reason);
						var left = players.Find(x => x.Id == id);
						if (left != null)
						{
							players.Remove(left);
							PlayerLeft?.Invoke(left, reason);
						}
						break;
					case MessageType.Ping:
						connection.Send(MessageType.Pong, payload);
						break;
					case MessageType.Pong:
						RttMs = (int)(Clock.NowMs - Payload.ReadLong(payload));
						break;
					default:
						if (State == ClientState.Connected && (byte)type >= Protocol.FirstGameMessage)
							MessageReceived?.Invoke(type, payload);
						break;
				}
			}
			catch (Exception ex) when (!(ex is OutOfMemoryException))
			{
				connection.Close("bad message " + type + " from host: " + ex.Message);
			}
		}

		void Fail(string reason)
		{
			bool wasActive = State != ClientState.Disconnected;
			Reset();
			if (!wasActive)
				return;
			Log?.Invoke("disconnected: " + reason);
			Disconnected?.Invoke(reason);
		}

		void Reset()
		{
			connection = null;
			pendingReject = null;
			players.Clear();
			LocalPlayerId = -1;
			RttMs = -1;
			State = ClientState.Disconnected;
		}

		public void Send(MessageType type, byte[] payload)
		{
			if (State == ClientState.Connected)
				connection.Send(type, payload);
		}

		/// <summary>Leaves the session politely. Raises <see cref="Disconnected"/> immediately.</summary>
		public void Disconnect(string reason = "left the session")
		{
			if (State == ClientState.Disconnected)
				return;
			attemptId++;
			if (connection != null)
			{
				connection.Send(MessageType.Goodbye, Payload.String(reason));
				connection.CloseAfterFlush();
			}
			Fail(reason);
		}

		public void Dispose() => Disconnect();
	}
}
