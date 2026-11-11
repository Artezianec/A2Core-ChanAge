using Aion.Host.Authentication;
using Aion.World;

namespace Aion.Host.Native;

/// <summary>
/// Field boss broadcasts (Aion:FieldBosses:AnnouncementScope / ChatBroadcast, docs/FIELD-BOSSES.md): every few seconds
/// the notices the sessions queued (a boss's kill, its return when due) go to the configured audience (off, the same
/// map, or everyone) as a centre notice in the client's language, and optionally to chat in English. Official default
/// is off. Ported from ckrealm/A2Core 77ac9ed FunNoticeService (AGPL-3.0).
/// </summary>
public sealed class FieldBossNoticeService(IServiceProvider services, ILogger<FieldBossNoticeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var notice in FieldBosses.TakeDue())
            {
                try
                {
                    if (services.GetService<WorldPresence>() is { } presence)
                    {
                        var packet = GameSession.Notice(notice.English, notice.Russian, 20, "FFD54A");
                        foreach (var entry in presence.All)
                        {
                            if (notice.Scope == FieldBossBroadcastScope.Off) continue;
                            if (notice.Scope == FieldBossBroadcastScope.Zone && entry.Session.MapId != notice.MapId) continue;
                            _ = entry.Session.InvokeAsync(_ => (true, new List<byte[]> { packet }));
                        }
                    }
                    if (FieldBosses.Options.ChatBroadcast)
                        await StaffEndpoints.ChatAnnounceAsync(services, notice.English);
                    logger.LogInformation("Field boss notice ({Scope}, map {Map}): {Text}", notice.Scope, notice.MapId, notice.English);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException)
                {
                    logger.LogWarning(error, "Field boss notice failed: {Text}", notice.English);
                }
            }
        }
    }
}
