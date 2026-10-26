namespace RouteBaseWeb.Models;

/// <summary>DELETE /api/tours/{id} success body. TS mirror: <c>TourDeleteResponse</c> in
/// src/js/pages/multiday.ts (invariant: C# DTO + TS type change atomically).</summary>
public sealed record TourDeleteResponse(int TourId);
