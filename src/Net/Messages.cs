using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LootunCoop.Net
{
	public sealed class PlayerInfo
	{
		public int Id;
		public string Name;
		/// <summary>Last measured round-trip time to this player in ms, or -1 if unknown. Only meaningful on the host.</summary>
		public int RttMs = -1;

		public PlayerInfo(int id, string name)
		{
			Id = id;
			Name = name;
		}

		public override string ToString() => Name + " #" + Id;

		internal void Write(BinaryWriter w)
		{
			w.Write(Id);
			w.Write(Name);
		}

		internal static PlayerInfo Read(BinaryReader r) => new PlayerInfo(r.ReadInt32(), r.ReadString());
	}

	/// <summary>Helpers to build and parse payloads. All strings are UTF-8, length-prefixed (BinaryWriter format).</summary>
	public static class Payload
	{
		public static byte[] Build(Action<BinaryWriter> write)
		{
			using (var ms = new MemoryStream())
			{
				using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
					write(w);
				return ms.ToArray();
			}
		}

		public static T Parse<T>(byte[] payload, Func<BinaryReader, T> read)
		{
			using (var r = new BinaryReader(new MemoryStream(payload, false), Encoding.UTF8))
				return read(r);
		}

		public static byte[] String(string s) => Build(w => w.Write(s ?? ""));

		public static string ReadString(byte[] payload) => Parse(payload, r => r.ReadString());

		public static byte[] Long(long v) => Build(w => w.Write(v));

		public static long ReadLong(byte[] payload) => Parse(payload, r => r.ReadInt64());
	}

	internal sealed class HelloMessage
	{
		public int ProtocolVersion;
		public string ModVersion;
		public string PlayerName;
		public List<string> Mods = new List<string>();

		public byte[] ToPayload() => Payload.Build(w =>
		{
			w.Write(ProtocolVersion);
			w.Write(ModVersion ?? "");
			w.Write(PlayerName ?? "");
			w.Write(Mods.Count);
			foreach (var m in Mods)
				w.Write(m);
		});

		public static HelloMessage Parse(byte[] payload) => Payload.Parse(payload, r =>
		{
			var h = new HelloMessage
			{
				ProtocolVersion = r.ReadInt32(),
				ModVersion = r.ReadString(),
				PlayerName = r.ReadString(),
			};
			int n = r.ReadInt32();
			if (n < 0 || n > 4096)
				throw new InvalidDataException("bad mod count " + n);
			for (int i = 0; i < n; i++)
				h.Mods.Add(r.ReadString());
			return h;
		});
	}

	internal sealed class WelcomeMessage
	{
		public int PlayerId;
		public List<PlayerInfo> Players = new List<PlayerInfo>();

		public byte[] ToPayload() => Payload.Build(w =>
		{
			w.Write(PlayerId);
			w.Write(Players.Count);
			foreach (var p in Players)
				p.Write(w);
		});

		public static WelcomeMessage Parse(byte[] payload) => Payload.Parse(payload, r =>
		{
			var m = new WelcomeMessage { PlayerId = r.ReadInt32() };
			int n = r.ReadInt32();
			if (n < 0 || n > Protocol.MaxPlayersLimit)
				throw new InvalidDataException("bad player count " + n);
			for (int i = 0; i < n; i++)
				m.Players.Add(PlayerInfo.Read(r));
			return m;
		});
	}

	internal static class PlayerLeftMessage
	{
		public static byte[] ToPayload(int id, string reason) => Payload.Build(w =>
		{
			w.Write(id);
			w.Write(reason ?? "");
		});

		public static void Parse(byte[] payload, out int id, out string reason)
		{
			using (var r = new BinaryReader(new MemoryStream(payload, false), Encoding.UTF8))
			{
				id = r.ReadInt32();
				reason = r.ReadString();
			}
		}
	}
}
