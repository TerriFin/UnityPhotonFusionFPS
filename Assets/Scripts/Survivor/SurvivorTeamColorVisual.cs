using UnityEngine;

namespace SimpleFPS
{
	public sealed class SurvivorTeamColorVisual : MonoBehaviour
	{
		public Renderer[] TeamColorRenderers;
		public int MaterialSlot;

		private Survivor _survivor;
		private SceneObjects _sceneObjects;
		private int _lastAppliedIndex = int.MinValue;
		private Material _lastAppliedMaterial;

		private void Awake()
		{
			_survivor = GetComponentInParent<Survivor>();
		}

		private void Update()
		{
			if (_survivor == null)
				return;
			if (_survivor.Object == null || _survivor.Object.IsValid == false)
				return;

			var gameplay = GetGameplay();
			if (gameplay == null)
				return;

			int teamColorIndex = Gameplay.NeutralTeamColorIndex;
			PlayerData playerData = default;
			if (gameplay.PlayerData.TryGet(_survivor.OwnerRef, out playerData))
			{
				teamColorIndex = playerData.TeamColorIndex;
			}

			Material material = gameplay.GetTeamColorMaterial(teamColorIndex);
			if (material == null)
				return;
			if (_lastAppliedIndex == teamColorIndex && _lastAppliedMaterial == material)
				return;

			ApplyMaterial(material);
			_lastAppliedIndex = teamColorIndex;
			_lastAppliedMaterial = material;
		}

		private Gameplay GetGameplay()
		{
			if (_sceneObjects == null && _survivor.Runner != null)
			{
				_sceneObjects = _survivor.Runner.GetSingleton<SceneObjects>();
			}

			return _sceneObjects != null ? _sceneObjects.Gameplay : null;
		}

		private void ApplyMaterial(Material material)
		{
			if (TeamColorRenderers == null)
				return;

			for (int i = 0; i < TeamColorRenderers.Length; i++)
			{
				var renderer = TeamColorRenderers[i];
				if (renderer == null)
					continue;

				var materials = renderer.sharedMaterials;
				if (materials == null || materials.Length == 0)
					continue;

				int slot = Mathf.Clamp(MaterialSlot, 0, materials.Length - 1);
				if (materials[slot] == material)
					continue;

				materials[slot] = material;
				renderer.sharedMaterials = materials;
			}
		}
	}
}
