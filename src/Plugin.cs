using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LootunCoop.Net;
using LootunCoop.UI;

namespace LootunCoop
{
	/// <summary>Co-op mod entry point. See docs/MULTIPLAYER.md for the design and phases.</summary>
	[BepInPlugin(PluginGUID, PluginName, PluginVersion)]
	public class Plugin : BaseUnityPlugin
	{
		public const string PluginGUID = "personal.lootuncoop";
		public const string PluginName = "LootunCoop";
		public const string PluginVersion = "0.0.1";

		internal static ManualLogSource Log;
		internal static ConfigEntry<string> PlayerName;
		internal static ConfigEntry<string> HostAddress;
		internal static ConfigEntry<int> Port;
		internal static ConfigEntry<int> MaxPlayers;

		CoopSession session;
		CoopPanel panel;

		private void Awake()
		{
			Log = Logger;
			PlayerName = Config.Bind("Coop", "PlayerName", "Player", "Name shown to other players.");
			HostAddress = Config.Bind("Coop", "HostAddress", "", "Last host IP you joined (e.g. the host's Radmin VPN IP).");
			Port = Config.Bind("Coop", "Port", Protocol.DefaultPort, "TCP port. The host must allow it through the Windows firewall.");
			MaxPlayers = Config.Bind("Coop", "MaxPlayers", 3,
				new ConfigDescription("Players per session including the host. 4 is raid-sized (untested).", new AcceptableValueRange<int>(2, Protocol.MaxPlayersLimit)));

			session = new CoopSession();
			panel = new CoopPanel(session);
			new Harmony(PluginGUID).PatchAll();
			Log.LogInfo(PluginName + " " + PluginVersion + " loaded. Press F7 for the co-op panel.");
		}

		private void Update()
		{
			session.Poll();
		}

		private void OnGUI()
		{
			panel.OnGUI();
		}

		private void OnApplicationQuit()
		{
			session.Leave();
		}
	}
}
