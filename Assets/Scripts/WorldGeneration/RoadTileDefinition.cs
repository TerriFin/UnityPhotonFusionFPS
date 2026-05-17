using UnityEngine;

namespace SimpleFPS
{
	public class RoadTileDefinition : MonoBehaviour
	{
		public RoadSocket North;
		public RoadSocket East;
		public RoadSocket South;
		public RoadSocket West;
		public RoadEnvironment Environment = RoadEnvironment.Normal;
		public bool IsBoundaryTile;
		public bool IsHeightChangeRamp;
		[Min(1)]
		public int Weight = 1;
		[Min(0)]
		public int RepeatCooldown;

		public bool Matches(RoadSocket north, RoadSocket east, RoadSocket south, RoadSocket west, RoadEnvironment environment, int rotationSteps)
		{
			return Environment == environment
				&& GetRotatedSocket(RoadDirection.North, rotationSteps) == north
				&& GetRotatedSocket(RoadDirection.East, rotationSteps) == east
				&& GetRotatedSocket(RoadDirection.South, rotationSteps) == south
				&& GetRotatedSocket(RoadDirection.West, rotationSteps) == west;
		}

		private RoadSocket GetRotatedSocket(RoadDirection direction, int rotationSteps)
		{
			int sourceDirection = ((int)direction - rotationSteps) % 4;
			if (sourceDirection < 0)
				sourceDirection += 4;

			return GetSocket((RoadDirection)sourceDirection);
		}

		private RoadSocket GetSocket(RoadDirection direction)
		{
			return direction switch
			{
				RoadDirection.North => North,
				RoadDirection.East => East,
				RoadDirection.South => South,
				RoadDirection.West => West,
				_ => RoadSocket.Closed,
			};
		}
	}
}
