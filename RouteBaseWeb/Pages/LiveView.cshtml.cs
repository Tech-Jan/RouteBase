using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RouteBaseWeb.Pages;

[Authorize]
public class LiveViewModel : PageModel
{
    private readonly ILiveSessionDirectory _liveSessions;
    private readonly IMapTilerService _mapTilerService;

    public LiveViewModel(ILiveSessionDirectory liveSessions, IMapTilerService mapTilerService)
    {
        _liveSessions = liveSessions;
        _mapTilerService = mapTilerService;
    }

    /// <summary>The watched sessions, earliest start first. Never empty on a rendered page.</summary>
    public List<LiveSessionInfo> Sessions { get; set; } = [];
    public string? MapTilerApiKey { get; set; }

    /// <summary>The track id of the viewer's OWN live session, if one is being watched — the only session
    /// they may mint a public live-share link for. Null when watching only others'.</summary>
    public int? SelfTrackId => Sessions.FirstOrDefault(s => s.IsSelf)?.TrackId;

    /// <summary>The single-session route (/LiveView/{id}) and the multi-session query (?sessions=a,b,c) both land here.</summary>
    public async Task<IActionResult> OnGetAsync(string? sessionId, string? sessions)
    {
        var ids = string.IsNullOrWhiteSpace(sessionId)
            ? (sessions ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [sessionId];

        Sessions = await _liveSessions.ResolveAsync(ids);
        if (Sessions.Count == 0)
            return NotFound();

        MapTilerApiKey = await _mapTilerService.GetApiKeyAsync();
        return Page();
    }
}
