using System;

namespace RealtimeTranslator.AgcBench;

/// <summary>評価対象のゲイン処理バリアント。1 フレーム (2400 サンプル) を PCM16 へ変換する。</summary>
internal interface IGainVariant
{
    string Name { get; }
    float Gain { get; }
    float AppliedGain { get; }
    byte[] ProcessFrame(ReadOnlySpan<float> frame);
}
