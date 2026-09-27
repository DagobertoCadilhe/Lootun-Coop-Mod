using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LootunCoop.Net;

namespace LootunCoop.TestClient
{
	/// <summary>
	/// Usage: LootunCoop.TestClient.exe [host] [port] [name] [mods]
	/// mods = comma-separated "guid@version" list; must match the host's BepInEx plugins (the host's reject message lists differences).
	/// Type a line to chat, /quit to leave.
	/// </summary>
	internal static class Program
	{
		static int Main(string[] args)
		{
			string host = args.Length > 0 ? args[0] : "127.0.0.1";
			int port = args.Length > 1 ? int.Parse(args[1]) : Protocol.DefaultPort;
			string name = args.Length > 2 ? args[2] : "TestClient";
			var mods = (args.Length > 3 ? args[3] : "personal.lootuncoop@0.0.1").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(m => m.Trim()).ToList();

			var client = new CoopClient(new ClientOptions { PlayerName = name, ModVersion = "0.0.1", Mods = mods });
			bool done = false;
			client.Log += m => Console.WriteLine("[client] " + m);
			client.Connected += () => Console.WriteLine("players: " + string.Join(", ", client.Players.Select(p => p.ToString())));
			client.Disconnected += r => done = true;
			client.PlayerJoined += p => Console.WriteLine("* " + p + " joined");
			client.PlayerLeft += (p, r) => Console.WriteLine("* " + p + " left (" + r + ")");
			client.MessageReceived += (type, payload) =>
			{
				if (type != MessageType.Chat)
				{
					Console.WriteLine("message " + type + " (" + payload.Length + " bytes)");
					return;
				}
				using (var r = new BinaryReader(new MemoryStream(payload)))
				{
					int sender = r.ReadInt32();
					string text = r.ReadString();
					var p = client.Players.FirstOrDefault(x => x.Id == sender);
					Console.WriteLine((p?.Name ?? "#" + sender) + ": " + text);
				}
			};

			var input = new ConcurrentQueue<string>();
			new Thread(() =>
			{
				string line;
				while ((line = Console.ReadLine()) != null)
					input.Enqueue(line);
				input.Enqueue("/quit");
			}) { IsBackground = true }.Start();

			client.Connect(host, port);
			while (!done)
			{
				client.Poll();
				while (input.TryDequeue(out var line))
				{
					if (line.Trim() == "/quit")
					{
						client.Disconnect();
						break;
					}
					if (line.Trim() == "/ping")
						Console.WriteLine("ping " + client.RttMs + " ms");
					else
						client.Send(MessageType.Chat, Payload.Build(w =>
						{
							w.Write(client.LocalPlayerId);
							w.Write(line);
						}));
				}
				Thread.Sleep(10);
			}
			return 0;
		}
	}
}
