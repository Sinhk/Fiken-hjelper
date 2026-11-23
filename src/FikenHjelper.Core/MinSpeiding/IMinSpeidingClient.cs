namespace FikenHjelper.Core.MinSpeiding;

public interface IMinSpeidingClient
{
    Task<MinSpeidingEventLoadResult> GetEventParticipantsAsync(
        string projectId,
        string apiKey,
        string? baseUrlOverride = null,
        CancellationToken ct = default);
}
