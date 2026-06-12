using UnityEngine;
using UnityEngine.UI;

namespace SimpleFPS
{
	[ExecuteAlways]
	[RequireComponent(typeof(RectTransform))]
	public sealed class ResponsiveRosterGrid : MonoBehaviour
	{
		public RectTransform Viewport;
		public GridLayoutGroup Grid;
		public int MaxColumns = 2;
		public float MinCardWidth = 220f;
		public float CardHeight = 92f;
		public Vector2 Spacing = new Vector2(6f, 6f);

		private RectTransform _rectTransform;
		private int _lastChildCount = -1;
		private Vector2 _lastViewportSize;

		public int CurrentColumns { get; private set; } = 1;

		private void Awake()
		{
			EnsureReferences();
			Rebuild();
		}

		private void OnEnable()
		{
			EnsureReferences();
			Rebuild();
		}

		private void Update()
		{
			if (Viewport == null)
				return;

			Vector2 size = Viewport.rect.size;
			if (_lastChildCount != transform.childCount || (size - _lastViewportSize).sqrMagnitude > 0.01f)
				Rebuild();
		}

		private void OnRectTransformDimensionsChange()
		{
			Rebuild();
		}

		public void Rebuild()
		{
			EnsureReferences();
			if (_rectTransform == null || Grid == null || Viewport == null)
				return;

			_rectTransform.anchorMin = new Vector2(0f, 1f);
			_rectTransform.anchorMax = new Vector2(1f, 1f);
			_rectTransform.pivot = new Vector2(0.5f, 1f);

			Grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
			Grid.startAxis = GridLayoutGroup.Axis.Horizontal;
			Grid.childAlignment = TextAnchor.UpperLeft;
			Grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
			Grid.spacing = Spacing;

			float width = Mathf.Max(1f, Viewport.rect.width);
			float horizontalPadding = Grid.padding.left + Grid.padding.right;
			float available = Mathf.Max(1f, width - horizontalPadding);
			int columns = Mathf.FloorToInt((available + Spacing.x) / Mathf.Max(1f, MinCardWidth + Spacing.x));
			columns = Mathf.Clamp(columns, 1, Mathf.Max(1, MaxColumns));

			float cellWidth = (available - Spacing.x * (columns - 1)) / columns;
			Grid.constraintCount = columns;
			Grid.cellSize = new Vector2(Mathf.Max(1f, cellWidth), Mathf.Max(1f, CardHeight));
			CurrentColumns = columns;

			int childCount = transform.childCount;
			int rows = childCount > 0 ? Mathf.CeilToInt(childCount / (float)columns) : 0;
			float height = Grid.padding.top + Grid.padding.bottom;
			if (rows > 0)
				height += rows * Grid.cellSize.y + (rows - 1) * Spacing.y;

			Vector2 size = _rectTransform.sizeDelta;
			size.y = Mathf.Max(height, Viewport.rect.height);
			_rectTransform.sizeDelta = size;

			_lastChildCount = childCount;
			_lastViewportSize = Viewport.rect.size;
		}

		private void EnsureReferences()
		{
			if (_rectTransform == null)
				_rectTransform = transform as RectTransform;
			if (Grid == null)
				Grid = GetComponent<GridLayoutGroup>();
		}
	}
}
