using Janus.Ledger.Api.CommandHandlers;
using Janus.Ledger.Api.Handlers;
using Janus.Ledger.Api.Infrastructure;
using Janus.Ledger.Api.QueryHandlers;
using Janus.Ledger.Api.Repositories;
using Janus.LedgerApi.Extensions;
using janus_ledger_api.CommandHandlers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
builder.Services.AddScoped<IDapperSuspenseTransactionRepository, DapperSuspenseTransactionRepository>();

builder.Services.AddScoped<ApproveSuspenseTransactionHandler>();
builder.Services.AddScoped<RejectSuspenseTransactionHandler>();
builder.Services.AddScoped<CorrectSuspenseTransactionHandler>();
builder.Services.AddScoped<ReturnSuspenseToProviderHandler>();

builder.Services.AddScoped<GetUnresolvedSuspenseTransactionsHandler>();
builder.Services.AddScoped<GetSuspenseTransactionByIdHandler>();

builder.Services.AddSuspenseRateLimiting();

var app = builder.Build();

app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
        if (error is UnauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { success = false, message = error.Message });
            return;
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { success = false, message = "An unexpected server error occurred." });
    });
});

app.UseHttpsRedirection();
app.UseRateLimiter();

app.MapSuspenseEndpoints();

app.Run();
