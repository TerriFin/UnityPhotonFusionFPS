using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SimpleFPS
{
	[DisallowMultipleComponent]
	public sealed class SurvivorRetreatModeControl : MonoBehaviour
	{
		public Button CycleButton;
		public Image Background;
		public TextMeshProUGUI Label;

		public Color NoRetreatColor = new Color(0.28f, 0.24f, 0.32f, 0.95f);
		public Color Retreat75Color = new Color(0.74f, 0.24f, 0.16f, 0.95f);
		public Color Retreat50Color = new Color(0.75f, 0.42f, 0.14f, 0.95f);
		public Color Retreat25Color = new Color(0.38f, 0.58f, 0.2f, 0.95f);

		public ESurvivorRetreatMode Value { get; private set; }
		public event Action<ESurvivorRetreatMode> ValueChanged;

		private bool _listenerAttached;

		private void Awake()
		{
			EnsureRuntimeControl();
			AttachListener();
			RefreshVisual();
		}

		public void SetValueWithoutNotify(ESurvivorRetreatMode value)
		{
			Value = Enum.IsDefined(typeof(ESurvivorRetreatMode), value)
				? value
				: ESurvivorRetreatMode.NoRetreat;
			RefreshVisual();
		}

		private void HandleClicked()
		{
			Value = Value switch
			{
				ESurvivorRetreatMode.NoRetreat => ESurvivorRetreatMode.RetreatAt25Percent,
				ESurvivorRetreatMode.RetreatAt25Percent => ESurvivorRetreatMode.RetreatAt50Percent,
				ESurvivorRetreatMode.RetreatAt50Percent => ESurvivorRetreatMode.RetreatAt75Percent,
				_ => ESurvivorRetreatMode.NoRetreat,
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
			Background = Background != null ? Background : GetComponent<Image>();
			if (Background == null)
				Background = gameObject.AddComponent<Image>();

			CycleButton = CycleButton != null ? CycleButton : GetComponent<Button>();
			if (CycleButton == null)
				CycleButton = gameObject.AddComponent<Button>();
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
					ESurvivorRetreatMode.RetreatAt75Percent => "75%",
					ESurvivorRetreatMode.RetreatAt50Percent => "50%",
					ESurvivorRetreatMode.RetreatAt25Percent => "25%",
					_ => "NONE",
				};
			}

			if (Background != null)
			{
				Background.color = Value switch
				{
					ESurvivorRetreatMode.RetreatAt75Percent => Retreat75Color,
					ESurvivorRetreatMode.RetreatAt50Percent => Retreat50Color,
					ESurvivorRetreatMode.RetreatAt25Percent => Retreat25Color,
					_ => NoRetreatColor,
				};
			}
		}
	}
}
