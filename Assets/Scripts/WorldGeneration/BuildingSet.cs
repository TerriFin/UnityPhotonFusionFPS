using UnityEngine;

namespace SimpleFPS
{
	[CreateAssetMenu(menuName = "SimpleFPS/World Generation/Building Set")]
	public class BuildingSet : ScriptableObject
	{
		public BuildingDefinition[] Buildings;
	}
}
