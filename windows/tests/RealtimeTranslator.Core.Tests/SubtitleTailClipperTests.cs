using System;
using System.Globalization;
using System.Linq;
using System.Text;
using RealtimeTranslator.Core.Subtitles;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

public sealed class SubtitleTailClipperTests
{
    // Given: 結合文字 (e + combining acute) が上限を超える英語
    // When: 末尾クリップする
    // Then: UTF-16 符号単位の途中ではなく書記素クラスタ境界で切り、結合文字を孤立させない
    [Fact]
    public void ClipDoesNotSplitCombiningCharacters()
    {
        var unit = "e\u0301";
        var text = string.Concat(Enumerable.Repeat(unit, SubtitleTailClipper.EnglishCharacterLimit + 10));

        var clipped = SubtitleTailClipper.Clip(text);

        Assert.StartsWith(SubtitleTailClipper.Ellipsis, clipped, StringComparison.Ordinal);
        var body = clipped[SubtitleTailClipper.Ellipsis.Length..];
        Assert.Equal(SubtitleTailClipper.EnglishCharacterLimit, CountTextElements(body));
        Assert.False(
            IsCombiningMark(Rune.GetRuneAt(body, 0)),
            "suffix must not start with a combining mark");
        Assert.StartsWith(unit, body, StringComparison.Ordinal);
        Assert.EndsWith(unit, body, StringComparison.Ordinal);
    }

    // Given: スペイン語の反転記号・アクセント・ñ を含む長文
    // When: 末尾クリップする
    // Then: ¿ ¡ ñ を符号単位の途中で割らず、語先頭の反転記号を残す
    [Fact]
    public void ClipKeepsSpanishInvertedMarksAndTildeOnCharacterBoundaries()
    {
        var sentence = "¿Cómo estás? ¡Qué niño más grande! ";
        var text = string.Concat(Enumerable.Repeat(sentence, 8));

        var clipped = SubtitleTailClipper.Clip(text);

        Assert.StartsWith(SubtitleTailClipper.Ellipsis, clipped, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', clipped);
        Assert.Contains("¿", clipped, StringComparison.Ordinal);
        Assert.Contains("¡", clipped, StringComparison.Ordinal);
        Assert.Contains("ñ", clipped, StringComparison.Ordinal);
        foreach (var mark in new[] { "¿", "¡" })
        {
            var index = clipped.IndexOf(mark, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            Assert.True(
                index == 0 || clipped[index - 1] is ' ' or '…',
                "inverted punctuation must sit on a character boundary, not mid-word");
        }
    }

    // Given: サロゲートペア絵文字が上限を超える
    // When: 末尾クリップする
    // Then: 上位/下位サロゲートを分割しない
    [Fact]
    public void ClipDoesNotSplitSurrogatePairs()
    {
        var unit = "😀";
        var text = string.Concat(Enumerable.Repeat(unit, SubtitleTailClipper.EnglishCharacterLimit + 5));

        var clipped = SubtitleTailClipper.Clip(text);

        Assert.StartsWith(SubtitleTailClipper.Ellipsis, clipped, StringComparison.Ordinal);
        var body = clipped[SubtitleTailClipper.Ellipsis.Length..];
        Assert.Equal(SubtitleTailClipper.EnglishCharacterLimit, CountTextElements(body));
        Assert.False(char.IsSurrogate(body[0]) && !char.IsHighSurrogate(body[0]));
        Assert.True(Rune.TryGetRuneAt(body, 0, out _));
        Assert.EndsWith(unit, body, StringComparison.Ordinal);
    }

    private static int CountTextElements(string text)
    {
        var count = 0;
        var offset = 0;
        while (offset < text.Length)
        {
            offset += StringInfo.GetNextTextElementLength(text.AsSpan(offset));
            count += 1;
        }

        return count;
    }

    private static bool IsCombiningMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
}
