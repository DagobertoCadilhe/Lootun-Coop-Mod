using System;
using LootunCoop.UI;
using UnityEngine;

namespace LootunCoop
{
	/// <summary>
	/// Drives the session and panel from our own hidden, persistent GameObject. Some games destroy or disable BepInEx's manager
	/// object after startup, which silently stops the plugin's Update/OnGUI.
	/// </summary>
	internal sealed class CoopRunner : MonoBehaviour
	{
		internal CoopSession Session;
		internal CoopPanel Panel;
		bool loggedUpdate;
		bool loggedGui;
		bool legacyInput = true;
		bool quitting;

		internal static CoopRunner Create(CoopSession session, CoopPanel panel)
		{
			var go = new GameObject("LootunCoop");
			DontDestroyOnLoad(go);
			go.hideFlags = HideFlags.HideAndDontSave;
			var runner = go.AddComponent<CoopRunner>();
			runner.Session = session;
			runner.Panel = panel;
			return runner;
		}

		void Update()
		{
			if (!loggedUpdate)
			{
				loggedUpdate = true;
				Plugin.Log.LogInfo("runner: Update is running");
			}
			if (legacyInput)
			{
				try
				{
					if (Input.GetKeyDown(KeyCode.F7))
						Panel.Toggle();
				}
				catch (Exception e)
				{
					legacyInput = false;
					Plugin.Log.LogInfo("runner: legacy Input unavailable, using IMGUI key events only (" + e.GetType().Name + ")");
				}
			}
			Session.Poll();
		}

		void OnGUI()
		{
			if (!loggedGui)
			{
				loggedGui = true;
				Plugin.Log.LogInfo("runner: OnGUI is running");
			}
			Panel.OnGUI(handleKey: !legacyInput);
		}

		void OnApplicationQuit()
		{
			quitting = true;
			Session.Leave();
		}

		void OnDisable()
		{
			if (!quitting)
				Plugin.Log.LogWarning("runner: disabled/destroyed by the game; co-op panel and networking stop");
		}
	}
}
