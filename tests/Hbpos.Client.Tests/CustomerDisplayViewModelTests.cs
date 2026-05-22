using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Tests;

public sealed class CustomerDisplayViewModelTests
{
    [Fact]
    public void LoadLines_calculates_item_quantity_and_sku_count()
    {
        var viewModel = new CustomerDisplayViewModel();

        viewModel.LoadLines(
            [
                new CustomerDisplayLine("Milk", "SKU-001", 2m, 3m, 6m),
                new CustomerDisplayLine("Bread", "SKU-002", 1.5m, 4m, 6m)
            ],
            subtotal: 12m,
            taxAmount: 0m,
            savingsAmount: 1m);

        Assert.Equal(3.5m, viewModel.TotalItemQuantity);
        Assert.Equal(2, viewModel.SkuCount);
        Assert.Equal(11m, viewModel.TotalToPay);
    }
}
