using System;
using RealtimeTranslator.Core.Audio;
using RealtimeTranslator.Core.Realtime;
using Xunit;

namespace RealtimeTranslator.Core.Tests;

/// <summary>ルーティング判定バッファの窓切り詰め。macOS <c>RoutingSourceTextWindow</c> と同値。</summary>
public sealed class RoutingSourceTextWindowTests
{
    // Given: 末尾ウィンドウより長い原文
    // When: ja-en の末尾非空白 scalar 窓で切り詰める
    // Then: 末尾の非空白 scalar ウィンドウ相当が残り、上限を超えない
    [Fact]
    public void TrimKeepsRecentEvidenceWindow()
    {
        var prefix = new string('あ', 64);
        var tail = "hello world today";

        var trimmed = RoutingSourceTextWindow.Trim(prefix + tail, LanguagePair.JaEn);

        Assert.True(trimmed.EndsWith(tail, StringComparison.Ordinal) || trimmed.Contains("world", StringComparison.Ordinal));
        Assert.True(trimmed.Length <= RoutingSourceTextWindow.MaxLength);
    }

    // Given: 空白と改行だけの原文
    // When: ja-en の末尾非空白 scalar 窓で切り詰める
    // Then: 非空白 scalar が無いため空文字になり、ルーティング証拠を残さない
    [Fact]
    public void TrimWhitespaceOnlyBecomesEmpty()
    {
        var text = "  \n\t  ";

        var trimmed = RoutingSourceTextWindow.Trim(text, LanguagePair.JaEn);

        Assert.Equal(string.Empty, trimmed);
    }

    // Given: 空白と改行だけの原文
    // When: en-es の語窓切り詰めを行う
    // Then: 語が無いため空文字になり、空白 run 圧縮の " " を残さない
    [Fact]
    public void TrimEnEsWhitespaceOnlyBecomesEmpty()
    {
        var text = "  \n\t  ";

        var trimmed = RoutingSourceTextWindow.Trim(text, LanguagePair.EnEs);

        Assert.Equal(string.Empty, trimmed);
    }

    // Given: 空文字
    // When: ja-en で切り詰める
    // Then: 空文字のまま返す
    [Fact]
    public void TrimEmptyReturnsEmpty()
    {
        var trimmed = RoutingSourceTextWindow.Trim(string.Empty, LanguagePair.JaEn);

        Assert.Equal(string.Empty, trimmed);
    }

    // Given: null 原文
    // When: 切り詰めを呼ぶ
    // Then: ArgumentNullException を message 付きで投げる
    [Fact]
    public void TrimNullThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => RoutingSourceTextWindow.Trim(null!, LanguagePair.JaEn));

        Assert.Equal("text", exception.ParamName);
    }

    // Given: en-es 語窓内に長い語と、上限を超える空白 run がある
    // When: en-es の語窓切り詰めを行う
    // Then: 語窓の語は残り、空白 run は 1 個へ圧縮され、保持長は語窓より短い
    [Fact]
    public void TrimEnEsCollapsesLongWhitespaceRunAndKeepsWordWindow()
    {
        var discarded = "old1 old2 old3 ";
        var longWord = new string('x', 40);
        var gap = new string(' ', RoutingSourceTextWindow.MaxLength + 32);
        var text = discarded + "aa bb cc" + gap + "dd ee ff " + longWord + " hh";
        var selectedWindow = text[SpokenLanguageDetector.RecentWordWindowStart(text)..];

        var trimmed = RoutingSourceTextWindow.Trim(text, LanguagePair.EnEs);

        Assert.Equal("aa bb cc" + gap + "dd ee ff " + longWord + " hh", selectedWindow);
        Assert.Equal("aa bb cc dd ee ff " + longWord + " hh", trimmed);
        Assert.Contains(longWord, trimmed);
        Assert.True(trimmed.Length < selectedWindow.Length);
        Assert.True(trimmed.Length <= RoutingSourceTextWindow.MaxLength);
    }

    // Given: 空白のない1語と、語間空白を圧縮しても上限を超える長いトークン列
    // When: en-es の語窓切り詰めを行う
    // Then: 空白 run は圧縮され、戻り値は上限以内かつ Unicode scalar 境界で切れる
    [Fact]
    public void TrimEnEsCapsLongTokenAndWhitespaceFreeInputAtMaxLength()
    {
        var whitespaceFree = new string('x', RoutingSourceTextWindow.MaxLength + 8);
        var longToken = new string('y', RoutingSourceTextWindow.MaxLength + 3);
        var manyWords = string.Join("   ", ["aa", longToken, "zz"]);
        const string twoUnitScalar = "😀";
        var nearLimit = new string('z', RoutingSourceTextWindow.MaxLength - 1) + twoUnitScalar;

        var trimmedToken = RoutingSourceTextWindow.Trim(whitespaceFree, LanguagePair.EnEs);
        var trimmedWords = RoutingSourceTextWindow.Trim(manyWords, LanguagePair.EnEs);
        var trimmedScalarBoundary = RoutingSourceTextWindow.Trim(nearLimit, LanguagePair.EnEs);

        Assert.Equal(new string('x', RoutingSourceTextWindow.MaxLength), trimmedToken);
        Assert.Equal("aa " + new string('y', RoutingSourceTextWindow.MaxLength - 3), trimmedWords);
        Assert.Equal(nearLimit[..^twoUnitScalar.Length], trimmedScalarBoundary);
        Assert.DoesNotContain(twoUnitScalar, trimmedScalarBoundary);
        Assert.True(trimmedToken.Length <= RoutingSourceTextWindow.MaxLength);
        Assert.True(trimmedWords.Length <= RoutingSourceTextWindow.MaxLength);
        Assert.True(trimmedScalarBoundary.Length <= RoutingSourceTextWindow.MaxLength);
    }
}
