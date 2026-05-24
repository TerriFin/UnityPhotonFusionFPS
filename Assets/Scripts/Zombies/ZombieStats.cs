using System;

namespace SimpleFPS
{
	[Serializable]
	public struct ZombieStats
	{
		public float MaxHealth;
		public float Damage;
		public float MoveSpeed;
		public float AlertRadius;
		public float AttackRange;
		public float AttackCooldown;
	}
}
