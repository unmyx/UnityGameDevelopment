using System.Collections.Generic;

namespace Game.Interaction
{
    /// <summary>
    /// Collects the required scene-owned references for a scene-bound interactable.
    /// </summary>
    public sealed class SceneBindingValidation
    {
        private readonly List<string> _failures = new List<string>(8);

        public void Require(bool condition, string failureReason)
        {
            if (!condition && !string.IsNullOrWhiteSpace(failureReason))
            {
                _failures.Add(failureReason);
            }
        }

        public bool Complete(out string failureReason)
        {
            if (_failures.Count == 0)
            {
                failureReason = string.Empty;
                return true;
            }

            failureReason = string.Join("; ", _failures);
            return false;
        }
    }
}
