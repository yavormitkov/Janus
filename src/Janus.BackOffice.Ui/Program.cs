using System;
using System.Net.Http;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Janus.BackOffice.Ui;
using Janus.BackOffice.Ui.Services.Http;
using Janus.BackOffice.Ui.Services.Identity;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

builder.Services.AddSingleton<SimulationIdentityService>();
builder.Services.AddTransient<SimulationHeaderHandler>();

builder.Services.AddScoped<JanusApiClient>(sp =>
{
    var handler = sp.GetRequiredService<SimulationHeaderHandler>();
    handler.InnerHandler = new HttpClientHandler();

    var httpClient = new HttpClient(handler)
    {
        BaseAddress = new Uri("http://localhost:5000/")
    };

    return new JanusApiClient(httpClient);
});

await builder.Build().RunAsync();
