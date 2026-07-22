using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Game.Core
{
    public static class DevelopmentDiagnostics
    {
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        public static void Log(object message)
        {
            UnityEngine.Debug.Log(message);
        }

        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        public static void Log(object message, Object context)
        {
            UnityEngine.Debug.Log(message, context);
        }

        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        public static void DrawRay(Vector3 start, Vector3 direction, Color color)
        {
            UnityEngine.Debug.DrawRay(start, direction, color);
        }
    }

    public sealed class WarningOnceGate
    {
        private readonly HashSet<string> _reportedCauses = new HashSet<string>();

        public bool ShouldReport(string cause)
        {
            return !string.IsNullOrWhiteSpace(cause) && _reportedCauses.Add(cause);
        }

        public void Reset(string cause)
        {
            if (!string.IsNullOrWhiteSpace(cause))
            {
                _reportedCauses.Remove(cause);
            }
        }

        public void Clear()
        {
            _reportedCauses.Clear();
        }
    }
}
