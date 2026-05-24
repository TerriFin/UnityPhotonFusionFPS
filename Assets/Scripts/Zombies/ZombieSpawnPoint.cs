using UnityEngine;

namespace SimpleFPS
{
	public sealed class ZombieSpawnPoint : MonoBehaviour
	{
		public int MaxSpawnCountPerPulse = 4;
		public float NonForcedSurvivorBlockRadius = 12f;

		public bool IsForced => NonForcedSurvivorBlockRadius <= 0f;

		private void OnValidate()
		{
			MaxSpawnCountPerPulse = Mathf.Max(1, MaxSpawnCountPerPulse);
			NonForcedSurvivorBlockRadius = Mathf.Max(0f, NonForcedSurvivorBlockRadius);
		}
	}
}
