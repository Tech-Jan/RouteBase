using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Services.Photos;

/// <summary>Materializes a track's <see cref="TrackSample"/> stream (time + position + cumulative
/// distance) from a decoded blob — shared by the import matcher (position) and the API (marker
/// placement). Distance falls back to cumulative haversine when the blob has no distance channel.</summary>
public static class PhotoTrackSamples
{
    public static async Task<IReadOnlyList<TrackSample>> LoadAsync(
        ITrackReader reader, TrackId trackId, CancellationToken ct)
    {
        var decoded = await reader.GetTrackAsync(trackId, ct);
        if (decoded == null || decoded.Count == 0) return Array.Empty<TrackSample>();

        var samples = new TrackSample[decoded.Count];
        bool hasDist = !decoded.Distance.IsNull;
        for (int i = 0; i < decoded.Count; i++)
        {
            double lat = decoded.Lat[i], lng = decoded.Lng[i];
            double dist;
            if (hasDist)
            {
                double d = decoded.Distance[i];
                dist = double.IsNaN(d)
                    ? (i > 0
                        ? samples[i - 1].DistanceM + GeoUtils.HaversineDistance(samples[i - 1].Lat, samples[i - 1].Lng, lat, lng)
                        : 0)
                    : d;
            }
            else
            {
                dist = i > 0
                    ? samples[i - 1].DistanceM + GeoUtils.HaversineDistance(samples[i - 1].Lat, samples[i - 1].Lng, lat, lng)
                    : 0;
            }
            samples[i] = new TrackSample((long)decoded.TimeEpochS[i], lat, lng, dist);
        }
        return samples;
    }
}
