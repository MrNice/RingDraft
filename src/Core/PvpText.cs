namespace RingDraft.Core;

/// <summary>
/// The words on PvP end screens:
/// <list type="bullet">
/// <item>last one standing: the game's own Victory banner and a WinQuote;</item>
/// <item>fell together on the last turn, nobody left: TieBanner and a TieQuote;</item>
/// <item>fell on an earlier turn, then the rest tied: the game's own Defeat banner and a NearTieQuote;</item>
/// <item>fell while someone else went on to win: the game's own Defeat banner and a LossQuote.</item>
/// </list>
/// The game has no "tie" text of its own, so these are ours, in English and Simplified Chinese (the game's language codes "eng" and "zhs"); every other language gets English for now.
/// </summary>
public static class PvpText
{
    public sealed record Lines(string TieBanner, IReadOnlyList<string> TieQuotes, IReadOnlyList<string> NearTieQuotes,
        IReadOnlyList<string> LossQuotes, IReadOnlyList<string> WinQuotes);

    public static readonly Lines English = new("Tie - No Winner", new[]
    {
        "Turns out you were no better...",
        "A foolish feud claims another...",
        "You all won. Just kidding.",
        "Mutually assured destruction.",
        "All that sabotage, and for what?",
        "The Spire thanks you for your cooperation.",
        "Evenly matched. Evenly dead.",
        "Nobody wins. Least of all you.",
    }, new[]
    {
        "So close to sharing the tie. Instead, just a loss.",
        "You missed the tie by one turn. Fitting.",
        "Even your timing was a turn too early.",
        "Couldn't even hang on long enough to tie.",
        "They all lost too. You just lost first.",
        "Not good enough to win, not patient enough to tie.",
        "A front-row seat to a tie you weren't in.",
        "First to fall, last to be remembered.",
    }, new[]
    {
        "Outdrafted, outlasted, outclassed.",
        "Your rival thanks you for the deck you gave them.",
        "They built you a deck to die with. It worked.",
        "Somewhere, your rival is gloating.",
        "You picked their cards. They picked your grave.",
        "Should have been nicer to your picker.",
        "Sabotaged, and you never saw it coming.",
        "The last one standing wasn't you.",
    }, new[]
    {
        "Last one standing. Your friends will remember this.",
        "Victory, built on the ruins of your friends' decks.",
        "You drafted their doom. Well done.",
        "Ruthless, cunning, and still breathing.",
        "Friendship is temporary. Victory is forever.",
        "They trusted you with their cards. Their mistake.",
        "The Spire bows to its pettiest climber.",
        "Survived the Spire and your friends' worst picks.",
    });

    public static readonly Lines SimplifiedChinese = new("平局 - 无人获胜", new[]
    {
        "原来你也没比别人强到哪儿去……",
        "愚蠢的内斗又葬送了一批人……",
        "你们都赢了。开玩笑的。",
        "两败俱伤，无一幸免。",
        "处心积虑地使坏，结果呢？",
        "尖塔感谢各位的通力合作。",
        "势均力敌，一起完蛋。",
        "没有赢家，尤其不是你。",
    }, new[]
    {
        "差一点就能一起平局，结果只是输了。",
        "离平局只差一回合，很符合你的风格。",
        "连倒下的时机都早了一回合。",
        "连撑到平局都做不到。",
        "他们也都输了，只是你输得最早。",
        "赢不了，也没耐心等到平局。",
        "前排观看了一场与你无关的平局。",
        "最先倒下，最后被人记起。",
    }, new[]
    {
        "被算计、被熬死、被碾压。",
        "对手感谢你送的这副好牌。",
        "他们为你配了一副送命的牌。很成功。",
        "此刻，你的对手正在得意。",
        "你替他们选牌，他们替你选了坟墓。",
        "早该对帮你选牌的人好一点。",
        "被人暗算了，你却浑然不觉。",
        "最后站着的人，不是你。",
    }, new[]
    {
        "最后一个站着的人。你的朋友会记住这一刻的。",
        "胜利，建立在朋友们牌组的废墟之上。",
        "你亲手为他们选好了末路。干得漂亮。",
        "冷酷、狡猾，而且还活着。",
        "友谊是暂时的，胜利是永恒的。",
        "他们把牌交给了你，这是他们的错。",
        "尖塔向它最记仇的攀登者低头。",
        "熬过了尖塔，也熬过了朋友们最狠的选牌。",
    });

    /// <param name="language">The game's language code, e.g. "eng" or "zhs".</param>
    public static Lines For(string? language) =>
        string.Equals(language, "zhs", StringComparison.OrdinalIgnoreCase) ? SimplifiedChinese : English;
}
