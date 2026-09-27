using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LootunCoop.UI
{
	/// <summary>
	/// A "Co-op" button in the game's left menu, cloned from the Achievements button and placed above it. Opens the co-op
	/// panel and shows the session state. Falls back to the small IMGUI button if the menu can't be found.
	/// </summary>
	internal static class MenuButton
	{
		static GameObject go;
		static TMP_Text text;
		static float nextTry, nextRefresh;
		static bool failed;

		public static void Update(CoopPanel panel, CoopSession session)
		{
			if (failed)
				return;
			if (go == null)
			{
				panel.HasMenuButton = false;
				if (Time.unscaledTime < nextTry || GameData.GameController == null)
					return;
				nextTry = Time.unscaledTime + 2f;
				if (!TryCreate(panel))
					return;
			}
			if (Time.unscaledTime < nextRefresh || text == null)
				return;
			nextRefresh = Time.unscaledTime + 0.5f;
			string label = session.IsHost ? "Co-op " + session.Players.Count + "/" + Plugin.MaxPlayers.Value
				: session.IsClient ? "Co-op (" + session.Players.Count + ")" : "Co-op";
			if (text.text != label)
			{
				text.text = label;
				text.color = session.IsActive ? new Color(0.58f, 1f, 0.43f) : Color.white;
			}
		}

		static bool TryCreate(CoopPanel panel)
		{
			var all = Object.FindObjectsOfType<MenuButtonController>(true);
			if (all.Length == 0)
				return false;
			var template = all.FirstOrDefault(b => b.Menu == GameMenus.Achievments) ?? all.FirstOrDefault(b => b.Menu == GameMenus.Glossary);
			if (template == null)
			{
				failed = true;
				Plugin.Log.LogWarning("[ui] no Achievements/Glossary menu button to copy; keeping the small Co-op button");
				return false;
			}
			try
			{
				go = Object.Instantiate(template.gameObject, template.transform.parent, false);
				go.name = "LootunCoopMenuButton";
				go.transform.SetSiblingIndex(template.transform.GetSiblingIndex());
				if (template.transform.parent.GetComponent<LayoutGroup>() == null)
				{
					var rt = (RectTransform)go.transform;
					rt.anchoredPosition += new Vector2(0, rt.rect.height + 4);
				}
				go.SetActive(true);

				var ctrl = go.GetComponent<MenuButtonController>();
				ctrl.UnlockButton(true);
				ctrl.LockPanel?.SetActive(false);
				ctrl.MenuButton.gameObject.SetActive(true);
				ctrl.MenuButton.onClick = new Button.ButtonClickedEvent();
				ctrl.MenuButton.onClick.AddListener(panel.Toggle);

				foreach (var loc in go.GetComponentsInChildren<LocalisationTMPTextField>(true))
					Object.DestroyImmediate(loc);
				text = ctrl.MenuButtonText;
				text.text = "Co-op";

				var icon = go.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i != ctrl.MenuButtonBackground && i.sprite != null
					&& (ctrl.LockPanel == null || !i.transform.IsChildOf(ctrl.LockPanel.transform)) && i != ctrl.MenuButton.targetGraphic);
				if (icon != null)
				{
					var avatar = Resources.Load<Sprite>("Avatars/Players/Male1");
					if (avatar != null)
					{
						icon.sprite = avatar;
						icon.preserveAspect = true;
					}
					else
						icon.enabled = false;
				}
				panel.HasMenuButton = true;
				Plugin.Log.LogInfo("[ui] added Co-op button to the left menu");
				return true;
			}
			catch (System.Exception e)
			{
				failed = true;
				if (go != null)
					Object.Destroy(go);
				go = null;
				Plugin.Log.LogWarning("[ui] could not add the Co-op menu button, keeping the small one: " + e);
				return false;
			}
		}
	}
}
