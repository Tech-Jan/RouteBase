using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RouteBaseWeb.Models;

[Table("Multi_Day_Tour")]
public class MultiDayTour
{
    [Key]
    [Column("multi_day_ID")]
    public int MultiDayId { get; set; }

    [Column("multi_day_tour_name")]
    [MaxLength(255)]
    public string? MultiDayTourName { get; set; }

    [Column("multi_day_description")]
    public string? MultiDayDescription { get; set; }

    [Column("start_date")]
    public DateTime? StartDate { get; set; }

    [Column("end_date")]
    public DateTime? EndDate { get; set; }

    [Column("total_distance")]
    public double? TotalDistance { get; set; }

    [Column("total_ascent")]
    public double? TotalAscent { get; set; }

    [Column("total_descent")]
    public double? TotalDescent { get; set; }

    [Column("total_time")]
    public double? TotalTime { get; set; }

    [Column("total_days")]
    public int? TotalDays { get; set; }

    [Column("total_activities")]
    public int? TotalActivities { get; set; }

    [Column("user_id")]
    public string? UserId { get; set; }

    // Navigation properties
    [ForeignKey("UserId")]
    public virtual AppUser? User { get; set; }
    public virtual ICollection<Activity> Activities { get; set; } = new List<Activity>();
}
