namespace RouteBaseWeb.Services.Navigation;

public record NavItem(
    string Href,
    string Label,
    string Icon,
    string? BadgeId = null,
    IReadOnlyList<NavItem>? Children = null,
    bool ToggleOnly = false);

public static class NavMenu
{
    public static readonly IReadOnlyList<NavItem> Primary =
    [
        new("/",           "Dashboard",    "home"),
        new("/Activities", "Activities",   "calendar"),
        new("/Activities/Trash", "Trash",   "trash"),
        new("/MultiDay",   "Multi-Day Tours", "map"),
        new("/Segments",   "Segments",     "target"),
        new("/Races",      "Races",        "trophy"),
        new("/Sharing",    "Shared Links", "share"),
        new("/Statistics", "Statistics", "chart"),
        new("/Friends",        "Friends",  "users", BadgeId: "navFriendsBadge"),
        new("/Record",         "Record",   "record"),
        new("/Photos",         "Photos",   "image"),
        new("/Import",         "Import",   "upload"),
        new("/Activities?editorPick=1", "Track Editor", "scissors"),
        new("/ProfileSettings", "Profile", "user", ToggleOnly: true, Children:
        [
            new("/ProfileSettings", "Settings",    "gear"),
            new("/KeysAndData",     "API Keys & Database", "cloud"),
            new("/Friends",        "Friends",      "users", BadgeId: "navProfileFriendsBadge"),
        ]),
    ];

    // Mobile "More" sheet — order/labels intentionally differ from Primary ("Settings" not "Profile").
    // Single source for the mobile tabbar's More active-state set and the sheet's link list.
    public static readonly IReadOnlyList<NavItem> MobileSheet =
    [
        new("/MultiDay",        "Multi-Day Tours", "map"),
        new("/Races",           "Races",           "trophy"),
        new("/Sharing",         "Shared Links",    "share"),
        new("/Record",          "Record",          "record"),
        new("/Friends",         "Friends",         "users"),
        new("/ProfileSettings", "Settings",        "gear"),
        new("/KeysAndData",     "API Keys & Database", "cloud"),
        new("/Photos",          "Photos",          "image"),
        new("/Import",          "Import",          "upload"),
        new("/Activities?editorPick=1", "Track Editor", "scissors"),
        new("/Activities/Trash", "Trash",          "trash"),
    ];
}
