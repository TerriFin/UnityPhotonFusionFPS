namespace SimpleFPS
{
	/// <summary>
	/// Carries the disconnect reason across the Startup scene reload that
	/// MenuConnectionCallbacks.OnShutdown uses to recover from a non-graceful disconnect.
	/// </summary>
	public static class PendingDisconnectMessage
	{
		private static string _message;

		public static void Set(string message)
		{
			_message = message;
		}

		public static bool TryConsume(out string message)
		{
			message = _message;
			_message = null;
			return string.IsNullOrEmpty(message) == false;
		}
	}
}
