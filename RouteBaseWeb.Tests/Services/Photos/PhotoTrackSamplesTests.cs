using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Photos;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Photos;

public class PhotoTrackSamplesTests
{
    private sealed class FakeTrackReader : ITrackReader
    {
        private readonly DecodedTrack _track;
        public FakeTrackReader(DecodedTrack track) => _track = track;
        public Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default) =>
            Task.FromResult<DecodedTrack?>(_track);
    }

    [Fact]
    public async Task LoadAsync_NaNDistancePoint_AccumulatesHaversine_NotFlatline()
    {
        // 3 points heading north ~111m apart each; Distance channel valid at 0 and 2, NULL at 1.
        var lat  = new TrackChannel(new[] { 4790000, 4790100, 4790200 }, 1e-5);
        var lng  = new TrackChannel(new[] { 800000, 800000, 800000 }, 1e-5);
        var time = new TrackChannel(new[] { 0, 60, 120 }, 1.0);
        var dist = new TrackChannel(new[] { 0, TrackChannel.NullQ, 222 }, 1.0);
        var track = new DecodedTrack { Count = 3, Lat = lat, Lng = lng, TimeEpochS = time, Distance = dist };

        var samples = await PhotoTrackSamples.LoadAsync(new FakeTrackReader(track), new TrackId(1), default);

        Assert.True(samples[1].DistanceM > samples[0].DistanceM,
            "NaN point must accumulate a haversine increment, not flatline at the previous cumulative value");
        Assert.Equal(222.0, samples[2].DistanceM, 0); // channel resumes at its own authoritative value
    }
}
