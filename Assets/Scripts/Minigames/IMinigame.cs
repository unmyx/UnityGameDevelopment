namespace Game.Minigames
{
    /// <summary>
    /// IMinigame defines the contract for all minigames in the system.
    /// Every minigame must implement this interface.
    /// 
    /// Design:
    /// - Lifecycle methods: Initialize, Start, Update, End
    /// - Result tracking: Pass/Fail/Timeout
    /// - Data-driven: Each minigame has associated MinigameData
    /// 
    /// Responsibilities:
    /// - Initialize: Set up minigame state/UI
    /// - OnMinigameStart: Called when minigame enters active state
    /// - OnMinigameEnd: Called when minigame exits (any reason)
    /// - Provide current result (Pass/Fail)
    /// 
    /// Usage:
    /// Create a subclass: public class PuzzleMinigame : BaseMinigame { ... }
    /// Then instantiate via MinigameManager.StartMinigame<PuzzleMinigame>(data)
    /// </summary>
    public interface IMinigame
    {
        void Initialize(MinigameData data);
        void OnMinigameStart();
        void OnMinigameUpdate();
        void OnMinigameEnd();
        bool IsActive();
        MinigameResult GetResult();
        MinigameData GetMinigameData();
        string GetMinigameId();
    }

    public enum MinigameResult
    {
        None,
        Pass,
        Fail,
        Timeout,
        Cancelled
    }

    public class MinigameData
    {
        public string minigameId = "minigame_default";
        public string displayName = "Minigame";
        public float timeLimit = 0f;

          public int difficulty = 5;
        public System.Collections.Generic.Dictionary<string, object> parameters = new();
        public void SetParameter(string key, object value)
        {
            parameters[key] = value;
        }

        public object GetParameter(string key)
        {
            if (parameters.TryGetValue(key, out object value))
            {
                return value;
            }
            return null;
        }

        public override string ToString()
        {
            return $"[{minigameId}] {displayName} (Difficulty: {difficulty})";
        }
    }
}
