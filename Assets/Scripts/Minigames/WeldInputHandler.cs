using Game.Input;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Minigames
{
    internal sealed class WeldInputHandler
    {
        private InputAction _paintAction;
        private InputAction _radiusScrollAction;

        public void CacheActions()
        {
            _paintAction = null;
            _radiusScrollAction = null;

            if (InputManager.Instance == null)
            {
                return;
            }

            InputActionAsset inputAsset = InputManager.Instance.GetInputActionAsset();
            if (inputAsset == null)
            {
                return;
            }

            InputActionMap playerMap = inputAsset.FindActionMap("Player");
            if (playerMap == null)
            {
                return;
            }

            _paintAction = playerMap.FindAction("Attack");

            InputActionMap uiMap = inputAsset.FindActionMap("UI");
            if (uiMap != null)
            {
                _radiusScrollAction = uiMap.FindAction("ScrollWheel");
            }
        }

        public bool IsPaintHeld()
        {
            return _paintAction != null && _paintAction.IsPressed();
        }

        public float GetRadiusScrollDeltaY()
        {
            if (_radiusScrollAction == null)
            {
                return 0f;
            }

            return _radiusScrollAction.ReadValue<Vector2>().y;
        }

        public bool TryGetLocalPointerPosition(RectTransform areaRect, Camera uiCamera, out Vector2 localPoint)
        {
            localPoint = Vector2.zero;
            if (areaRect == null)
            {
                return false;
            }

            Vector2 screenMousePos = UnityEngine.Input.mousePosition;
            return RectTransformUtility.ScreenPointToLocalPointInRectangle(areaRect, screenMousePos, uiCamera, out localPoint);
        }

        public bool IsPointerInside(RectTransform areaRect, Camera uiCamera)
        {
            if (areaRect == null)
            {
                return false;
            }

            return RectTransformUtility.RectangleContainsScreenPoint(areaRect, UnityEngine.Input.mousePosition, uiCamera);
        }

        public void Clear()
        {
            _paintAction = null;
            _radiusScrollAction = null;
        }
    }
}
