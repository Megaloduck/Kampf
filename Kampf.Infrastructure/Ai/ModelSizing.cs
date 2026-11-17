namespace Kampf.Infrastructure.Ai;

public enum ModelFitVerdict
{
    Comfortable,
    Tight,
    TooLarge,
}

/// <summary>
/// What one GGUF file will cost in memory: the weights are the file itself, the
/// context cache at 4k tokens is estimated at 15% of the weights, and llama.cpp
/// needs a fixed 320 MB for its working buffers.
/// </summary>
public sealed record ModelFit(
    long WeightBytes,
    long ContextBytes,
    long WorkingBytes,
    long RequiredBytes,
    long AvailableBytes,
    ModelFitVerdict Verdict)
{
    public string Describe() =>
        $"about {ModelSizing.FormatBytes(RequiredBytes)} of RAM " +
        $"({ModelSizing.FormatBytes(WeightBytes)} weights + " +
        $"{ModelSizing.FormatBytes(ContextBytes)} context cache + " +
        $"{ModelSizing.FormatBytes(WorkingBytes)} working) \u2014 " +
        $"{ModelSizing.FormatBytes(AvailableBytes)} free, {VerdictText}";

    public string VerdictText => Verdict switch
    {
        ModelFitVerdict.Comfortable => "fits comfortably",
        ModelFitVerdict.Tight => "fits, but little memory is left for other apps",
        _ => "too large until more memory is free",
    };
}

/// <summary>A model small enough to be worth recommending on this machine.</summary>
public sealed record ModelRecommendation(
    string RepoId,
    string FileName,
    string Label,
    long WeightBytes)
{
    public string Describe() =>
        $"{Label} (Q4_K_M) \u2014 {ModelSizing.FormatBytes(WeightBytes)} to download, " +
        $"about {ModelSizing.FormatBytes(ModelSizing.RequiredFor(WeightBytes))} of RAM";
}

/// <summary>
/// Sizes a model against the memory a machine has free and picks the largest
/// bundled offering that fits. Deliberately conservative: a smaller model that
/// answers quickly beats one that swaps.
/// </summary>
public static class ModelSizing
{
    /// <summary>The context length the app opens (see <c>LocalChatModel</c>).</summary>
    public const int ContextTokens = 4096;

    private const double ContextShare = 0.15;

    private const long WorkingBytes = 320L * 1024 * 1024;

    private const long Gigabyte = 1024L * 1024 * 1024;

    /// <summary>Q4_K_M builds that download as a single file and run in this app.</summary>
    private static readonly ModelTier[] Tiers =
    {
        new("Qwen/Qwen2.5-0.5B-Instruct-GGUF", "qwen2.5-0.5b-instruct-q4_k_m.gguf", "Qwen2.5 0.5B", 491_400_032),
        new("Qwen/Qwen2.5-1.5B-Instruct-GGUF", "qwen2.5-1.5b-instruct-q4_k_m.gguf", "Qwen2.5 1.5B", 1_117_320_736),
        new("Qwen/Qwen2.5-3B-Instruct-GGUF", "qwen2.5-3b-instruct-q4_k_m.gguf", "Qwen2.5 3B", 2_104_932_768),
        new("bartowski/Qwen2.5-7B-Instruct-GGUF", "Qwen2.5-7B-Instruct-Q4_K_M.gguf", "Qwen2.5 7B", 4_683_074_240),
        new("bartowski/Qwen2.5-14B-Instruct-GGUF", "Qwen2.5-14B-Instruct-Q4_K_M.gguf", "Qwen2.5 14B", 8_988_110_976),
    };

    public static ModelFit Estimate(long weightBytes, long availableBytes)
    {
        if (weightBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(weightBytes), "A model needs a positive size.");

        var context = ContextFor(weightBytes);
        var required = weightBytes + context + WorkingBytes;
        var available = Math.Max(0, availableBytes);

        var verdict = required <= available * 0.7
            ? ModelFitVerdict.Comfortable
            : required <= available * 0.9
                ? ModelFitVerdict.Tight
                : ModelFitVerdict.TooLarge;

        return new ModelFit(weightBytes, context, WorkingBytes, required, available, verdict);
    }

    /// <summary>The full memory cost of a model whose size is known.</summary>
    public static long RequiredFor(long weightBytes) =>
        weightBytes + ContextFor(weightBytes) + WorkingBytes;

    /// <summary>
    /// Starts from the tier the machine's total memory suggests, then steps down
    /// while that model would not fit in what is actually free.
    /// </summary>
    public static ModelRecommendation Recommend(HardwareProfile hardware)
    {
        var index = hardware.TotalMemoryBytes switch
        {
            < 6 * Gigabyte => 0,
            < 10 * Gigabyte => 1,
            < 18 * Gigabyte => 2,
            < 34 * Gigabyte => 3,
            _ => 4,
        };

        while (index > 0
               && Estimate(Tiers[index].WeightBytes, hardware.AvailableMemoryBytes).Verdict
                   == ModelFitVerdict.TooLarge)
        {
            index--;
        }

        var tier = Tiers[index];
        return new ModelRecommendation(tier.RepoId, tier.FileName, tier.Label, tier.WeightBytes);
    }

    public static string FormatBytes(long bytes) =>
        bytes >= Gigabyte
            ? $"{bytes / (double)Gigabyte:0.0} GB"
            : $"{bytes / (1024d * 1024):0} MB";

    private static long ContextFor(long weightBytes) =>
        (long)Math.Ceiling(weightBytes * ContextShare);

    private sealed record ModelTier(string RepoId, string FileName, string Label, long WeightBytes);
}
