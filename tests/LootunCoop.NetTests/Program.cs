using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using LootunCoop.Net;

namespace LootunCoop.NetTests
{
	internal static class Program
	{
		static readonly List<string> Mods = new List<string> { "personal.lootuncoop@0.0.1", "some.other.mod@1.2.0" };
		static readonly List<Action> pollers = new List<Action>();
		static int failures;

		static int Main()
		{
			Run("host + 2 clients handshake and share player lists", ThreePlayersJoin);
			Run("extra player is rejected when full", FullSessionRejects);
			Run("mod list mismatch is rejected with a readable reason", ModMismatchRejects);
			Run("protocol mismatch is rejected", ProtocolMismatchRejects);
			Run("game messages both ways, including a 1 MB payload", GameMessagesRoundTrip);
			Run("client leaving is announced to host and other clients", ClientLeaves);
			Run("host stopping disconnects everyone", HostStops);
			Run("kick removes the player", Kick);
			Run("silent peer is dropped by heartbeat timeout", HeartbeatTimeout);
			Run("socket that never says Hello is dropped", HandshakeTimeout);
			Run("garbage length prefix does not crash the host", GarbageFrame);
			Run("connecting to a closed port fails cleanly", ConnectRefused);
			Run("ping measures RTT", PingRtt);
			Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
			return failures == 0 ? 0 : 1;
		}

		static void Run(string name, Action test)
		{
			pollers.Clear();
			var sw = Stopwatch.StartNew();
			try
			{
				test();
				Console.WriteLine("PASS  " + name + " (" + sw.ElapsedMilliseconds + " ms)");
			}
			catch (Exception e)
			{
				failures++;
				Console.WriteLine("FAIL  " + name + ": " + e.Message);
			}
		}

		// ---- helpers ----

		static CoopHost Host(int maxPlayers = 3, List<string> mods = null, int timeoutMs = 10000, int handshakeMs = 5000)
		{
			var h = new CoopHost(new HostOptions
			{
				Port = 0,
				MaxPlayers = maxPlayers,
				HostName = "Hosty",
				ModVersion = "0.0.1",
				Mods = mods ?? Mods,
				TimeoutMs = timeoutMs,
				HandshakeTimeoutMs = handshakeMs,
				PingIntervalMs = 100,
			});
			h.Start();
			pollers.Add(h.Poll);
			return h;
		}

		static CoopClient Client(string name, List<string> mods = null)
		{
			var c = new CoopClient(new ClientOptions { PlayerName = name, ModVersion = "0.0.1", Mods = mods ?? Mods, PingIntervalMs = 100 });
			pollers.Add(c.Poll);
			return c;
		}

		static CoopClient Join(CoopHost h, string name)
		{
			var c = Client(name);
			c.Connect("127.0.0.1", h.Port);
			Until(() => c.State == ClientState.Connected, name + " connected");
			return c;
		}

		static void Until(Func<bool> cond, string what, int timeoutMs = 3000)
		{
			var sw = Stopwatch.StartNew();
			while (!cond())
			{
				if (sw.ElapsedMilliseconds > timeoutMs)
					throw new Exception("timed out waiting for: " + what);
				foreach (var p in pollers)
					p();
				Thread.Sleep(5);
			}
		}

		static void Check(bool cond, string what)
		{
			if (!cond)
				throw new Exception("check failed: " + what);
		}

		static string Names(IEnumerable<PlayerInfo> ps) => string.Join(",", ps.OrderBy(p => p.Id).Select(p => p.Name + "#" + p.Id));

		// ---- tests ----

		static void ThreePlayersJoin()
		{
			var h = Host();
			var joined = new List<string>();
			h.PlayerJoined += p => joined.Add(p.Name);
			var a = Join(h, "Alice");
			var b = Join(h, "Bob");
			Until(() => a.Players.Count == 3, "Alice sees Bob");
			Check(Names(h.Players) == "Hosty#0,Alice#1,Bob#2", "host list " + Names(h.Players));
			Check(Names(a.Players) == Names(h.Players), "alice list " + Names(a.Players));
			Check(Names(b.Players) == Names(h.Players), "bob list " + Names(b.Players));
			Check(a.LocalPlayerId == 1 && b.LocalPlayerId == 2, "local ids");
			Check(string.Join(",", joined) == "Alice,Bob", "join events");
			h.Stop();
		}

		static void FullSessionRejects()
		{
			var h = Host(maxPlayers: 3);
			Join(h, "A");
			Join(h, "B");
			var c = Client("C");
			string reason = null;
			c.Disconnected += r => reason = r;
			c.Connect("127.0.0.1", h.Port);
			Until(() => reason != null, "C rejected");
			Check(reason.Contains("full"), reason);
			Check(h.Players.Count == 3, "host still has 3");
			h.Stop();
		}

		static void ModMismatchRejects()
		{
			var h = Host();
			var c = Client("Mod", new List<string> { "personal.lootuncoop@0.0.1", "some.other.mod@1.1.0", "extra.mod@1.0" });
			string reason = null;
			c.Disconnected += r => reason = r;
			c.Connect("127.0.0.1", h.Port);
			Until(() => reason != null, "rejected");
			Check(reason.Contains("mod list mismatch"), reason);
			Check(reason.Contains("some.other.mod version differs (host 1.2.0, you 1.1.0)"), reason);
			Check(reason.Contains("host does not have extra.mod@1.0") && !reason.Contains("you are missing"), reason);
			Check(h.Players.Count == 1, "not added");
			h.Stop();
		}

		static void ProtocolMismatchRejects()
		{
			var h = Host();
			using (var raw = new TcpClient("127.0.0.1", h.Port))
			{
				var hello = Payload.Build(w =>
				{
					w.Write(Protocol.Version + 99);
					w.Write("x");
					w.Write("Old");
					w.Write(0);
				});
				RawSend(raw, MessageType.Hello, hello);
				var reply = RawReceive(raw, () => h.Poll());
				Check(reply.Item1 == MessageType.Reject, "got " + reply.Item1);
				Check(Payload.ReadString(reply.Item2).Contains("protocol mismatch"), Payload.ReadString(reply.Item2));
			}
			h.Stop();
		}

		static void GameMessagesRoundTrip()
		{
			var h = Host();
			var a = Join(h, "A");
			var b = Join(h, "B");
			var atHost = new List<string>();
			h.MessageReceived += (id, t, p) => atHost.Add(id + ":" + t + ":" + (p.Length > 100 ? p.Length.ToString() : Payload.ReadString(p)));
			var atB = new List<string>();
			b.MessageReceived += (t, p) => atB.Add(t + ":" + Payload.ReadString(p));

			a.Send(MessageType.Chat, Payload.String("hi from A"));
			var big = new byte[1024 * 1024];
			new Random(1).NextBytes(big);
			a.Send((MessageType)40, big);
			Until(() => atHost.Count == 2, "host got 2 messages");
			Check(atHost[0] == "1:Chat:hi from A", atHost[0]);
			Check(atHost[1] == "1:40:" + big.Length, atHost[1]);

			h.Broadcast(MessageType.Chat, Payload.String("from host"), exceptPlayerId: 1);
			Until(() => atB.Count == 1, "B got broadcast");
			Check(atB[0] == "Chat:from host", atB[0]);
			h.Stop();
		}

		static void ClientLeaves()
		{
			var h = Host();
			var a = Join(h, "A");
			var b = Join(h, "B");
			Until(() => a.Players.Count == 3, "A sees B");
			string hostReason = null, aSaw = null;
			h.PlayerLeft += (p, r) => hostReason = p.Name + ":" + r;
			a.PlayerLeft += (p, r) => aSaw = p.Name;
			b.Disconnect("bye");
			Check(b.State == ClientState.Disconnected, "B state");
			Until(() => hostReason != null && aSaw != null, "leave propagated");
			Check(hostReason.StartsWith("B:"), hostReason);
			Check(aSaw == "B", aSaw);
			Check(Names(a.Players) == "Hosty#0,A#1", Names(a.Players));
			h.Stop();
		}

		static void HostStops()
		{
			var h = Host();
			var a = Join(h, "A");
			string reason = null;
			a.Disconnected += r => reason = r;
			h.Stop("done for today");
			Until(() => reason != null, "A disconnected");
			Check(reason.Contains("done for today"), reason);
			Until(() => h.Players.Count == 1, "host cleaned up");
		}

		static void Kick()
		{
			var h = Host();
			var a = Join(h, "A");
			string reason = null;
			a.Disconnected += r => reason = r;
			h.Kick(1, "testing");
			Until(() => reason != null && h.Players.Count == 1, "kicked");
			Check(reason.Contains("kicked: testing"), reason);
			h.Stop();
		}

		static void HeartbeatTimeout()
		{
			var h = Host(timeoutMs: 400);
			string left = null;
			h.PlayerLeft += (p, r) => left = r;
			using (var raw = new TcpClient("127.0.0.1", h.Port))
			{
				var hello = new HelloMessage { ProtocolVersion = Protocol.Version, PlayerName = "Zombie" };
				hello.Mods.AddRange(Mods);
				RawSend(raw, MessageType.Hello, hello.ToPayload());
				Until(() => h.Players.Count == 2, "zombie joined");
				Until(() => left != null, "zombie timed out", 3000);
				Check(left == "timed out", left);
			}
			h.Stop();
		}

		static void HandshakeTimeout()
		{
			var h = Host(handshakeMs: 200);
			using (var raw = new TcpClient("127.0.0.1", h.Port))
			{
				var reply = RawReceive(raw, () => h.Poll());
				Check(reply.Item1 == MessageType.Reject, "got " + reply.Item1);
				Check(Payload.ReadString(reply.Item2).Contains("timed out"), Payload.ReadString(reply.Item2));
			}
			h.Stop();
		}

		static void GarbageFrame()
		{
			var h = Host();
			var a = Join(h, "A");
			using (var raw = new TcpClient("127.0.0.1", h.Port))
			{
				raw.GetStream().Write(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3 }, 0, 7);
				var sw = Stopwatch.StartNew();
				while (sw.ElapsedMilliseconds < 300)
				{
					h.Poll();
					a.Poll();
					Thread.Sleep(5);
				}
			}
			Check(a.State == ClientState.Connected && h.Players.Count == 2, "session unaffected");
			a.Send(MessageType.Chat, Payload.String("still alive"));
			string got = null;
			h.MessageReceived += (id, t, p) => got = Payload.ReadString(p);
			Until(() => got == "still alive", "host still works");
			h.Stop();
		}

		static void ConnectRefused()
		{
			var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
			l.Start();
			int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
			l.Stop();
			var c = Client("Nobody");
			string reason = null;
			c.Disconnected += r => reason = r;
			c.Connect("127.0.0.1", port);
			Until(() => reason != null, "failure reported", 6000);
			Check(reason.StartsWith("could not connect"), reason);
			Check(c.State == ClientState.Disconnected, "state");
		}

		static void PingRtt()
		{
			var h = Host();
			var a = Join(h, "A");
			Until(() => a.RttMs >= 0 && h.Players[1].RttMs >= 0, "rtt measured");
			Check(a.RttMs < 1000, "client rtt " + a.RttMs);
			h.Stop();
		}

		// ---- raw socket helpers (talk to the host without CoopClient) ----

		static void RawSend(TcpClient raw, MessageType type, byte[] payload)
		{
			int len = 1 + payload.Length;
			var frame = new byte[4 + len];
			BitConverter.GetBytes(len).CopyTo(frame, 0);
			frame[4] = (byte)type;
			payload.CopyTo(frame, 5);
			raw.GetStream().Write(frame, 0, frame.Length);
		}

		static Tuple<MessageType, byte[]> RawReceive(TcpClient raw, Action pump)
		{
			var s = raw.GetStream();
			var sw = Stopwatch.StartNew();
			while (raw.Available < 5)
			{
				if (sw.ElapsedMilliseconds > 3000)
					throw new Exception("no reply");
				pump();
				Thread.Sleep(5);
			}
			var header = new byte[4];
			s.Read(header, 0, 4);
			int len = BitConverter.ToInt32(header, 0);
			var body = new byte[len];
			int read = 0;
			while (read < len)
				read += s.Read(body, read, len - read);
			return Tuple.Create((MessageType)body[0], body.Skip(1).ToArray());
		}
	}
}
