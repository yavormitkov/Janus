using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Janus.BackOffice.Ui.Services.Identity;

namespace Janus.BackOffice.Ui.Services.Http;

public class SimulationHeaderHandler : DelegatingHandler
{
    private readonly SimulationIdentityService _identityService;

    public SimulationHeaderHandler(SimulationIdentityService identityService)
    {
        _identityService = identityService;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove("X-Simulation-Mode");
        request.Headers.Add("X-Simulation-Mode", _identityService.CurrentRole);
        return base.SendAsync(request, cancellationToken);
    }
}
