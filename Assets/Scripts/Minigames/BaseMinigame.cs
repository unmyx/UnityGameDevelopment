using UnityEngine;
using Game.Core;

namespace Game.Minigames
{
    /// <summary>
    /// BaseMinigame is an abstract base class for all minigames.
    /// Respects pause state and Time.timeScale. Skips updates while paused.
    /// </summary>
    public abstract class BaseMinigame : MonoBehaviour, IMinigame
    {
        protected MinigameData _minigameData;
        private bool _isActive = false;
        private MinigameResult _result = MinigameResult.None;
        private float _elapsedTime = 0f;

        public virtual void Initialize(MinigameData data)
        {
            if (data == null)
            {
                return;
            }

            _minigameData = data;
            _result = MinigameResult.None;
            _elapsedTime = 0f;

            OnInitialize();
        }

        public virtual void OnMinigameStart()
        {
            _isActive = true;
            _elapsedTime = 0f;
            _result = MinigameResult.None;

            OnStart();
        }

        public virtual void OnMinigameUpdate()
        {
            if (!_isActive)
                return;

            PauseManager pauseManager = PauseManager.Instance;
            if (pauseManager != null && pauseManager.IsPaused)
                return;

            if (_minigameData.timeLimit > 0)
            {
                _elapsedTime += Time.deltaTime;

                if (_elapsedTime >= _minigameData.timeLimit)
                {
                    SetResult(MinigameResult.Timeout);
                    return;
                }
            }

            OnUpdate();
        }

        public virtual void OnMinigameEnd()
        {
            if (!_isActive)
                return;

            _isActive = false;

            OnEnd();
        }
        protected abstract void OnInitialize();
        protected abstract void OnStart();
        protected abstract void OnUpdate();

        protected abstract void OnEnd();

        protected void SetResult(MinigameResult result)
        {
            if (result == MinigameResult.None)
            {
                return;
            }

            _result = result;
            OnMinigameEnd();
        }

        public bool IsActive()
        {
            return _isActive;
        }

        public MinigameResult GetResult()
        {
            return _result;
        }

        public MinigameData GetMinigameData()
        {
            return _minigameData;
        }

        public string GetMinigameId()
        {
            return _minigameData?.minigameId ?? "unknown";
        }

        protected float GetRemainingTime()
        {
            if (_minigameData.timeLimit <= 0)
                return -1f;

            return Mathf.Max(0f, _minigameData.timeLimit - _elapsedTime);
        }

        protected float GetElapsedTime()
        {
            return _elapsedTime;
        }

        protected object GetParameter(string key)
        {
            if (_minigameData.parameters.TryGetValue(key, out object value))
            {
                return value;
            }

            return null;
        }

        protected T GetParameter<T>(string key)
        {
            object value = GetParameter(key);
            if (value is T typedValue)
            {
                return typedValue;
            }
            return default;
        }
    }
}

