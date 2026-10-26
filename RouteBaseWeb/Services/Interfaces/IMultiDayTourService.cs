using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Service for multi-day tour management and aggregation.
/// </summary>
public interface IMultiDayTourService
{
    Task<List<MultiDayTour>> GetAllMultiDayToursAsync();

    /// <summary>Delete the tour ROW only (B7): its activities keep living and just lose the tour link.
    /// Owner-scoped; false = missing or not the caller's.</summary>
    Task<bool> DeleteTourAsync(int tourId);
}
