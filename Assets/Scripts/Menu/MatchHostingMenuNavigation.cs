using Fusion.Menu;
using UnityEngine;

namespace SimpleFPS
{
	public sealed class MatchHostingMenuNavigation : MonoBehaviour
	{
		public MenuUIController UIController;

		public void OnHostGameButtonPressed()
		{
			if (UIController == null)
			{
				Debug.LogError($"{nameof(MatchHostingMenuNavigation)} on {name} has no menu UI controller.", this);
				return;
			}

			UIController.Show<MatchHostingMenuController>();
		}
	}
}
