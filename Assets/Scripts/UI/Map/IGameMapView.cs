using UnityEngine;
using UnityEngine.UI;

namespace SimpleFPS
{
	/// <summary>
	/// Minimal contract a map surface needs to expose so <see cref="GameMapIconController"/> can lay
	/// out icons on top of it. The main full-screen map and the always-on minimap both implement this.
	/// Selection / order issuing intentionally lives outside this interface — those features are only
	/// meaningful on the full map.
	/// </summary>
	public interface IGameMapView
	{
		RawImage GetMapImage();
		Vector2 WorldToMapUI(Vector3 worldPosition);
		bool IsWorldPositionVisibleOnMap(Vector3 worldPosition);
	}
}
