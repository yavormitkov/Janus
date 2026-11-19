using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;

namespace Janus.Gateway.Middlewares;

public class DemoIdentityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;

    public DemoIdentityMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Simulation-Mode", out var simulationMode))
        {
            var mode = simulationMode.ToString();

            if (mode == "Maker")
            {
                var makerId = _configuration["IdentitySimulation:Maker:UserId"] ?? string.Empty;
                var makerRole = _configuration["IdentitySimulation:Maker:UserRole"] ?? string.Empty;

                context.Request.Headers.Remove("X-User-Id");
                context.Request.Headers.Remove("X-User-Role");

                context.Request.Headers.Add("X-User-Id", makerId);
                context.Request.Headers.Add("X-User-Role", makerRole);
            }
            else if (mode == "Checker")
            {
                var checkerId = _configuration["IdentitySimulation:Checker:UserId"] ?? string.Empty;
                var checkerRole = _configuration["IdentitySimulation:Checker:UserRole"] ?? string.Empty;

                context.Request.Headers.Remove("X-User-Id");
                context.Request.Headers.Remove("X-User-Role");

                context.Request.Headers.Add("X-User-Id", checkerId);
                context.Request.Headers.Add("X-User-Role", checkerRole);
            }
        }

        await _next(context);
    }
}
