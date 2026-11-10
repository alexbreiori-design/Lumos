namespace Lumos.Services;

internal static class BrightnessMath
{
    public const int Min = 0;
    public const int Max = 100;
    public const int OverlayAlphaAtZero = 180;

    public static int Clamp(int value) => Math.Clamp(value, Min, Max);

    public static int[] ApplyDelta(IReadOnlyList<int> snapshot, int delta)
    {
        var result = new int[snapshot.Count];
        for (var i = 0; i < snapshot.Count; i++)
            result[i] = Clamp(snapshot[i] + delta);
        return result;
    }

    public static int Average(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
            return Max;
        return (int)Math.Round(values.Average());
    }

    public static uint ToDevice(int percent, uint max)
    {
        if (max == 0)
            return 0;
        return (uint)Math.Round(Clamp(percent) / 100.0 * max);
    }

    public static int ToPercent(uint current, uint max)
    {
        if (max == 0)
            return 0;
        var clamped = Math.Clamp(current, 0, max);
        return (int)Math.Round(clamped * 100.0 / max);
    }

    public static byte AlphaForBrightness(int brightness)
    {
        var level = Clamp(brightness);
        if (level >= Max)
            return 0;
        return (byte)Math.Round((Max - level) * OverlayAlphaAtZero / 100.0);
    }
}
