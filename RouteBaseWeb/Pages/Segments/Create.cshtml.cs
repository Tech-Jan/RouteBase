using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages.Segments;

public class CreateModel : PageModel
{
    private readonly IActivityQueryService _activityService;

    public CreateModel(IActivityQueryService activityService)
    {
        _activityService = activityService;
    }

    public List<Activity> Activities { get; set; } = new();

    public async Task OnGetAsync()
    {
        // No cap: a Take(n) here silently hid rides — newest-first, so exactly the older rides a user
        // wants to trace a segment over (same fix as the Races course picker).
        Activities = await _activityService.GetActivitiesWithGpsCoordinatesAsync(int.MaxValue);
    }
}
