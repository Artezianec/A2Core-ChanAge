using System.Text;
using System.Text.Json;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Core.Security;

namespace Aion.Host.Authentication;

public static class QaBotEndpoints
{
    public static void MapQaBotEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/qa/runs", async (HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IQaBotStore store, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct, StaffPermission.ViewDashboard) is null) return Results.Unauthorized();
            var offset = int.TryParse(ctx.Request.Query["offset"], out var o) ? o : 0;
            var limit = int.TryParse(ctx.Request.Query["limit"], out var l) ? l : 50;
            return Results.Ok(await store.ListRunsAsync(offset, limit, ct));
        });
        app.MapGet("/api/admin/qa/runs/{runId:long}", async (long runId, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IQaBotStore store, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct, StaffPermission.ViewDashboard) is null) return Results.Unauthorized();
            var run = await store.GetRunAsync(runId, ct);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });
        app.MapGet("/api/admin/qa/runs/{runId:long}/reports", async (long runId, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IQaBotStore store, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct, StaffPermission.ViewDashboard) is null) return Results.Unauthorized();
            return Results.Ok(await store.ListReportsForRunAsync(runId, ct));
        });
        app.MapGet("/api/admin/qa/stuck-summary", async (HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IQaBotStore store, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct, StaffPermission.ViewDashboard) is null) return Results.Unauthorized();
            var limit = int.TryParse(ctx.Request.Query["limit"], out var l) ? l : 100;
            return Results.Ok(await store.StuckSummaryAsync(limit, ct));
        });
        app.MapGet("/api/admin/qa/export", async (HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IQaBotStore store, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct, StaffPermission.ViewDashboard) is null) return Results.Unauthorized();
            long? runId = long.TryParse(ctx.Request.Query["runId"], out var id) ? id : null;
            var format = ctx.Request.Query["format"].ToString();
            var reports = await store.ExportReportsAsync(runId, ct);
            if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder("report_id,run_id,quest_code,quest_step,reason_code,level,map_id,created_at\n");
                foreach (var r in reports)
                    sb.Append($"{r.ReportId},{r.RunId},{r.QuestCode},{r.QuestStep},{r.ReasonCode},{r.CharacterLevel},{r.MapId},{r.CreatedAt:O}\n");
                return Results.Text(sb.ToString(), "text/csv");
            }
            return Results.Json(reports, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        });
    }
}