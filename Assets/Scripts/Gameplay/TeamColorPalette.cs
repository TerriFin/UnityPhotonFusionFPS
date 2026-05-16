using UnityEngine;

namespace SimpleFPS
{
	[CreateAssetMenu(menuName = "SimpleFPS/Team Color Palette")]
	public sealed class TeamColorPalette : ScriptableObject
	{
		public Material[] TeamMaterials;
		public Material NeutralMaterial;

		public int TeamColorCount => TeamMaterials != null ? TeamMaterials.Length : 0;

		public Material GetMaterial(int teamColorIndex)
		{
			if (teamColorIndex >= 0 && TeamMaterials != null && teamColorIndex < TeamMaterials.Length)
				return TeamMaterials[teamColorIndex];

			return NeutralMaterial;
		}
	}
}
