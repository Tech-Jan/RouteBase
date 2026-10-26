using System.Text.Json;
using System.Text.Json.Serialization;

namespace RouteBaseWeb.Models.Geometry;

/// <summary>Allowed crossing direction of a gate. <see cref="Forward"/>/<see cref="Backward"/>
/// are the two sides of the gate normal (normal = the line rotated +90°); <see cref="Any"/>
/// accepts both. The camelCase-string wire form is declared ON THE TYPE (TimeMode pattern) so the
/// OpenAPI generator emits the string enum; RuleDocument's own serializer options already produced
/// the same strings, so this is wire-neutral.</summary>
[JsonConverter(typeof(CrossDirectionJsonConverter))]
public enum CrossDirection { Any, Forward, Backward }

/// <summary>Pins CrossDirection's wire form to "any"/"forward"/"backward" under ANY JsonSerializerOptions.</summary>
public sealed class CrossDirectionJsonConverter : JsonStringEnumConverter<CrossDirection>
{
    public CrossDirectionJsonConverter() : base(JsonNamingPolicy.CamelCase) { }
}

/// <summary>
/// A directed gate: a line segment on the lat/long plane that efforts cross. Pure geometry —
/// no EF, no map — so crossing detection is unit-testable with hand-built points.
///
/// Crossings are computed in a local equirectangular frame (x = lng·cos(refLat), y = lat) so a
/// degree of longitude and a degree of latitude are comparable near the gate; this app's
/// activities are regional, so a single reference latitude (the gate midpoint) is accurate enough.
/// </summary>
public sealed class GeoLine
{
    public double Lat1 { get; }
    public double Lng1 { get; }
    public double Lat2 { get; }
    public double Lng2 { get; }
    public CrossDirection Direction { get; }

    public GeoBoundingBox Bbox { get; }

    private readonly double _cosRefLat;

    public GeoLine(double lat1, double lng1, double lat2, double lng2, CrossDirection direction = CrossDirection.Any)
    {
        Lat1 = lat1; Lng1 = lng1; Lat2 = lat2; Lng2 = lng2;
        Direction = direction;
        Bbox = new GeoBoundingBox(lat1, lat2, lng1, lng2);
        _cosRefLat = Math.Cos((lat1 + lat2) * 0.5 * Math.PI / 180.0);
    }

    /// <summary>
    /// Does the track segment (aLat,aLng)→(bLat,bLng) cross this gate? On success reports
    /// <paramref name="t"/> ∈ [0,1) (position along the track segment) and <paramref name="dir"/>
    /// (+1 forward, -1 backward). <paramref name="t"/> is half-open so a sample sitting exactly on
    /// the line is counted by one segment only, never twice.
    /// </summary>
    public bool TryCross(double aLat, double aLng, double bLat, double bLng, out double t, out int dir)
    {
        t = 0; dir = 0;

        double ax = aLng * _cosRefLat, ay = aLat;
        double bx = bLng * _cosRefLat, by = bLat;
        double g1x = Lng1 * _cosRefLat, g1y = Lat1;
        double g2x = Lng2 * _cosRefLat, g2y = Lat2;

        double rx = bx - ax, ry = by - ay;     // track segment direction
        double sx = g2x - g1x, sy = g2y - g1y; // gate direction

        double denom = rx * sy - ry * sx;
        if (Math.Abs(denom) < 1e-15) return false;   // parallel or degenerate

        double qx = g1x - ax, qy = g1y - ay;
        double tt = (qx * sy - qy * sx) / denom;     // param along the track segment
        double uu = (qx * ry - qy * rx) / denom;     // param along the gate
        if (tt < 0 || tt >= 1) return false;
        if (uu < 0 || uu > 1) return false;

        t = tt;
        // Sign of (track velocity · gate normal), normal = (-sy, sx).
        double d = rx * -sy + ry * sx;
        dir = d > 0 ? 1 : (d < 0 ? -1 : 0);
        return true;
    }

    /// <summary>True when <paramref name="dir"/> (from <see cref="TryCross"/>) satisfies this gate's direction constraint.</summary>
    public bool DirectionAllows(int dir) => Direction switch
    {
        CrossDirection.Forward => dir > 0,
        CrossDirection.Backward => dir < 0,
        _ => true,
    };
}
