using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>Pins the DELETE /api/tours/{id} contract: 200 + named TourDeleteResponse on success,
/// bodyless 404 when the service refuses (missing/foreign).</summary>
public class ToursApiControllerTests
{
    private sealed class StubTourService(bool deleteResult) : IMultiDayTourService
    {
        public Task<List<MultiDayTour>> GetAllMultiDayToursAsync() => Task.FromResult(new List<MultiDayTour>());
        public Task<bool> DeleteTourAsync(int tourId) => Task.FromResult(deleteResult);
    }

    [Fact]
    public async Task Delete_ReturnsNamedDto_WhenDeleted()
    {
        var result = await new ToursApiController(new StubTourService(true)).DeleteTour(7);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<TourDeleteResponse>(ok.Value);
        Assert.Equal(7, body.TourId);
    }

    [Fact]
    public async Task Delete_Returns404_WhenServiceRefuses()
    {
        var result = await new ToursApiController(new StubTourService(false)).DeleteTour(7);
        Assert.IsType<NotFoundResult>(result);
    }
}
