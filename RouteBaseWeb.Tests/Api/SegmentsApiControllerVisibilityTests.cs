using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// PATCH {id}/visibility validates the value in the CONTROLLER (the activities-PATCH precedent): the
/// crud's false conflates "not yours / not there" with "bad value", so an unvalidated typo used to
/// surface as 404 "segment gone" — a client would drop the segment from its list instead of showing
/// the input error. Pure input validation ahead of every dependency → null-deps construction.
/// </summary>
public class SegmentsApiControllerVisibilityTests
{
    private static SegmentsApiController NewController() =>
        new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    [Fact]
    public async Task UpdateVisibility_UnknownValue_Is400WithApiError_NotA404()
    {
        var result = await NewController().UpdateVisibility(
            5, new UpdateSegmentVisibilityRequest("everyone"));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var body = Assert.IsType<ApiError>(bad.Value);
        Assert.Equal(ApiErrorCodes.BadRequest, body.Code);
    }
}
