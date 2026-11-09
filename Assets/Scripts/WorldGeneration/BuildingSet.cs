using System.Collections.Generic;
using UnityEngine;

namespace SimpleFPS
{
	[CreateAssetMenu(menuName = "SimpleFPS/World Generation/Building Set")]
	public class BuildingSet : ScriptableObject
	{
		[Tooltip("Project folders (with subfolders) the editor fills Buildings from: every generated .building and building prefab in them. " +
			"A prefab made from a .building replaces it. Empty: Buildings is edited by hand.")]
		public string[] Folders;
		public BuildingDefinition[] Buildings;

		public struct TypeSettings
		{
			public string Name;
			public int Weight;
			public int RepeatCooldownDistance;
		}

		/// <summary>Shared settings per BuildingDefinition.TypeKey: the largest weight and cooldown among the type's members.</summary>
		public Dictionary<string, TypeSettings> GetTypeSettings()
		{
			var result = new Dictionary<string, TypeSettings>();
			if (Buildings == null)
				return result;

			for (int i = 0; i < Buildings.Length; i++)
			{
				BuildingDefinition definition = Buildings[i];
				string key = definition != null ? definition.TypeKey : null;
				if (key == null)
					continue;

				int weight = Mathf.Max(1, definition.Weight);
				int cooldown = Mathf.Max(0, definition.RepeatCooldownDistance);
				if (result.TryGetValue(key, out TypeSettings existing))
				{
					weight = Mathf.Max(weight, existing.Weight);
					cooldown = Mathf.Max(cooldown, existing.RepeatCooldownDistance);
				}

				result[key] = new TypeSettings { Name = definition.BuildingType.Trim(), Weight = weight, RepeatCooldownDistance = cooldown };
			}

			return result;
		}

		/// <summary>One line per type whose members disagree on weight or cooldown.</summary>
		public List<string> GetTypeMismatches()
		{
			var result = new List<string>();
			Dictionary<string, TypeSettings> shared = GetTypeSettings();
			foreach (KeyValuePair<string, TypeSettings> type in shared)
			{
				var members = new List<string>();
				bool mismatch = false;
				for (int i = 0; i < Buildings.Length; i++)
				{
					BuildingDefinition definition = Buildings[i];
					if (definition == null || definition.TypeKey != type.Key)
						continue;

					members.Add($"{definition.name} ({Mathf.Max(1, definition.Weight)}/{Mathf.Max(0, definition.RepeatCooldownDistance)})");
					mismatch |= Mathf.Max(1, definition.Weight) != type.Value.Weight || Mathf.Max(0, definition.RepeatCooldownDistance) != type.Value.RepeatCooldownDistance;
				}

				if (mismatch)
					result.Add($"{type.Value.Name}: weight/cooldown differ between {string.Join(", ", members)}; using {type.Value.Weight}/{type.Value.RepeatCooldownDistance}.");
			}

			return result;
		}
	}
}
