using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services;

/// <summary>
/// Pure-function utility for calculating distance-based activity splits
/// from a list of GPS data points.
/// </summary>
public static class SplitCalculator
{
    /// <summary>
    /// Splits GPS data into distance-based segments and computes per-split metrics
    /// (time, speed, pace, elevation gain/loss).
    /// </summary>
    /// <param name="gpsData">Ordered GPS points with distance and altitude.</param>
    /// <param name="splitDistanceKm">Split interval in kilometres.</param>
    public static List<Split> CalculateSplits(List<GpsData> gpsData, decimal splitDistanceKm)
    {
        var splits = new List<Split>();

        if (gpsData.Count == 0) return splits;

        double splitDistance = (double)splitDistanceKm * 1000; // Convert to meters
        int splitNumber = 1;
        double currentSplitDistance = splitDistance;

        var splitStartPoint = gpsData[0];
        double splitStartDistance = splitStartPoint.Distance ?? 0;
        // Splits are measured relative to the track's first distance, so slices whose Distance
        // doesn't start at ~0 (e.g. a segment effort) still split correctly.
        double baseDistance = splitStartDistance;
        double splitElevGain = 0;
        double splitElevLoss = 0;
        double previousAltitude = splitStartPoint.Altitude ?? 0;

        foreach (var point in gpsData)
        {
            double currentDistance = point.Distance ?? 0;
            double currentAltitude = point.Altitude ?? 0;

            // Track elevation changes within split
            double altChange = currentAltitude - previousAltitude;
            if (altChange > 0)
                splitElevGain += altChange;
            else
                splitElevLoss += Math.Abs(altChange);

            previousAltitude = currentAltitude;

            // Check if we've completed a split
            if (currentDistance - baseDistance >= currentSplitDistance)
            {
                double splitDistanceActual = currentDistance - splitStartDistance;
                TimeSpan splitTime = ComputeSplitTime(splitStartPoint, point);
                splits.Add(BuildSplit(splitNumber, splitDistanceActual, currentDistance, splitTime, splitElevGain, splitElevLoss));

                // Prepare for next split
                splitNumber++;
                currentSplitDistance += splitDistance;
                splitStartPoint = point;
                splitStartDistance = currentDistance;
                splitElevGain = 0;
                splitElevLoss = 0;
                previousAltitude = currentAltitude;
            }
        }

        // Add final partial split if there's remaining distance (at least 10% of split distance)
        var lastPoint = gpsData[^1];
        double remainingDistance = (lastPoint.Distance ?? 0) - splitStartDistance;

        if (remainingDistance > splitDistance * 0.1)
        {
            TimeSpan splitTime = ComputeSplitTime(splitStartPoint, lastPoint);
            splits.Add(BuildSplit(splitNumber, remainingDistance, lastPoint.Distance ?? 0, splitTime, splitElevGain, splitElevLoss));
        }

        return splits;
    }

    private static Split BuildSplit(int number, double distM, double cumDistM, TimeSpan time, double elevGain, double elevLoss)
    {
        double distKm = distM / 1000.0;
        double speedKmh = time.TotalHours > 0 ? distKm / time.TotalHours : 0;
        double paceMinPerKm = distKm > 0 ? time.TotalMinutes / distKm : 0;
        return new Split
        {
            SplitNumber        = number,
            Distance           = Math.Round(distKm, 2),
            CumulativeDistance = Math.Round(cumDistM / 1000.0, 2),
            Time               = time,
            Speed              = Math.Round(speedKmh, 2),
            Pace               = Math.Round(paceMinPerKm, 2),
            ElevationGain      = Math.Round(elevGain, 1),
            ElevationLoss      = Math.Round(elevLoss, 1),
            NetElevationChange = Math.Round(elevGain - elevLoss, 1)
        };
    }

    private static TimeSpan ComputeSplitTime(GpsData start, GpsData end)
    {
        // UTC is the only per-point timestamp; local time is derived from the activity offset at
        // display only (GPS.datetime_loc dropped — SL-01). UTC is DST-safe within a split anyway.
        if (start.DateTimeUtc.HasValue && end.DateTimeUtc.HasValue)
            return end.DateTimeUtc.Value - start.DateTimeUtc.Value;
        return TimeSpan.Zero;
    }
}
