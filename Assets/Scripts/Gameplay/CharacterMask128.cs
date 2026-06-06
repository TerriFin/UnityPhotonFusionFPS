namespace SimpleFPS
{
	public struct CharacterMask128
	{
		public const int Capacity = 128;

		public int Mask0;
		public int Mask1;
		public int Mask2;
		public int Mask3;

		public bool IsEmpty => Mask0 == 0 && Mask1 == 0 && Mask2 == 0 && Mask3 == 0;

		public CharacterMask128(int mask0, int mask1, int mask2, int mask3)
		{
			Mask0 = mask0;
			Mask1 = mask1;
			Mask2 = mask2;
			Mask3 = mask3;
		}

		public bool Contains(int index)
		{
			if (index < 0 || index >= Capacity)
				return false;

			int bit = 1 << (index & 31);
			return (GetSegment(index >> 5) & bit) != 0;
		}

		public void Set(int index, bool value)
		{
			if (index < 0 || index >= Capacity)
				return;

			int segment = index >> 5;
			int bit = 1 << (index & 31);
			int mask = GetSegment(segment);
			mask = value ? mask | bit : mask & ~bit;
			SetSegment(segment, mask);
		}

		public void Clear()
		{
			Mask0 = 0;
			Mask1 = 0;
			Mask2 = 0;
			Mask3 = 0;
		}

		public static CharacterMask128 FirstBits(int count)
		{
			count = UnityEngine.Mathf.Clamp(count, 0, Capacity);
			var mask = new CharacterMask128();
			for (int i = 0; i < count; i++)
			{
				mask.Set(i, true);
			}

			return mask;
		}

		private int GetSegment(int segment)
		{
			return segment switch
			{
				0 => Mask0,
				1 => Mask1,
				2 => Mask2,
				3 => Mask3,
				_ => 0,
			};
		}

		private void SetSegment(int segment, int value)
		{
			switch (segment)
			{
				case 0:
					Mask0 = value;
					break;
				case 1:
					Mask1 = value;
					break;
				case 2:
					Mask2 = value;
					break;
				case 3:
					Mask3 = value;
					break;
			}
		}
	}
}
