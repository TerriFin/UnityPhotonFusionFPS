using System;
using UnityEngine;

namespace SimpleFPS
{
	/// <summary>
	/// Photon's LoadBalancingClient logs an Error every time the server sends a disconnect message
	/// (twice — once for the message, once for the resulting peer state change). The application
	/// already handles disconnects gracefully via MenuConnectionCallbacks.OnShutdown, so these
	/// lines are pure noise. Fusion routes every DebugLevel.ERROR through a single shared
	/// UnityLogStream, so the only place to silence them is Unity's log handler.
	/// </summary>
	public static class FusionDisconnectLogFilter
	{
		private const string SuppressionFragment = "DisconnectMessage. Code:";

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void Install()
		{
			// Unity restores the default handler between editor play/stop cycles without a domain
			// reload, so check the live handler rather than relying on a static "installed" flag.
			if (Debug.unityLogger.logHandler is FilteringLogHandler)
				return;

			Debug.unityLogger.logHandler = new FilteringLogHandler(Debug.unityLogger.logHandler);
		}

		private sealed class FilteringLogHandler : ILogHandler
		{
			private readonly ILogHandler _inner;

			public FilteringLogHandler(ILogHandler inner)
			{
				_inner = inner;
			}

			public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
			{
				if (ShouldSuppress(format, args))
					return;

				_inner.LogFormat(logType, context, format, args);
			}

			public void LogException(Exception exception, UnityEngine.Object context)
			{
				_inner.LogException(exception, context);
			}

			private static bool ShouldSuppress(string format, object[] args)
			{
				if (string.IsNullOrEmpty(format) == false && format.IndexOf(SuppressionFragment, StringComparison.Ordinal) >= 0)
					return true;

				if (args == null)
					return false;

				for (int i = 0; i < args.Length; i++)
				{
					if (args[i] is string s && s.IndexOf(SuppressionFragment, StringComparison.Ordinal) >= 0)
						return true;
				}

				return false;
			}
		}
	}
}
