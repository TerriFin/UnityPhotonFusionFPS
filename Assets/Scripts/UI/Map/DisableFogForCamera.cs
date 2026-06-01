using UnityEngine;
using UnityEngine.Rendering;

namespace SimpleFPS
{
	/// <summary>
	/// URP has no per-camera fog toggle. This component turns RenderSettings.fog off for the
	/// duration of one camera's render and restores it afterwards, so an overhead minimap
	/// camera can render the map without the scene's atmospheric fog covering it.
	/// </summary>
	[RequireComponent(typeof(Camera))]
	public sealed class DisableFogForCamera : MonoBehaviour
	{
		private Camera _camera;
		private bool _previousFog;
		private bool _fogOverridden;

		private void Awake()
		{
			_camera = GetComponent<Camera>();
		}

		private void OnEnable()
		{
			RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
			RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
		}

		private void OnDisable()
		{
			RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
			RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;

			if (_fogOverridden)
			{
				RenderSettings.fog = _previousFog;
				_fogOverridden = false;
			}
		}

		private void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera camera)
		{
			if (camera != _camera)
				return;

			_previousFog = RenderSettings.fog;
			RenderSettings.fog = false;
			_fogOverridden = true;
		}

		private void OnEndCameraRendering(ScriptableRenderContext ctx, Camera camera)
		{
			if (camera != _camera || _fogOverridden == false)
				return;

			RenderSettings.fog = _previousFog;
			_fogOverridden = false;
		}
	}
}
