namespace RouteBaseWeb.Models;

public record EmptyStateModel(
    string Body,
    string? CtaHref = null,
    string? CtaLabel = null,
    bool IsWarning = false
);
