using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages;

public class MultiDayModel : PageModel
{
    private readonly IMultiDayTourService _multiDayTourService;

    public MultiDayModel(IMultiDayTourService multiDayTourService)
    {
        _multiDayTourService = multiDayTourService;
    }

    public List<MultiDayTour> MultiDayTours { get; set; } = new();

    public async Task OnGetAsync()
    {
        MultiDayTours = await _multiDayTourService.GetAllMultiDayToursAsync();
    }
}
