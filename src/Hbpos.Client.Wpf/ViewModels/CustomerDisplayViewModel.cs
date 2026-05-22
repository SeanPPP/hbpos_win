using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Client.Wpf.Models;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class CustomerDisplayViewModel : ObservableObject
{
    [ObservableProperty]
    private decimal _subtotal;

    [ObservableProperty]
    private decimal _taxAmount;

    [ObservableProperty]
    private decimal _savingsAmount;

    [ObservableProperty]
    private decimal _totalToPay;

    [ObservableProperty]
    private decimal _totalItemQuantity;

    [ObservableProperty]
    private int _skuCount;

    [ObservableProperty]
    private string _terminalName = "Terminal 01";

    [ObservableProperty]
    private string _promotionTitle = "customer.promotionTitle";

    [ObservableProperty]
    private string _promotionSubtitle = "customer.promotionSubtitle";

    [ObservableProperty]
    private string _promotionBody = "customer.promotionBody";

    [ObservableProperty]
    private bool _isReadyForPayment;

    public ObservableCollection<CustomerDisplayLine> Lines { get; } = [];

    public string TotalToPayLabel => "customer.totalToPay";

    public string ReadyForPaymentLabel => "customer.readyForPayment";

    public string InsertOrTapLabel => "customer.insertOrTap";

    public string SubtotalLabel => "Subtotal";

    public string TaxLabel => "Tax";

    public string SavingsLabel => "Savings";

    public void LoadLines(IEnumerable<CustomerDisplayLine> lines, decimal subtotal, decimal taxAmount, decimal savingsAmount)
    {
        var materialized = lines.ToList();
        Lines.ReplaceWith(materialized);
        Subtotal = subtotal;
        TaxAmount = taxAmount;
        SavingsAmount = savingsAmount;
        TotalToPay = subtotal + taxAmount - savingsAmount;
        TotalItemQuantity = materialized.Sum(line => line.Quantity);
        SkuCount = materialized.Count;
        IsReadyForPayment = TotalToPay > 0m;
    }
}
