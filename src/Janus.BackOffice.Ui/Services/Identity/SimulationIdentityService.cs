using System;

namespace Janus.BackOffice.Ui.Services.Identity;

public class SimulationIdentityService
{
    public string CurrentRole { get; private set; } = "Maker";

    public event Action? OnIdentityChanged;

    public void SetRole(string role)
    {
        if (CurrentRole != role)
        {
            CurrentRole = role;
            OnIdentityChanged?.Invoke();
        }
    }
}
