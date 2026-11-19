using Janus.Core.Shared.Enums;
using Janus.Ledger.Api.CommandHandlers;
using Janus.Ledger.Api.Commands;
using Janus.Ledger.Api.Handlers;
using Janus.Ledger.Api.Queries;
using Janus.Ledger.Api.QueryHandlers;
using janus_ledger_api.CommandHandlers;
using janus_ledger_api.Commands;

namespace Janus.LedgerApi.Extensions;

public static class SuspenseEndpointsExtensions
{
    public static IEndpointRouteBuilder MapSuspenseEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/suspense")
                          .RequireRateLimiting("SuspenseApiPolicy");

        group.MapGet("/unresolved/{status:int}", async (GetUnresolvedSuspenseTransactionsHandler handler, int status) =>
        {
            var query = new GetUnresolvedSuspenseTransactionsQuery((SuspenseStatus)status);
            var result = await handler.HandleAsync(query);
            return Results.Ok(result);
        });

        group.MapGet("/{id:guid}", async (Guid id, GetSuspenseTransactionByIdHandler handler) =>
        {
            var query = new GetSuspenseTransactionByIdQuery(id);
            var result = await handler.HandleAsync(query);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapPost("/correct", async (CorrectSuspenseTransactionCommand command, CorrectSuspenseTransactionHandler handler, HttpContext context) =>
        {
            var userIdStr = context.Request.Headers["X-User-Id"].ToString();
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            var updatedCommand = command with { MakerUserId = userId };
            var isSuccess = await handler.HandleAsync(updatedCommand);
            return isSuccess
                ? Results.Ok(new { success = true, message = "Transaction updated by Maker." })
                : Results.UnprocessableEntity(new { success = false, message = "Transaction not found or invalid status." });
        });

        group.MapPost("/approve", async (ApproveSuspenseTransactionCommand command, ApproveSuspenseTransactionHandler handler, HttpContext context) =>
        {
            var userIdStr = context.Request.Headers["X-User-Id"].ToString();
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            var updatedCommand = command with { CheckerUserId = userId };
            var isSuccess = await handler.HandleAsync(updatedCommand);
            return isSuccess
                ? Results.Ok(new { success = true, message = "Transaction approved by Checker." })
                : Results.BadRequest(new { success = false, message = "Approval failed. Check 4-eyes principle or status." });
        });

        group.MapPost("/reject", async (RejectSuspenseTransactionCommand command, RejectSuspenseTransactionHandler handler, HttpContext context) =>
        {
            var userIdStr = context.Request.Headers["X-User-Id"].ToString();
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            var updatedCommand = command with { CheckerUserId = userId };
            var isSuccess = await handler.HandleAsync(updatedCommand);
            return isSuccess
                ? Results.Ok(new { success = true, message = "Transaction rejected." })
                : Results.BadRequest(new { success = false, message = "Rejection failed. Check 4-eyes principle or status." });
        });

        group.MapPost("/return-to-provider", async (ReturnSuspenseToProviderCommand command, ReturnSuspenseToProviderHandler handler, HttpContext context) =>
        {
            var userIdStr = context.Request.Headers["X-User-Id"].ToString();
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            var updatedCommand = command with { OperatorUserId = userId };
            var isSuccess = await handler.HandleAsync(updatedCommand);
            return isSuccess
                ? Results.Ok(new { success = true, message = "Duplicate transaction marked for return to provider." })
                : Results.BadRequest(new { success = false, message = "Action failed. Only duplicate transactions can be returned." });
        });

        return routes;
    }
}
