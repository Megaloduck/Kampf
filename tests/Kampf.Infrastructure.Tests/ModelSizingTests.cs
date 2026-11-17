using Kampf.Infrastructure.Ai;
using Xunit;

namespace Kampf.Infrastructure.Tests;

public class ModelSizingTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private static HardwareProfile Machine(long totalGb, long freeGb, int cores = 8) =>
        new(cores, totalGb * Gigabyte, freeGb * Gigabyte);

    [Fact]
    public void The_probe_reports_cores_and_real_memory()
    {
        var hardware = HardwareProbe.Detect();

        Assert.True(hardware.LogicalCores > 0);
        Assert.True(hardware.TotalMemoryBytes > 0);
        Assert.InRange(hardware.AvailableMemoryBytes, 0, hardware.TotalMemoryBytes);
        Assert.True(hardware.TotalMemoryGb > 0);
    }

    [Fact]
    public void A_small_machine_is_offered_the_smallest_model()
    {
        var recommendation = ModelSizing.Recommend(Machine(4, 2));

        Assert.Equal("qwen2.5-0.5b-instruct-q4_k_m.gguf", recommendation.FileName);
        Assert.StartsWith("Qwen/Qwen2.5-0.5B", recommendation.RepoId);
    }

    [Fact]
    public void A_sixteen_gigabyte_laptop_is_offered_the_three_billion_model() =>
        Assert.Equal(
            "qwen2.5-3b-instruct-q4_k_m.gguf",
            ModelSizing.Recommend(Machine(16, 8)).FileName);

    [Fact]
    public void A_large_machine_is_offered_the_fourteen_billion_model() =>
        Assert.Equal(
            "Qwen2.5-14B-Instruct-Q4_K_M.gguf",
            ModelSizing.Recommend(Machine(64, 40)).FileName);

    [Fact]
    public void A_machine_with_little_free_memory_steps_down()
    {
        var hardware = Machine(32, 2);
        var recommendation = ModelSizing.Recommend(hardware);

        Assert.Equal("qwen2.5-1.5b-instruct-q4_k_m.gguf", recommendation.FileName);
        Assert.True(ModelSizing.Estimate(recommendation.WeightBytes, hardware.AvailableMemoryBytes).RequiredBytes < 2 * Gigabyte);
    }

    [Fact]
    public void An_estimate_breaks_the_cost_into_weights_context_and_working_memory()
    {
        var fit = ModelSizing.Estimate(2 * Gigabyte, Machine(16, 8).AvailableMemoryBytes);

        Assert.Equal(2 * Gigabyte, fit.WeightBytes);
        Assert.Equal((long)Math.Ceiling(2 * Gigabyte * 0.15), fit.ContextBytes);
        Assert.Equal(320L * 1024 * 1024, fit.WorkingBytes);
        Assert.Equal(fit.WeightBytes + fit.ContextBytes + fit.WorkingBytes, fit.RequiredBytes);
        Assert.Equal(ModelFitVerdict.Comfortable, fit.Verdict);
        Assert.Contains("fits comfortably", fit.Describe());
    }

    [Fact]
    public void A_model_larger_than_the_free_memory_is_too_large()
    {
        var fit = ModelSizing.Estimate(10 * Gigabyte, Machine(16, 4).AvailableMemoryBytes);

        Assert.Equal(ModelFitVerdict.TooLarge, fit.Verdict);
        Assert.Contains("too large", fit.Describe());
    }

    [Fact]
    public void A_recommendation_says_what_it_will_cost()
    {
        var recommendation = ModelSizing.Recommend(Machine(16, 8));

        Assert.Contains("Qwen2.5 3B", recommendation.Describe());
        Assert.Contains("to download", recommendation.Describe());
        Assert.True(ModelSizing.RequiredFor(recommendation.WeightBytes) > recommendation.WeightBytes);
    }

    [Fact]
    public void An_unknown_size_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModelSizing.Estimate(0, 8 * Gigabyte));
}
