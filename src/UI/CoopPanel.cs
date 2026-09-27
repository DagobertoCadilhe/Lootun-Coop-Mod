using LootunCoop.Net;
using UnityEngine;

namespace LootunCoop.UI
{
	/// <summary>Temporary IMGUI panel for connection testing. Toggled with F7 or the small "Co-op" button in the top-left corner.</summary>
	internal sealed class CoopPanel
	{
		const int WindowId = 0x4C43; // "LC"

		readonly CoopSession session;
		Rect rect = new Rect(20, 20, 340, 420);
		public bool Visible;
		string chatInput = "";
		string portText;
		string addressText;

		public CoopPanel(CoopSession session)
		{
			this.session = session;
		}

		public void Toggle() => Visible = !Visible;

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
				if (GUI.Button(new Rect(4, 4, 64, 20), "Co-op"))
					Toggle();
				return;
			}
			rect = GUILayout.Window(WindowId, rect, DrawWindow, "LootunCoop " + Plugin.PluginVersion + "  (F7)");
		}

		void DrawWindow(int id)
		{
			if (addressText == null)
			{
				addressText = Plugin.HostAddress.Value;
				portText = Plugin.Port.Value.ToString();
			}

			GUILayout.BeginHorizontal();
			GUILayout.Label("Name", GUILayout.Width(60));
			Plugin.PlayerName.Value = GUILayout.TextField(Plugin.PlayerName.Value, Protocol.MaxNameLength);
			GUILayout.EndHorizontal();

			GUILayout.Label(Status());
			if (!string.IsNullOrEmpty(session.LastError))
				GUILayout.Label("<color=#ff8080>" + session.LastError + "</color>");

			if (!session.IsActive)
			{
				GUILayout.BeginHorizontal();
				GUILayout.Label("Host IP", GUILayout.Width(60));
				addressText = GUILayout.TextField(addressText);
				portText = GUILayout.TextField(portText, 5, GUILayout.Width(55));
				GUILayout.EndHorizontal();

				GUILayout.BeginHorizontal();
				if (GUILayout.Button("Host (" + Plugin.MaxPlayers.Value + " players)"))
				{
					if (ParsePort(out int port))
						session.StartHost(Plugin.PlayerName.Value, port, Plugin.MaxPlayers.Value);
				}
				if (GUILayout.Button("Join"))
				{
					if (ParsePort(out int port) && addressText.Trim().Length > 0)
					{
						Plugin.HostAddress.Value = addressText.Trim();
						session.Join(Plugin.PlayerName.Value, addressText.Trim(), port);
					}
				}
				GUILayout.EndHorizontal();
			}
			else if (GUILayout.Button(session.IsHost ? "Stop hosting" : "Leave"))
				session.Leave();

			GUILayout.Space(6);
			GUILayout.Label("Players");
			foreach (var p in session.Players)
			{
				string ping = p.Id == session.LocalPlayerId ? "you"
					: session.IsHost ? (p.RttMs >= 0 ? p.RttMs + " ms" : "...")
					: p.Id == Protocol.HostPlayerId ? "host" : "";
				GUILayout.Label("  #" + p.Id + "  " + p.Name + "   " + ping);
			}

			GUILayout.Space(6);
			foreach (var line in session.Chat)
				GUILayout.Label(line);

			GUI.enabled = session.IsHost || (session.Client != null && session.Client.State == ClientState.Connected);
			GUILayout.BeginHorizontal();
			bool enter = Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return
				&& GUI.GetNameOfFocusedControl() == "coopChat";
			GUI.SetNextControlName("coopChat");
			chatInput = GUILayout.TextField(chatInput, 200);
			if (GUILayout.Button("Send", GUILayout.Width(60)) || enter)
			{
				session.SendChat(chatInput);
				chatInput = "";
			}
			GUILayout.EndHorizontal();
			GUI.enabled = true;

			GUI.DragWindow();
		}

		string Status()
		{
			if (session.IsHost)
				return "Hosting on port " + session.Host.Port + " - " + session.Players.Count + "/" + Plugin.MaxPlayers.Value;
			if (session.IsClient)
			{
				var c = session.Client;
				return c.State == ClientState.Connected ? "Connected, ping " + (c.RttMs >= 0 ? c.RttMs + " ms" : "...") : c.State + "...";
			}
			return "Not connected";
		}

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
