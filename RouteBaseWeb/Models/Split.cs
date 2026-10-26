namespace RouteBaseWeb.Models;

public class Split
{
    public int SplitNumber { get; set; }
    public double Distance { get; set; }
    public double CumulativeDistance { get; set; }
    public TimeSpan Time { get; set; }
    public double Pace { get; set; }
    public double Speed { get; set; }
    public double ElevationGain { get; set; }
    public double ElevationLoss { get; set; }
    public double NetElevationChange { get; set; }
}
