using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models.Requests;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Unit tests for <see cref="RecordingRequestValidator"/> (MD-02) — boundary validation of the
/// recording write path. Pure logic, no mocks: the default request must pass so an empty JSON body
/// keeps working; out-of-range values must be rejected (not normalized).
/// </summary>
public class RecordingRequestValidatorTests
{
    [Fact]
    public void Validate_DefaultStartRequest_ReturnsNull()
    {
        Assert.Null(RecordingRequestValidator.Validate(new StartRecordingRequest()));
    }

    [Fact]
    public void Validate_UnknownVisibility_ReturnsError()
    {
        Assert.NotNull(RecordingRequestValidator.Validate(
            new StartRecordingRequest { Visibility = "banana" }));
    }

    [Theory]
    [InlineData(999999, false)]
    [InlineData(-720, true)]
    [InlineData(840, true)]
    public void Validate_TimezoneOffsetOutOfRange_ReturnsError(int offset, bool valid)
    {
        var result = RecordingRequestValidator.Validate(
            new StartRecordingRequest { TimezoneOffsetMinutes = offset });
        Assert.Equal(valid, result is null);
    }

    [Fact]
    public void Validate_TrackNameTooLong_ReturnsError()
    {
        Assert.NotNull(RecordingRequestValidator.Validate(
            new StartRecordingRequest { TrackName = new string('x', 201) }));
    }

    [Theory]
    [InlineData(91, false)]
    [InlineData(90, true)]
    public void Validate_BatchWithBadLat_ReturnsError(double lat, bool valid)
    {
        // Timestamp supplied so this exercises the lat bound only (RS-03 now also rejects a default
        // 0001-01-01 timestamp — see Validate_DefaultTimestamp_Rejected).
        var batch = new RecordingPointsBatch
        {
            Points = [new RecordingGpsPoint { Lat = lat, Lng = 0, TimestampUtc = DateTime.UtcNow }]
        };
        Assert.Equal(valid, RecordingRequestValidator.Validate(batch, DateTime.UtcNow) is null);
    }

    // ── RS-03: NaN/∞ + altitude/speed/timestamp guards ──────────────────────────

    // Builds a single-point batch from a nominal valid point, applying one mutation (class, not record —
    // no `with`, so an Action override).
    private static RecordingPointsBatch Batch(Action<RecordingGpsPoint>? mutate = null)
    {
        var p = new RecordingGpsPoint
        {
            Lat = 47.0, Lng = 11.0, Altitude = 1000, Speed = 5, TimestampUtc = DateTime.UtcNow,
        };
        mutate?.Invoke(p);
        return new RecordingPointsBatch { Points = [p] };
    }

    [Fact]
    public void Validate_NaNLat_Rejected() =>
        Assert.NotNull(RecordingRequestValidator.Validate(Batch(p => p.Lat = double.NaN), DateTime.UtcNow));

    [Fact]
    public void Validate_InfinityLng_Rejected() =>
        Assert.NotNull(RecordingRequestValidator.Validate(Batch(p => p.Lng = double.PositiveInfinity), DateTime.UtcNow));

    [Fact]
    public void Validate_NaNAltitude_Rejected() =>
        Assert.NotNull(RecordingRequestValidator.Validate(Batch(p => p.Altitude = double.NaN), DateTime.UtcNow));

    [Fact]
    public void Validate_NegativeSpeed_Rejected() =>
        Assert.NotNull(RecordingRequestValidator.Validate(Batch(p => p.Speed = -1), DateTime.UtcNow));

    [Fact]
    public void Validate_OutOfRangePressure_Rejected() =>
        Assert.NotNull(RecordingRequestValidator.Validate(Batch(p => p.Pressure = 50), DateTime.UtcNow));

    [Fact]
    public void Validate_NaNPressure_Rejected() =>
        Assert.NotNull(RecordingRequestValidator.Validate(Batch(p => p.Pressure = double.NaN), DateTime.UtcNow));

    [Fact]
    public void Validate_PlausiblePressure_Accepted() =>
        Assert.Null(RecordingRequestValidator.Validate(Batch(p => p.Pressure = 1013.25), DateTime.UtcNow));

    [Fact]
    public void Validate_FarFutureTimestamp_Rejected() =>
        Assert.NotNull(RecordingRequestValidator.Validate(Batch(p => p.TimestampUtc = DateTime.UtcNow.AddDays(2)), DateTime.UtcNow));

    [Fact]
    public void Validate_DefaultTimestamp_Rejected() =>
        Assert.NotNull(RecordingRequestValidator.Validate(Batch(p => p.TimestampUtc = default), DateTime.UtcNow));

    [Fact]
    public void Validate_OfflineBufferedBatch_Accepted() =>
        Assert.Null(RecordingRequestValidator.Validate(Batch(p => p.TimestampUtc = DateTime.UtcNow.AddHours(-30)), DateTime.UtcNow));

    [Fact]
    public void Validate_ValidBatch_Passes() =>
        Assert.Null(RecordingRequestValidator.Validate(Batch(), DateTime.UtcNow));
}
