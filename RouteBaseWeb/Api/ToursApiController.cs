using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace RouteBaseWeb.Api;

/// <summary>
/// Multi-day tour HTTP surface (B7). Thin: owner scoping lives in the service (VisibleTours is
/// owner-only for tours), so this only maps results to status codes.
/// </summary>
[ApiController]
[Route("api/tours")]
public class ToursApiController : ControllerBase
{
    private readonly IMultiDayTourService _tours;

    public ToursApiController(IMultiDayTourService tours) => _tours = tours;

    /// <summary>Delete the tour row only — its activities are kept and lose the tour link.
    /// A missing or foreign tour is 404 (never leak existence).</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType<TourDeleteResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteTour(int id) =>
        await _tours.DeleteTourAsync(id) ? Ok(new TourDeleteResponse(id)) : NotFound();
}
