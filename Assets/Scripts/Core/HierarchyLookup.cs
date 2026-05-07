using UnityEngine;

namespace Game.Core
{
    public static class HierarchyLookup
    {
        public static bool TryFindChild(
            Transform parent,
            string childPath,
            out Transform child,
            Object context,
            string contractName)
        {
            child = null;
            if (parent == null || string.IsNullOrWhiteSpace(childPath))
            {
                return false;
            }

            child = parent.Find(childPath);
            if (child != null)
            {
                return true;
            }

            Debug.LogWarning(
                $"[HierarchyLookup] Missing child contract '{contractName}' at path '{childPath}' under '{parent.name}'.",
                context);
            return false;
        }
    }
}
