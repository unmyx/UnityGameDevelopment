using UnityEngine;

namespace Game.Player
{
    public enum LocalPlayerPresentationMode
    {
        Unknown = 0,
        FreePlay = 1,
        Paused = 2,
        Minigame = 3,
        Menu = 4
    }

    /// <summary>
    /// Local runtime-only presentation ownership seam.
    /// Coordinates cursor authority and owner-scoped presentation mode without changing gameplay rules.
    /// </summary>
    public sealed class LocalPlayerPresentationState
    {
        public LocalPlayerPresentationMode Mode { get; private set; } = LocalPlayerPresentationMode.FreePlay;
        public string ActiveMinigameId { get; private set; } = string.Empty;
        public string ActiveMinigameOwnerPlayerId { get; private set; } = string.Empty;
        public bool IsGameplayCameraSuppressed { get; private set; }

        public string CursorAuthorityOwner { get; private set; } = string.Empty;
        public CursorLockMode CursorLockMode { get; private set; } = CursorLockMode.Locked;
        public bool CursorVisible { get; private set; }

        private bool _hasCursorSnapshot;
        private CursorLockMode _cursorSnapshotLockMode = CursorLockMode.Locked;
        private bool _cursorSnapshotVisible;

        public void SetLocalMode(LocalPlayerPresentationMode mode)
        {
            Mode = mode;
        }

        public void BeginMinigamePresentation(string minigameId, string ownerPlayerId)
        {
            Mode = LocalPlayerPresentationMode.Minigame;
            ActiveMinigameId = string.IsNullOrWhiteSpace(minigameId) ? string.Empty : minigameId.Trim();
            ActiveMinigameOwnerPlayerId = string.IsNullOrWhiteSpace(ownerPlayerId) ? string.Empty : ownerPlayerId.Trim();
        }

        public void EndMinigamePresentation()
        {
            ActiveMinigameId = string.Empty;
            ActiveMinigameOwnerPlayerId = string.Empty;
            if (Mode == LocalPlayerPresentationMode.Minigame)
            {
                Mode = LocalPlayerPresentationMode.FreePlay;
            }
        }

        public void SetGameplayCameraSuppressed(bool suppressed)
        {
            IsGameplayCameraSuppressed = suppressed;
        }

        public bool CanAcquireCursorAuthority(string authorityOwner)
        {
            string normalizedOwner = NormalizeOwner(authorityOwner);
            if (string.IsNullOrEmpty(normalizedOwner))
            {
                return false;
            }

            if (string.IsNullOrEmpty(CursorAuthorityOwner))
            {
                return true;
            }

            return string.Equals(CursorAuthorityOwner, normalizedOwner, System.StringComparison.Ordinal);
        }

        public bool TryAcquireCursorAuthority(string authorityOwner, CursorLockMode lockMode, bool visible)
        {
            string normalizedOwner = NormalizeOwner(authorityOwner);
            if (!CanAcquireCursorAuthority(normalizedOwner))
            {
                return false;
            }

            if (!_hasCursorSnapshot)
            {
                _cursorSnapshotLockMode = Cursor.lockState;
                _cursorSnapshotVisible = Cursor.visible;
                _hasCursorSnapshot = true;
            }

            CursorAuthorityOwner = normalizedOwner;
            CursorLockMode = lockMode;
            CursorVisible = visible;

            Cursor.lockState = lockMode;
            Cursor.visible = visible;
            return true;
        }

        public bool TryReleaseCursorAuthority(string authorityOwner)
        {
            string normalizedOwner = NormalizeOwner(authorityOwner);
            if (string.IsNullOrEmpty(CursorAuthorityOwner)
                || !string.Equals(CursorAuthorityOwner, normalizedOwner, System.StringComparison.Ordinal))
            {
                return false;
            }

            CursorAuthorityOwner = string.Empty;

            if (_hasCursorSnapshot)
            {
                Cursor.lockState = _cursorSnapshotLockMode;
                Cursor.visible = _cursorSnapshotVisible;
                CursorLockMode = _cursorSnapshotLockMode;
                CursorVisible = _cursorSnapshotVisible;
            }

            _hasCursorSnapshot = false;
            return true;
        }

        private static string NormalizeOwner(string authorityOwner)
        {
            return string.IsNullOrWhiteSpace(authorityOwner) ? string.Empty : authorityOwner.Trim();
        }
    }
}
