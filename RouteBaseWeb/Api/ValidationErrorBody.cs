using Microsoft.AspNetCore.Mvc.ModelBinding;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Api;

/// <summary>
/// API-07: turns MVC's model-binding/validation failures into the canonical <see cref="ApiError"/>.
///
/// Without this, <c>[ApiController]</c> answers a malformed request body with the framework's
/// <c>ValidationProblemDetails</c> — the one error shape on the wire that D31/D32 never unified, and the
/// one no generated client can read (they all parse <c>{code, message}</c>).
///
/// The per-field errors are FLATTENED into the message rather than carried as their own map: nothing in
/// this app renders per-field server errors (forms validate client-side), so a structured body would be
/// a new schema no one reads. The code is specific — <c>validationFailed</c> — because "your body did not
/// parse" is genuinely different from "your input was rejected by a rule", and a client may branch on it.
/// </summary>
public static class ValidationErrorBody
{
    /// <summary>Fields listed in one message; beyond this the message says how many more there are.</summary>
    internal const int MaxFields = 5;

    public static ApiError From(ModelStateDictionary modelState)
    {
        var fields = new List<(string Field, string Message)>();
        foreach (var (key, entry) in modelState)
            if (entry is { Errors.Count: > 0 })
                fields.Add((Name(key), FirstMessage(entry)));

        if (fields.Count == 0) // defensive: the factory only runs on an invalid state
            return ApiError.WithValidationFailure("The request body is not valid.");

        var shown = string.Join("; ", fields.Take(MaxFields).Select(f => $"{f.Field}: {f.Message}"));
        var hidden = fields.Count - MaxFields;
        return ApiError.WithValidationFailure(hidden > 0 ? $"{shown} (+{hidden} more)" : shown);
    }

    /// <summary>STJ reports body paths as <c>$.trackId</c>; strip the JSON-path prefix so the message
    /// names the field the caller wrote. A model-level error has no key at all.</summary>
    private static string Name(string key) =>
        string.IsNullOrEmpty(key) ? "request"
        : key.StartsWith("$.", StringComparison.Ordinal) ? key[2..]
        : key == "$" ? "request"
        : key;

    /// <summary>One message per field: the rest are almost always the same failure restated, and the
    /// framework's raw JSON-parse text is unreadable for a caller.</summary>
    private static string FirstMessage(ModelStateEntry entry)
    {
        var error = entry.Errors[0];
        if (string.IsNullOrWhiteSpace(error.ErrorMessage))
            return error.Exception is null ? "is not valid" : "could not be parsed";
        // API-01: the System.Text.Json input formatter surfaces its OWN exception text as the message —
        // it wraps JsonException in an InputFormatterException, which ModelState stores as ErrorMessage
        // (with Exception left null), so the earlier Exception-based guard never saw it. That text leaks
        // CLR type names and byte/line offsets; its diagnostic tokens are stable and never appear in a
        // DataAnnotations message, so degrade to the generic line rather than echo internals.
        if (IsRawParserText(error.ErrorMessage)) return "could not be parsed";
        return error.ErrorMessage;
    }

    /// <summary>Tells of a raw System.Text.Json deserialization message (type-mismatch, malformed JSON, or
    /// a missing required member) — none of which a hand-written validation message contains.</summary>
    private static bool IsRawParserText(string message) =>
        message.Contains("LineNumber:", StringComparison.Ordinal)
        || message.Contains("BytePositionInLine:", StringComparison.Ordinal)
        || message.Contains("could not be converted", StringComparison.Ordinal)
        || message.Contains("JSON deserialization for type", StringComparison.Ordinal);
}
