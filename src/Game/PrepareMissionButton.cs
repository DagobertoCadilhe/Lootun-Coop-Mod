using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LootunCoop.Game
{
	/// <summary>
	/// Adds a "Begin Co-op" button next to Prepare Mission's "Begin Mission" (visible only while hosting) and lists the joining
	/// guests in the window title.
	/// </summary>
	[HarmonyPatch(typeof(PrepareMissionController), nameof(PrepareMissionController.LoadMission))]
	static class PrepareMissionButton
	{
		/// <summary>The last opened Prepare Mission window.</summary>
		internal static PrepareMissionController Window;
		static GameObject button;
		static TMP_Text label;
		static bool searched;

		internal static bool WindowOpen => Window != null && Window.gameObject.activeInHierarchy;

		static void Postfix(PrepareMissionController __instance)
		{
			Window = __instance;
			if (!searched)
			{
				searched = true;
				button = Create(__instance);
				CoopMission.Changed += () =>
				{
					if (WindowOpen)
						PrepareMissionSlots.Fill(Window);
				};
			}
			PrepareMissionSlots.Fill(__instance);
			Refresh();
		}

		/// <summary>Updates visibility, label and title. Cheap; called when the window opens and periodically while it is open.</summary>
		internal static void Refresh()
		{
			if (!WindowOpen)
				return;
			bool hosting = Plugin.Session.IsHost;
			if (button != null && button.activeSelf != hosting)
				button.SetActive(hosting);
			int guests = CoopMission.Guests.Count;
			if (label != null)
				label.text = guests > 0 ? "Begin Co-op (+" + guests + ")" : "Begin Co-op";

			var map = Window.Map;
			if (Window.TitleText == null || map == null)
				return;
			string title = map.Name + " - " + map.LevelText;
			if (hosting && guests > 0)
				title += "   <color=#8cf>Co-op: + " + string.Join(", ", CoopMission.Guests.Values.Select(g => g.Character.Name + " (" + g.Owner + ")"))
					+ "  -  pick up to " + CoopMission.OwnSlotsLeft(Window) + "</color>";
			if (Window.TitleText.text != title)
				Window.TitleText.text = title;
		}

		static GameObject Create(PrepareMissionController window)
		{
			Button begin = null;
			foreach (var b in window.GetComponentsInChildren<Button>(true))
			{
				for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
				{
					if (b.onClick.GetPersistentMethodName(i) == nameof(PrepareMissionController.ButtonBeginMission_Click))
						begin = b;
				}
			}
			if (begin == null)
			{
				Plugin.Log.LogWarning("[coop] Begin Mission button not found; use the F7 panel to begin co-op");
				return null;
			}
			var go = Object.Instantiate(begin.gameObject, begin.transform.parent, false);
			go.name = "LootunCoopBeginButton";
			go.transform.SetSiblingIndex(begin.transform.GetSiblingIndex() + 1);
			if (begin.transform.parent.GetComponent<LayoutGroup>() == null)
			{
				// to the left of Begin, 30% wider so the longer label fits
				var rt = (RectTransform)go.transform;
				float w = rt.rect.width, w2 = w * 1.3f, px = rt.pivot.x;
				rt.sizeDelta += new Vector2(w2 - w, 0);
				rt.anchoredPosition -= new Vector2(px * w + 12 + (1 - px) * w2, 0);
			}
			var copy = go.GetComponent<Button>();
			copy.onClick = new Button.ButtonClickedEvent();
			copy.onClick.AddListener(() => CoopMission.BeginFromPrepare(window));
			// the game's localisation component would reset the label to "Begin" on Start
			foreach (var loc in go.GetComponentsInChildren<LocalisationTMPTextField>(true))
				Object.DestroyImmediate(loc);
			label = go.GetComponentInChildren<TMP_Text>(true);
			if (label != null)
			{
				label.color = new Color(0.55f, 0.8f, 1f);
				label.fontSizeMax = label.fontSize;
				label.enableAutoSizing = true;
				label.fontSizeMin = 8f;
			}
			Plugin.Log.LogInfo("[coop] added Begin Co-op button to Prepare Mission");
			return go;
		}
	}
}
