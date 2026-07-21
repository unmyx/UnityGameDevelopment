namespace Game.Input
{
    /// <summary>
    /// One-shot, frame-scoped jump press latch. Duplicate callbacks in the same frame
    /// collapse into one request and unconsumed requests expire on the next frame.
    /// </summary>
    public sealed class JumpPressLatch
    {
        private int _pendingFrame = -1;
        private bool _isPending;

        public bool RegisterPress(int frame)
        {
            if (_isPending && _pendingFrame == frame)
            {
                return false;
            }

            _pendingFrame = frame;
            _isPending = true;
            return true;
        }

        public bool TryConsume(int frame)
        {
            ExpireBefore(frame);
            if (!_isPending || _pendingFrame != frame)
            {
                return false;
            }

            Clear();
            return true;
        }

        public bool HasPendingPress(int frame)
        {
            ExpireBefore(frame);
            return _isPending && _pendingFrame == frame;
        }

        public void ExpireBefore(int frame)
        {
            if (_isPending && _pendingFrame < frame)
            {
                Clear();
            }
        }

        public void Clear()
        {
            _pendingFrame = -1;
            _isPending = false;
        }
    }
}
