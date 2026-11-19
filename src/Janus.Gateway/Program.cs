using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Janus.Gateway.Middlewares;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("BlazorWasmCorsPolicy", policy =>
    {
        policy.WithOrigins("http://localhost:5002")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.UseRouting();

app.UseCors("BlazorWasmCorsPolicy");

app.UseMiddleware<DemoIdentityMiddleware>();

app.MapReverseProxy();

app.Run();
