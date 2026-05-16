using Fusion;
using UnityEngine;

namespace SimpleFPS
{
	public static class CharacterSensorEvents
	{
		public static void ReportNoise(Vector3 noisePosition, NetworkObject source, float radius = 0f)
		{
			for (int i = CharacterSensor.ActiveSensors.Count - 1; i >= 0; i--)
			{
				var sensor = CharacterSensor.ActiveSensors[i];
				if (sensor == null)
				{
					CharacterSensor.ActiveSensors.RemoveAt(i);
					continue;
				}

				sensor.RecordNoise(noisePosition, source, radius);
			}
		}

		public static void ReportBulletImpact(Vector3 impactPosition, Vector3 approximateShooterPosition, NetworkObject shooter)
		{
			for (int i = CharacterSensor.ActiveSensors.Count - 1; i >= 0; i--)
			{
				var sensor = CharacterSensor.ActiveSensors[i];
				if (sensor == null)
				{
					CharacterSensor.ActiveSensors.RemoveAt(i);
					continue;
				}

				sensor.RecordBulletImpact(impactPosition, approximateShooterPosition, shooter);
			}
		}
	}
}
