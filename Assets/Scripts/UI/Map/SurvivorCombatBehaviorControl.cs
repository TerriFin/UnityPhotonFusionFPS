using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SimpleFPS
{
	[DisallowMultipleComponent]
	public sealed class SurvivorCombatBehaviorControl : MonoBehaviour
	{
		public Button CycleButton;
		public Image Background;
		public TextMeshProUGUI Label;

		public Color NormalColor = new Color(0.18f, 0.55f, 0.72f, 0.95f);
		public Color AggressiveColor = new Color(0.78f, 0.28f, 0.12f, 0.95f);
		public Color DefensiveColor = new Color(0.2f, 0.58f, 0.32f, 0.95f);
		public Color NoneColor = new Color(0.28f, 0.24f, 0.32f, 0.95f);

		public ESurvivorCombatBehavior Value { get; private set; }
		public event Action<ESurvivorCombatBehavior> ValueChanged;

		private bool _listenerAttached;

		private void Awake()
		{
			EnsureRuntimeControl();
			AttachListener();
			RefreshVisual();
		}

		public void SetValueWithoutNotify(ESurvivorCombatBehavior value)
		{
			Value = Enum.IsDefined(typeof(ESurvivorCombatBehavior), value)
				? value
				: ESurvivorCombatBehavior.Normal;
			RefreshVisual();
		}

		private void HandleClicked()
		{
			Value = Value switch
			{
				ESurvivorCombatBehavior.Normal => ESurvivorCombatBehavior.Aggressive,
				ESurvivorCombatBehavior.Aggressive => ESurvivorCombatBehavior.Defensive,
				ESurvivorCombatBehavior.Defensive => ESurvivorCombatBehavior.None,
				_ => ESurvivorCombatBehavior.Normal,
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
					ESurvivorCombatBehavior.Aggressive => "AGGRO",
					ESurvivorCombatBehavior.Defensive => "DEF",
					ESurvivorCombatBehavior.None => "NONE",
					_ => "NORMAL",
				};
			}

			if (Background != null)
			{
				Background.color = Value switch
				{
					ESurvivorCombatBehavior.Aggressive => AggressiveColor,
					ESurvivorCombatBehavior.Defensive => DefensiveColor,
					ESurvivorCombatBehavior.None => NoneColor,
					_ => NormalColor,
				};
			}
		}
	}
}
