namespace Game.Core
{
    internal static class GameManagerInstanceLocator
    {
        public static GameManager Resolve(GameManager currentInstance)
        {
            return currentInstance;
        }
    }
}
