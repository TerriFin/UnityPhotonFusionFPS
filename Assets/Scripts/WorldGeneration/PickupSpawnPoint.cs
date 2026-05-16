using UnityEngine;

namespace SimpleFPS
{
	public class PickupSpawnPoint : MonoBehaviour
	{
		public enum PickupSpawnCategory
		{
			Weapon,
			Health,
		}

		public PickupSpawnCategory Category;
	}
}
