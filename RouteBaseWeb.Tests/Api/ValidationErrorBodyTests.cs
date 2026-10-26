using Microsoft.AspNetCore.Mvc.ModelBinding;
using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// API-07's message shaping, as pure logic — no pipeline needed. What the caller reads matters: an
/// unreadable error is only marginally better than the ValidationProblemDetails it replaced.
/// </summary>
public class ValidationErrorBodyTests
{
    private static ModelStateDictionary State(params (string Key, string Error)[] errors)
    {
        var state = new ModelStateDictionary();
        foreach (var (key, error) in errors) state.AddModelError(key, error);
        return state;
    }

    [Fact]
    public void CodeIsValidationFailed_NotTheBadRequestTier()
    {
        // A malformed body is a different thing from input a rule rejected — a client may branch on it.
        var body = ValidationErrorBody.From(State(("$.name", "The Name field is required.")));

        Assert.Equal(ApiErrorCodes.ValidationFailed, body.Code);
        Assert.NotEqual(ApiErrorCodes.BadRequest, body.Code, StringComparer.Ordinal);
    }

    [Fact]
    public void JsonPathPrefix_IsStripped_SoTheMessageNamesTheFieldTheCallerWrote()
    {
        var body = ValidationErrorBody.From(State(("$.trackId", "Expected a number.")));

        Assert.Equal("trackId: Expected a number.", body.Message);
        Assert.DoesNotContain("$.", body.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelLevelError_ReadsAsRequest_NotAsAnEmptyFieldName()
    {
        Assert.Equal("request: The body is empty.", ValidationErrorBody.From(State(("", "The body is empty."))).Message);
        Assert.Equal("request: bad json", ValidationErrorBody.From(State(("$", "bad json"))).Message);
    }

    [Fact]
    public void EveryFailingField_IsNamed_InOneMessage()
    {
        var body = ValidationErrorBody.From(State(
            ("$.name", "is required"),
            ("$.trackId", "must be a number")));

        Assert.Equal("name: is required; trackId: must be a number", body.Message);
    }

    [Fact]
    public void ManyFields_AreCapped_ButTheMessageSaysHowManyItHid()
    {
        var errors = Enumerable.Range(1, ValidationErrorBody.MaxFields + 3)
            .Select(i => ($"$.f{i}", "is required"))
            .ToArray();

        var body = ValidationErrorBody.From(State(errors));

        Assert.Contains("f1: is required", body.Message, StringComparison.Ordinal);
        Assert.Contains($"f{ValidationErrorBody.MaxFields}: is required", body.Message, StringComparison.Ordinal);
        Assert.DoesNotContain($"f{ValidationErrorBody.MaxFields + 1}:", body.Message, StringComparison.Ordinal);
        Assert.EndsWith("(+3 more)", body.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheFirstErrorPerField_IsShown()
    {
        var state = new ModelStateDictionary();
        state.AddModelError("$.name", "is required");
        state.AddModelError("$.name", "must be shorter than 50 characters");

        Assert.Equal("name: is required", ValidationErrorBody.From(state).Message);
    }

    [Fact] // API-01: the STJ formatter surfaces its raw text AS the message (Exception left null) — never echo it
    public void RawParserText_IsNotEchoed_ItLeaksClrTypesAndOffsets()
    {
        var body = ValidationErrorBody.From(State(("$.courseTrackId",
            "The JSON value could not be converted to System.Int32. Path: $.courseTrackId | LineNumber: 0 | BytePositionInLine: 25.")));

        Assert.Equal("courseTrackId: could not be parsed", body.Message);
        Assert.DoesNotContain("System.", body.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BytePositionInLine", body.Message, StringComparison.Ordinal);
    }

    [Fact] // a missing STJ `required` member leaks the CLR type name in a different shape — also degraded
    public void MissingRequiredMemberText_IsNotEchoed()
    {
        var body = ValidationErrorBody.From(State(("$",
            "JSON deserialization for type 'RouteBaseWeb.Models.Requests.CreateRaceRequest' was missing required properties including 'raceName'.")));

        Assert.Equal("request: could not be parsed", body.Message);
        Assert.DoesNotContain("RouteBaseWeb", body.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AParseException_WithNoMessage_StillReadsAsSomething()
    {
        // STJ deserialization failures arrive as an exception with an empty ErrorMessage; the raw
        // exception text is unreadable for a caller, so it must not be what lands on the wire.
        var state = new ModelStateDictionary();
        state.TryAddModelException("$.startedAt", new InvalidOperationException("The JSON value could not be converted"));

        var body = ValidationErrorBody.From(state);

        Assert.Equal("startedAt: could not be parsed", body.Message);
        Assert.DoesNotContain("Exception", body.Message, StringComparison.Ordinal);
    }
}
