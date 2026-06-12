using UnityEngine;
using UnityEngine.UI;

namespace SimpleFPS
{
	public enum GameMapIconKind
	{
		OwnSurvivor,
		EnemySurvivor,
		NeutralSurvivor,
		Pickup,
		Zombie,
	}

	public sealed class GameMapIcon : MonoBehaviour
	{
		public Image Image;
		public RectTransform RectTransform;
		public Survivor Survivor { get; private set; }
		public Fusion.NetworkObject NetworkObject { get; private set; }
		public GameMapIconKind Kind { get; private set; }
		public bool IsSelected { get; private set; }

		private Color _baseColor;
		private float _opacity = 1f;

		public void Initialize(Survivor survivor, GameMapIconKind kind, Color color)
		{
			Survivor = survivor;
			NetworkObject = survivor != null ? survivor.Object : null;
			Kind = kind;
			_baseColor = color;
			_opacity = 1f;

			if (RectTransform == null)
				RectTransform = transform as RectTransform;
			if (Image == null)
				Image = GetComponent<Image>();

			gameObject.name = survivor != null ? $"{kind} Icon {survivor.CharacterIndex}" : $"{kind} Icon";
			SetSelected(false);
		}

		public void Initialize(Fusion.NetworkObject networkObject, GameMapIconKind kind, Color color)
		{
			Survivor = null;
			NetworkObject = networkObject;
			Kind = kind;
			_baseColor = color;
			_opacity = 1f;

			if (RectTransform == null)
				RectTransform = transform as RectTransform;
			if (Image == null)
				Image = GetComponent<Image>();

			gameObject.name = networkObject != null ? $"{kind} Icon {networkObject.Id}" : $"{kind} Icon";
			SetSelected(false);
		}

		public void SetColor(Color color)
		{
			_baseColor = color;
			RefreshColor();
		}

		// Updates both the kind and the base color. Used when a revealed neutral survivor is recruited mid-session:
		// its OwnerRef changes, so it must flip from the neutral appearance to its new owner's team appearance.
		public void SetSurvivorAppearance(GameMapIconKind kind, Color color)
		{
			Kind = kind;
			_baseColor = color;
			RefreshColor();
		}

		public void SetOpacity(float opacity)
		{
			_opacity = Mathf.Clamp01(opacity);
			RefreshColor();
		}

		public void SetMapPosition(Vector2 anchoredPosition)
		{
			if (RectTransform == null)
				RectTransform = transform as RectTransform;

			RectTransform.anchoredPosition = anchoredPosition;
		}

		public void SetRotation(float yawDegrees)
		{
			if (RectTransform == null)
				RectTransform = transform as RectTransform;

			RectTransform.localRotation = Quaternion.Euler(0f, 0f, -yawDegrees);
		}

		public void SetActiveSurvivor(bool active)
		{
			if (RectTransform == null)
				RectTransform = transform as RectTransform;

			RectTransform.localScale = active ? Vector3.one * 1.35f : Vector3.one;
		}

		public void SetSelected(bool selected)
		{
			IsSelected = selected;
			RefreshColor();
		}

		private void RefreshColor()
		{
			if (Image == null)
				return;

			Color color;
			if (IsSelected)
				color = Color.Lerp(_baseColor, Color.white, 0.45f);
			else if (Kind == GameMapIconKind.EnemySurvivor)
				color = Color.Lerp(_baseColor, Color.red, 0.35f);
			else
				color = _baseColor;

			color.a *= _opacity;
			Image.color = color;
		}

		public bool ContainsScreenPoint(Vector2 screenPosition, Camera eventCamera)
		{
			if (RectTransform == null)
				RectTransform = transform as RectTransform;

			return RectTransformUtility.RectangleContainsScreenPoint(RectTransform, screenPosition, eventCamera);
		}
	}
}
