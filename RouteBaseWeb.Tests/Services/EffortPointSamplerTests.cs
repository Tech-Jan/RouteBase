using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// PS-01 pins for the uniform-stride index sampler: full-resolution passthrough, below-budget
/// passthrough, and endpoint-pinned monotonic downsampling.
/// </summary>
public class EffortPointSamplerTests
{
    [Fact]
    public void SampleIndices_BudgetZero_ReturnsAll()
    {
        var idx = EffortPointSampler.SampleIndices(100, 0);
        Assert.Equal(100, idx.Length);
        Assert.Equal(Enumerable.Range(0, 100), idx);
    }

    [Fact]
    public void SampleIndices_CountBelowBudget_ReturnsAll()
    {
        var idx = EffortPointSampler.SampleIndices(50, 100);
        Assert.Equal(Enumerable.Range(0, 50), idx);
    }

    [Fact]
    public void SampleIndices_PinsFirstAndLast_Monotonic()
    {
        var idx = EffortPointSampler.SampleIndices(1000, 10);

        Assert.Equal(10, idx.Length);
        Assert.Equal(0, idx[0]);          // first pinned
        Assert.Equal(999, idx[^1]);       // last pinned
        for (int i = 1; i < idx.Length; i++)
            Assert.True(idx[i] > idx[i - 1], "indices must be strictly ascending");
    }

    [Fact]
    public void SampleIndices_ZeroCount_ReturnsEmpty()
    {
        Assert.Empty(EffortPointSampler.SampleIndices(0, 100));
    }
}
