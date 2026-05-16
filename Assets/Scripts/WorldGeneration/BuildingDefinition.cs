using UnityEngine;

namespace SimpleFPS
{
	public class BuildingDefinition : MonoBehaviour
	{
		public BuildingCategory Category = BuildingCategory.Complex;
		public Vector2Int FootprintSize = Vector2Int.one;
		public RoadEnvironment Environment = RoadEnvironment.Normal;
		public BuildingSideRequirement North;
		public BuildingSideRequirement East;
		public BuildingSideRequirement South;
		public BuildingSideRequirement West;
		[Min(1)]
		public int Weight = 1;
		[Min(0)]
		public int RepeatCooldownDistance;

		public Vector2Int GetRotatedFootprintSize(int rotationSteps)
		{
			Vector2Int size = new Vector2Int(Mathf.Max(1, FootprintSize.x), Mathf.Max(1, FootprintSize.y));
			return rotationSteps % 2 == 0 ? size : new Vector2Int(size.y, size.x);
		}

		public BuildingSideRequirement GetRotatedRequirement(RoadDirection direction, int rotationSteps)
		{
			int sourceDirection = ((int)direction - rotationSteps) % 4;
			if (sourceDirection < 0)
				sourceDirection += 4;

			return GetRequirement((RoadDirection)sourceDirection);
		}

		private BuildingSideRequirement GetRequirement(RoadDirection direction)
		{
			return direction switch
			{
				RoadDirection.North => North,
				RoadDirection.East => East,
				RoadDirection.South => South,
				RoadDirection.West => West,
				_ => BuildingSideRequirement.Any,
			};
		}
	}
}
