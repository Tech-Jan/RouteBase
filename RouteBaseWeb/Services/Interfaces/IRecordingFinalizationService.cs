namespace RouteBaseWeb.Services.Interfaces;

public interface IRecordingFinalizationService
{
    /// <summary>
    /// Computes all activity statistics from the recorded GPS points and persists them
    /// to the Activity row. Marks the session as "finalized".
    /// </summary>
    Task FinalizeAsync(string sessionId);
}
