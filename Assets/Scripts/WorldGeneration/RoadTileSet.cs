using UnityEngine;

namespace SimpleFPS
{
	[CreateAssetMenu(menuName = "SimpleFPS/World Generation/Road Tile Set")]
	public class RoadTileSet : ScriptableObject
	{
		public RoadTileDefinition[] Tiles;
	}
}
