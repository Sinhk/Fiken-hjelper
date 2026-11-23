namespace FikenHjelper.Core.MinSpeiding;

public interface IMinSpeidingClient
{
    Task<MinSpeidingEventLoadResult> GetEventParticipantsAsync(
        string projectId,
        string participantsApiKey,
        string? questionsApiKey = null,
        string? baseUrlOverride = null,
        CancellationToken ct = default);
}
