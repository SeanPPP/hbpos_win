using System.Windows;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf.Services;

public interface ICustomerDisplayOrchestrator
{
    event EventHandler? Closed;

    void LoadFromCart(CustomerDisplayViewModel customerDisplay, PosSessionState session, PosCartService cart);

    void Prewarm(CustomerDisplayViewModel customerDisplay, PosSessionState session, PosCartService cart)
    {
    }

    CustomerDisplayWindowMode GetNextMode(CustomerDisplayWindowMode currentMode);

    CustomerDisplayWindowResult SetMode(
        CustomerDisplayWindowMode mode,
        CustomerDisplayViewModel customerDisplay,
        PosSessionState session,
        PosCartService cart,
        Window? owner);
}

public sealed class CustomerDisplayOrchestrator(
    ICustomerDisplayWindowService customerDisplayWindowService) : ICustomerDisplayOrchestrator
{
    public event EventHandler? Closed
    {
        add => customerDisplayWindowService.Closed += value;
        remove => customerDisplayWindowService.Closed -= value;
    }

    public void LoadFromCart(CustomerDisplayViewModel customerDisplay, PosSessionState session, PosCartService cart)
    {
        var lines = cart.Lines.Select(line => new CustomerDisplayLine(
            line.DisplayName,
            line.LookupCode,
            line.Quantity,
            line.UnitPrice,
            line.ActualAmount));
        customerDisplay.TerminalName = session.DeviceCode;
        customerDisplay.LoadLines(lines, cart.TotalAmount, 0m, cart.DiscountAmount);
    }

    public void Prewarm(CustomerDisplayViewModel customerDisplay, PosSessionState session, PosCartService cart)
    {
        LoadFromCart(customerDisplay, session, cart);
        customerDisplayWindowService.Prewarm(customerDisplay);
    }

    public CustomerDisplayWindowMode GetNextMode(CustomerDisplayWindowMode currentMode)
    {
        return currentMode switch
        {
            CustomerDisplayWindowMode.Closed => CustomerDisplayWindowMode.Normal,
            CustomerDisplayWindowMode.Normal => CustomerDisplayWindowMode.Fullscreen,
            _ => CustomerDisplayWindowMode.Closed
        };
    }

    public CustomerDisplayWindowResult SetMode(
        CustomerDisplayWindowMode mode,
        CustomerDisplayViewModel customerDisplay,
        PosSessionState session,
        PosCartService cart,
        Window? owner)
    {
        if (mode != CustomerDisplayWindowMode.Closed)
        {
            LoadFromCart(customerDisplay, session, cart);
        }

        return customerDisplayWindowService.SetMode(mode, customerDisplay, owner);
    }
}
