namespace GameFrame.Audio
{
    /// <summary>业务音效 ID，需在业务的 AudioRuntimeConfig 中配置对应条目。</summary>
    public static class GameAudioIds
    {
        public static readonly AudioId LudoBgm = new("bgm.ludo");
        public static readonly AudioId Dice = new("sfx.dice");
        public static readonly AudioId DiceAlternate = new("sfx.dice.alt");
        public static readonly AudioId Eat = new("sfx.eat");
        public static readonly AudioId MoveOne = new("sfx.move.1");
        public static readonly AudioId MoveTwo = new("sfx.move.2");
    }
}
