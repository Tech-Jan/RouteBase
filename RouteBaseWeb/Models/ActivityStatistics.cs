namespace RouteBaseWeb.Models;

public class ActivityStatistics
{
    public int TotalActivities { get; set; }
    /// <summary>Sum of total_distance from the Activity table. Unit: metres.</summary>
    public double TotalDistance { get; set; }
    /// <summary>Sum of total_ascent from the Activity table. Unit: metres.</summary>
    public double TotalAscent { get; set; }
    /// <summary>Sum of total_descent from the Activity table. Unit: metres.</summary>
    public double TotalDescent { get; set; }
    /// <summary>Sum of time_total from the Activity table. Unit: seconds.</summary>
    public double TotalTime { get; set; }
    /// <summary>Sum of time_moving from the Activity table. Unit: seconds.</summary>
    public double TotalMovingTime { get; set; }
    /// <summary>Average of speed_avg from the Activity table. Unit: m/s. Multiply by 3.6 for km/h.</summary>
    public double AverageSpeed { get; set; }
}
