using UnityEngine;

namespace SimpleFPS
{
	[CreateAssetMenu(menuName = "SimpleFPS/World Generation/Height Tile Set")]
	public class HeightTileSet : ScriptableObject
	{
		public HeightTileDefinition[] Tiles;
	}
}
