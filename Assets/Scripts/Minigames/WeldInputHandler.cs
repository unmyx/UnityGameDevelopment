using Game.Input;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Minigames
{
    internal sealed class WeldInputHandler
    {
        private InputAction _paintAction;

        public void CachePaintAction()
        {
            _paintAction = null;

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
        }

        public bool IsPaintHeld()
        {
            return _paintAction != null && _paintAction.IsPressed();
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
        }
    }
}
