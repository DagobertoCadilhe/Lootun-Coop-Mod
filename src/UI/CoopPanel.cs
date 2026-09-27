using System.Linq;
using LootunCoop.Game;
using LootunCoop.Net;
using UnityEngine;

namespace LootunCoop.UI
{
	/// <summary>
	/// IMGUI co-op panel: Connection, Party, Co-op mission and Log sections, plus a "Next" hint that says what to do.
	/// Toggled with F7 or the Co-op button in the game's left menu (<see cref="MenuButton"/>).
	/// </summary>
	internal sealed class CoopPanel
	{
		const int WindowId = 0x4C43; // "LC"
		const string Accent = "#8cf";
		const string Good = "#95ff6d";
		const string Warn = "#ffc4b1";
		const string Bad = "#ff8080";
		const string Dim = "#aaaaaa";

		readonly CoopSession session;
		Rect rect = new Rect(140, 60, 400, 520);
		public bool Visible;
		string portText;
		string addressText;
		bool showTools;
		GUIStyle header, box, rich;

		public CoopPanel(CoopSession session)
		{
			this.session = session;
		}

		public void Toggle() => Visible = !Visible;

		/// <summary>True once the in-game menu button exists; the IMGUI fallback button is hidden then.</summary>
		public bool HasMenuButton;

		/// <param name="handleKey">Read F7 from IMGUI events (used when the game has legacy Input disabled).</param>
		public void OnGUI(bool handleKey)
		{
			var e = Event.current;
			if (handleKey && e.type == EventType.KeyDown && e.keyCode == KeyCode.F7)
			{
				Toggle();
				e.Use();
			}
			if (!Visible)
			{
				if (!HasMenuButton && GUI.Button(new Rect(4, 4, 64, 20), "Co-op"))
					Toggle();
				return;
			}
			if (header == null)
			{
				rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
				header = new GUIStyle(rich) { fontStyle = FontStyle.Bold, fontSize = 14 }; // fontSize 0 means "font default", so no +2 on it
				box = new GUIStyle(GUI.skin.box) { padding = new RectOffset(8, 8, 6, 6) };
			}
			rect = GUILayout.Window(WindowId, rect, DrawWindow, "Co-op  -  LootunCoop " + Plugin.PluginVersion + "  (F7)");
		}

		void DrawWindow(int id)
		{
			if (addressText == null)
			{
				addressText = Plugin.HostAddress.Value;
				portText = Plugin.Port.Value.ToString();
			}

			GUILayout.BeginHorizontal();
			GUILayout.Label(Status(), header);
			if (GUILayout.Button("X", GUILayout.Width(24)))
				Visible = false;
			GUILayout.EndHorizontal();
			if (!string.IsNullOrEmpty(session.LastError))
				GUILayout.Label(Color(session.LastError, Bad), rich);
			GUILayout.Label(Color("Next: ", Accent) + NextStep(), rich);

			Section("Connection");
			DrawConnection();
			GUILayout.EndVertical();

			if (session.IsActive)
			{
				Section("Party");
				DrawParty();
				GUILayout.EndVertical();
			}

			if (session.IsHost)
			{
				Section("Co-op mission");
				DrawHostMission();
				GUILayout.EndVertical();
			}
			else if (IsConnectedClient)
			{
				Section("Your character");
				DrawClientCharacter();
				GUILayout.EndVertical();
			}

			if (session.IsActive)
			{
				Section("Log");
				DrawLog();
				GUILayout.EndVertical();
			}

			GUI.DragWindow();
		}

		void Section(string title)
		{
			GUILayout.Space(4);
			GUILayout.BeginVertical(box);
			GUILayout.Label(title, header);
		}

		void DrawConnection()
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label("Your name", GUILayout.Width(70));
			GUI.enabled = !session.IsActive;
			Plugin.PlayerName.Value = GUILayout.TextField(Plugin.PlayerName.Value, Protocol.MaxNameLength);
			GUI.enabled = true;
			GUILayout.EndHorizontal();

			if (session.IsActive)
			{
				if (GUILayout.Button(session.IsHost ? "Stop hosting" : "Leave session"))
					session.Leave();
				return;
			}

			GUILayout.BeginHorizontal();
			GUILayout.Label("Host IP", GUILayout.Width(70));
			addressText = GUILayout.TextField(addressText);
			GUILayout.Label("Port", GUILayout.Width(30));
			portText = GUILayout.TextField(portText, 5, GUILayout.Width(50));
			GUILayout.EndHorizontal();

			GUILayout.BeginHorizontal();
			if (GUILayout.Button("Host a session (" + Plugin.MaxPlayers.Value + " players)"))
			{
				if (ParsePort(out int port))
					session.StartHost(Plugin.PlayerName.Value, port, Plugin.MaxPlayers.Value);
			}
			if (GUILayout.Button("Join host IP", GUILayout.Width(110)))
			{
				if (ParsePort(out int port) && addressText.Trim().Length > 0)
				{
					Plugin.HostAddress.Value = addressText.Trim();
					session.Join(Plugin.PlayerName.Value, addressText.Trim(), port);
				}
				else if (addressText.Trim().Length == 0)
					session.LastError = "enter the host's IP (e.g. their Radmin VPN IP)";
			}
			GUILayout.EndHorizontal();
		}

		void DrawParty()
		{
			foreach (var p in session.Players)
			{
				string role = p.Id == Protocol.HostPlayerId ? "host" : "guest";
				string you = p.Id == session.LocalPlayerId ? Color(" (you)", Accent) : "";
				string ping = p.Id == session.LocalPlayerId ? ""
					: session.IsHost ? "  " + Color(p.RttMs >= 0 ? p.RttMs + " ms" : "...", Dim) : "";
				GUILayout.Label(Color(role, Dim) + "  " + p.Name + you + ping, rich);
				string character = CharacterOf(p);
				if (character != null)
					GUILayout.Label("      " + character, rich);
			}
			if (session.IsHost)
			{
				foreach (var g in CoopMission.Guests.Where(g => g.Key < 0))
					GUILayout.Label(Color("test", Dim) + "  " + CharacterCodec.Describe(g.Value.Character), rich);
			}
		}

		string CharacterOf(PlayerInfo p)
		{
			if (session.IsHost)
			{
				if (p.Id == Protocol.HostPlayerId)
					return null;
				return CoopMission.Guests.TryGetValue(p.Id, out var g)
					? Color(CharacterCodec.Describe(g.Character), Good)
					: Color("no character sent yet", Warn);
			}
			if (p.Id == session.LocalPlayerId)
				return session.SentCharacter != null ? Color(session.SentCharacter, Good) : Color("no character sent yet", Warn);
			return null;
		}

		void DrawHostMission()
		{
			if (CoopMission.IsRunning)
			{
				var enc = CoopMission.Encounter;
				GUILayout.Label(Color("Running", Good) + " on " + enc.Map.Name + " - " + enc.Characters.Count + " characters", rich);
				if (GUILayout.Button("End co-op mission"))
					CoopMission.End();
			}
			else
			{
				int guests = CoopMission.Guests.Count;
				GUILayout.Label(guests == 0 ? Color("No guest characters yet.", Warn)
					: guests + " guest character(s) ready. You can add up to " + CoopMission.OwnSlotsLeft(PrepareMissionButton.Window) + " of yours.", rich);
				GUI.enabled = guests > 0 && PrepareMissionButton.WindowOpen;
				if (GUILayout.Button(PrepareMissionButton.WindowOpen ? "Begin Co-op with this Prepare Mission" : "Open a mission's Prepare Mission first"))
					CoopMission.BeginFromPrepare(PrepareMissionButton.Window);
				GUI.enabled = true;
			}
			showTools = GUILayout.Toggle(showTools, " Testing tools");
			if (showTools)
			{
				GUI.enabled = CoopMission.CanAddClone;
				if (GUILayout.Button(CoopMission.CanAddClone ? "Add a clone of my selected character as a guest"
					: "Party full (" + CoopMission.MaxGuests + " guests max)"))
					session.AddTestClone();
				GUI.enabled = CoopMission.Guests.Keys.Any(k => k < 0);
				if (GUILayout.Button("Update clones from my selected character"))
					session.RefreshTestClones();
				GUI.enabled = true;
			}
		}

		void DrawClientCharacter()
		{
			if (CoopMirror.IsRunning)
			{
				GUILayout.Label(Color("In co-op", Good) + " on " + CoopMirror.Encounter.Map.Name + " with " + CharacterCodec.Describe(CoopMirror.Own ?? session.CoopCharacter), rich);
				GUILayout.Label(Color("Your character's skills, passives and loadout are locked until the mission ends. Gear changes are"
					+ " sent to the host automatically. XP, your share of items, gold and rubies arrive as you play.", Dim), rich);
				return;
			}
			if (CoopMirror.Problem != null)
				GUILayout.Label(Color("Can't show the co-op mission: " + CoopMirror.Problem, Bad), rich);
			var c = GameData.CurrentCharacter;
			GUILayout.Label("Selected: " + (c != null ? CharacterCodec.Describe(c) : Color("none", Warn)), rich);
			if (session.SendProblem != null)
				GUILayout.Label(Color(session.SendProblem, Warn), rich);
			GUILayout.Label(Color("Select a character in Equipment: it's sent to the host automatically, and so are later changes"
				+ " (gear, passives, skills). One character per player. Keep one mission slot free to watch the fight.", Dim), rich);
		}

		void DrawLog()
		{
			foreach (var line in session.Chat)
				GUILayout.Label(line.StartsWith("* ") ? Color(line, Dim) : line, rich);
		}

		bool IsConnectedClient => session.Client != null && session.Client.State == ClientState.Connected;

		string Status()
		{
			if (session.IsHost)
				return Color("Hosting", Good) + "  port " + session.Host.Port + "  -  " + session.Players.Count + "/" + Plugin.MaxPlayers.Value + " players";
			if (session.IsClient)
			{
				var c = session.Client;
				return c.State == ClientState.Connected
					? Color("Connected", Good) + "  ping " + (c.RttMs >= 0 ? c.RttMs + " ms" : "...")
					: Color(c.State + "...", Warn);
			}
			return Color("Not connected", Dim);
		}

		string NextStep()
		{
			if (session.IsHost)
			{
				if (CoopMission.IsRunning)
					return "the co-op mission is running. End it here when you're done.";
				if (session.Players.Count <= 1 && CoopMission.Guests.Count == 0)
					return "give your IP (e.g. Radmin VPN) and port " + session.Host.Port + " to your friends and wait for them to join.";
				var waiting = session.Players.Where(p => p.Id != Protocol.HostPlayerId && !CoopMission.Guests.ContainsKey(p.Id)).Select(p => p.Name).ToList();
				if (CoopMission.Guests.Count == 0)
					return "waiting for " + string.Join(", ", waiting) + " to pick a character.";
				return "Mission board -> pick a mission -> Prepare Mission -> choose your character(s) -> Begin Co-op."
					+ (waiting.Count > 0 ? Color("  (still waiting for " + string.Join(", ", waiting) + ")", Dim) : "");
			}
			if (IsConnectedClient && CoopMirror.IsRunning)
				return "you're in the co-op mission: watch it in the Combat screen (CO-OP slot).";
			if (IsConnectedClient)
				return session.SentCharacter == null
					? "select your character in Equipment; it is sent automatically."
					: "wait for the host to begin the co-op mission. Gear or passive changes are sent automatically.";
			if (session.IsClient)
				return "connecting...";
			return "host a session, or enter the host's IP and join.";
		}

		static string Color(string text, string color) => "<color=" + color + ">" + text + "</color>";

		bool ParsePort(out int port)
		{
			if (int.TryParse(portText, out port) && port > 0 && port < 65536)
			{
				Plugin.Port.Value = port;
				return true;
			}
			session.LastError = "invalid port";
			return false;
		}
	}
}
