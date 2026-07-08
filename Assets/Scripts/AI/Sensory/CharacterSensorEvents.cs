using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	// Global noise/bullet-impact stimulus fan-out. These fire for every shot and every confirmed bullet impact
	// in the game, so instead of touching every active sensor they query the shared sensor spatial hash with
	// the largest awareness radius any sensor has registered — a strict superset of every sensor that could
	// possibly hear the event. Each sensor still applies its own exact radius/faction/authority checks.
	public static class CharacterSensorEvents
	{
		public static void ReportNoise(Vector3 noisePosition, NetworkObject source, float radius = 0f)
		{
			// A per-event radius only ever *shrinks* a receiver's own awareness radius, so the global max stays
			// a valid query bound regardless of the parameter.
			var results = CharacterSensor.EventQueryBuffer;
			CharacterSensor.QuerySensorsForEvent(noisePosition, CharacterSensor.MaxNoiseAwarenessRadius, results);

			for (int i = results.Count - 1; i >= 0; i--)
			{
				var sensor = results[i];
				if (sensor != null)
					sensor.RecordNoise(noisePosition, source, radius);
			}
		}

		public static void ReportBulletImpact(Vector3 impactPosition, Vector3 approximateShooterPosition, NetworkObject shooter)
		{
			var results = CharacterSensor.EventQueryBuffer;
			CharacterSensor.QuerySensorsForEvent(impactPosition, CharacterSensor.MaxBulletImpactAwarenessRadius, results);

			for (int i = results.Count - 1; i >= 0; i--)
			{
				var sensor = results[i];
				if (sensor != null)
					sensor.RecordBulletImpact(impactPosition, approximateShooterPosition, shooter);
			}
		}
	}
}
