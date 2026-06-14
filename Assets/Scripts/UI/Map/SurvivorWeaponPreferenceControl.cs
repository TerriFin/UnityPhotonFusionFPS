using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SimpleFPS
{
	[DisallowMultipleComponent]
	public sealed class SurvivorWeaponPreferenceControl : MonoBehaviour
	{
		public Button CycleButton;
		public Image Background;
		public TextMeshProUGUI Label;

		[Header("Labels")]
		public string AutomaticLabel = "AUTO";
		public string StrongLabel = "STRONG";
		public string PistolLabel = "PISTOL";

		[Header("Colors")]
		public Color AutomaticColor = new Color(0.18f, 0.55f, 0.72f, 0.95f);
		public Color StrongColor = new Color(0.78f, 0.28f, 0.12f, 0.95f);
		public Color PistolColor = new Color(0.42f, 0.46f, 0.5f, 0.95f);

		public ESurvivorWeaponPreference Value { get; private set; }
		public event Action<ESurvivorWeaponPreference> ValueChanged;

		private bool _listenerAttached;

		private void Awake()
		{
			EnsureRuntimeControl();
			AttachListener();
			RefreshVisual();
		}

		public void SetValueWithoutNotify(ESurvivorWeaponPreference value)
		{
			Value = IsValid(value) ? value : ESurvivorWeaponPreference.Automatic;
			RefreshVisual();
		}

		private void HandleClicked()
		{
			Value = Value switch
			{
				ESurvivorWeaponPreference.Automatic => ESurvivorWeaponPreference.PreferStrongWeapons,
				ESurvivorWeaponPreference.PreferStrongWeapons => ESurvivorWeaponPreference.PreferPistol,
				_ => ESurvivorWeaponPreference.Automatic,
			};

			RefreshVisual();
			ValueChanged?.Invoke(Value);
		}

		private void AttachListener()
		{
			if (_listenerAttached || CycleButton == null)
				return;

			CycleButton.onClick.AddListener(HandleClicked);
			_listenerAttached = true;
		}

		private void EnsureRuntimeControl()
		{
			if (Background == null)
			{
				Background = GetComponent<Image>();
				if (Background == null)
					Background = gameObject.AddComponent<Image>();
			}

			if (CycleButton == null)
			{
				CycleButton = GetComponent<Button>();
				if (CycleButton == null)
					CycleButton = gameObject.AddComponent<Button>();
			}
			CycleButton.targetGraphic = Background;

			if (Label == null)
			{
				Transform existing = transform.Find("Label");
				Label = existing != null ? existing.GetComponent<TextMeshProUGUI>() : null;
			}

			if (Label == null)
			{
				var labelObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
				labelObject.transform.SetParent(transform, false);
				RectTransform rect = labelObject.GetComponent<RectTransform>();
				rect.anchorMin = Vector2.zero;
				rect.anchorMax = Vector2.one;
				rect.offsetMin = Vector2.zero;
				rect.offsetMax = Vector2.zero;
				Label = labelObject.GetComponent<TextMeshProUGUI>();
				Label.fontSize = 9f;
				Label.alignment = TextAlignmentOptions.Center;
				Label.color = Color.white;
				Label.enableWordWrapping = false;
				Label.raycastTarget = false;
			}
		}

		private void RefreshVisual()
		{
			if (Label != null)
			{
				Label.text = Value switch
				{
					ESurvivorWeaponPreference.PreferStrongWeapons => StrongLabel,
					ESurvivorWeaponPreference.PreferPistol => PistolLabel,
					_ => AutomaticLabel,
				};
			}

			if (Background != null)
			{
				Background.color = Value switch
				{
					ESurvivorWeaponPreference.PreferStrongWeapons => StrongColor,
					ESurvivorWeaponPreference.PreferPistol => PistolColor,
					_ => AutomaticColor,
				};
			}
		}

		private static bool IsValid(ESurvivorWeaponPreference value)
		{
			return value == ESurvivorWeaponPreference.Automatic ||
			       value == ESurvivorWeaponPreference.PreferStrongWeapons ||
			       value == ESurvivorWeaponPreference.PreferPistol;
		}
	}
}
