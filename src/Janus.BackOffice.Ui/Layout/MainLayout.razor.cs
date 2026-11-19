using Microsoft.AspNetCore.Components;
using Janus.BackOffice.Ui.Services.Identity;

namespace Janus.BackOffice.Ui.Layout;

public partial class MainLayout
{
    [Inject]
    private SimulationIdentityService IdentityService { get; set; } = null!;

    private void OnRoleChanged(ChangeEventArgs e)
    {
        var selectedRole = e.Value?.ToString();
        if (!string.IsNullOrEmpty(selectedRole))
        {
            IdentityService.SetRole(selectedRole);
        }
    }
}
