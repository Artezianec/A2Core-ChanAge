using Aion.Host.Authentication;
using Aion.World;

namespace Aion.Host.Native;

/// <summary>
/// Field boss notices (Aion:FieldBosses:Announcements, docs/FIELD-BOSSES.md): every few seconds the notices the sessions queued
/// (a boss's kill, its return when due) go to every player's screen in their client's language (EN or RU) and to chat in English.
/// Ported from ckrealm/A2Core 77ac9ed FunNoticeService (AGPL-3.0).
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
                        foreach (var entry in presence.All) _ = entry.Session.InvokeAsync(_ => (true, new List<byte[]> { packet }));
                    }
                    await StaffEndpoints.ChatAnnounceAsync(services, notice.English);
                    logger.LogInformation("Field boss notice: {Text}", notice.English);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException)
                {
                    logger.LogWarning(error, "Field boss notice failed: {Text}", notice.English);
                }
            }
        }
    }
}
