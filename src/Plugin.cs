using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace LootunCoop
{
	/// <summary>Co-op mod skeleton. See docs/MULTIPLAYER.md for the design and phases.</summary>
	[BepInPlugin(PluginGUID, PluginName, PluginVersion)]
	public class Plugin : BaseUnityPlugin
	{
		public const string PluginGUID = "personal.lootuncoop";
		public const string PluginName = "LootunCoop";
		public const string PluginVersion = "0.0.1";

		internal static ManualLogSource Log;

		private void Awake()
		{
			Log = Logger;
			new Harmony(PluginGUID).PatchAll();
			Log.LogInfo(PluginName + " " + PluginVersion + " loaded (skeleton, no networking yet).");
		}
	}
}
