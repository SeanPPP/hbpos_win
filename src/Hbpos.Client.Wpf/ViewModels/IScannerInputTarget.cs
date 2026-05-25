namespace Hbpos.Client.Wpf.ViewModels;

public interface IScannerInputTarget
{
    string ScannerPageId { get; }

    bool ProcessScannerBarcode(string barcode, string devicePath, string source);
}
