using System.Diagnostics;
using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

public sealed record PosTerminalWorkflowResult
{
    public string? StatusKey { get; init; }

    public object[] StatusArgs { get; init; } = [];

    public SellableItemDto? SelectedItem { get; init; }

    public CartLine? SelectedCartLine { get; init; }

    public IReadOnlyList<SellableItemDto>? Matches { get; init; }

    public bool? MatchesPopupOpen { get; init; }

    public bool? TouchKeyboardOpen { get; init; }

    public bool? WholeOrderOperation { get; init; }

    public bool ClearScanText { get; init; }

    public bool ClearKeypadBuffer { get; init; }

    public bool PaymentAllowed { get; init; }
}

public sealed class PosTerminalCatalogReloadedEventArgs : EventArgs
{
    public PosTerminalCatalogReloadedEventArgs(IReadOnlyList<SellableItemDto> catalogItems)
    {
        CatalogItems = catalogItems;
    }

    public IReadOnlyList<SellableItemDto> CatalogItems { get; }
}

public interface IPosTerminalWorkflowService
{
    event EventHandler<PosTerminalCatalogReloadedEventArgs>? CatalogReloaded;

    PosTerminalWorkflowResult ProcessScan(PosSessionState session, string scanText, bool preferExactLookup, string source);

    PosTerminalWorkflowResult AddSelectedItem(PosSessionState session, SellableItemDto item, bool clearScanText, bool closeMatchesPopup, string operation);

    PosTerminalWorkflowResult RemoveLine(CartLine? line);

    PosTerminalWorkflowResult IncreaseLine(CartLine? line);

    PosTerminalWorkflowResult DecreaseLine(CartLine? line);

    PosTerminalWorkflowResult ModifySelectedLineQuantity(CartLine? selectedLine, string keypadBuffer);

    PosTerminalWorkflowResult ModifySelectedLinePrice(CartLine? selectedLine, string keypadBuffer);

    PosTerminalWorkflowResult ApplySelectedLineDiscountAmount(CartLine? selectedLine, string keypadBuffer, bool isWholeOrderOperation);

    PosTerminalWorkflowResult ApplySelectedLineDiscountPercent(CartLine? selectedLine, string keypadBuffer, bool isWholeOrderOperation);

    PosTerminalWorkflowResult ApplyQuickDiscountPercent(CartLine? selectedLine, string? value, bool isWholeOrderOperation);

    PosTerminalWorkflowResult ClearCart();

    PosTerminalWorkflowResult GuardPayment();
}

public sealed class PosTerminalWorkflowService : IPosTerminalWorkflowService
{
    private static readonly TimeSpan RemoteLookupTimeout = TimeSpan.FromSeconds(2);

    private readonly LocalSellableItemIndex _priceIndex;
    private readonly PosCartService _cart;
    private readonly Func<string, string, CancellationToken, Task<RemoteLookupRefreshResult>>? _remoteLookupRefreshAsync;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? _reloadCatalogAsync;

    public PosTerminalWorkflowService(
        LocalSellableItemIndex priceIndex,
        PosCartService cart,
        Func<string, string, CancellationToken, Task<RemoteLookupRefreshResult>>? remoteLookupRefreshAsync = null,
        Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>>? reloadCatalogAsync = null)
    {
        _priceIndex = priceIndex;
        _cart = cart;
        _remoteLookupRefreshAsync = remoteLookupRefreshAsync;
        _reloadCatalogAsync = reloadCatalogAsync;
    }

    public event EventHandler<PosTerminalCatalogReloadedEventArgs>? CatalogReloaded;

    public PosTerminalWorkflowResult ProcessScan(PosSessionState session, string scanText, bool preferExactLookup, string source)
    {
        var submittedScanText = scanText;
        var totalStopwatch = Stopwatch.StartNew();
        var exactLookupElapsedMs = 0L;
        var searchElapsedMs = 0L;
        var matchKind = preferExactLookup ? "exact-not-found" : "search";
        var matchCount = 0;
        var autoAdded = false;
        PosTerminalWorkflowResult result;

        if (preferExactLookup)
        {
            var exactLookupStopwatch = Stopwatch.StartNew();
            var exactMatches = _priceIndex.FindExactMatches(session.StoreCode, submittedScanText);
            exactLookupStopwatch.Stop();
            exactLookupElapsedMs = exactLookupStopwatch.ElapsedMilliseconds;
            matchKind = exactMatches.Count switch
            {
                0 => "exact-not-found",
                1 => "lookup-exact",
                _ => "search-multiple"
            };

            var matches = exactMatches;
            var allowAutoAdd = exactMatches.Count == 1;
            if (exactMatches.Count == 0)
            {
                var metadataMatches = _priceIndex.FindMetadataExactMatches(session.StoreCode, submittedScanText);
                if (metadataMatches.Count > 0)
                {
                    matches = metadataMatches;
                    matchKind = metadataMatches.Count > 1 ? "metadata-duplicate" : "metadata-only";
                }
            }

            result = ApplyScanMatches(session, matches, submittedScanText, allowAutoAdd);
            matchCount = matches.Count;
            autoAdded = result.ClearScanText;
        }
        else
        {
            var exactLookupStopwatch = Stopwatch.StartNew();
            var exactMatches = _priceIndex.FindExactMatches(session.StoreCode, submittedScanText);
            exactLookupStopwatch.Stop();
            exactLookupElapsedMs = exactLookupStopwatch.ElapsedMilliseconds;
            var hasDuplicateExactMatch = exactMatches.Count > 1;
            if (hasDuplicateExactMatch)
            {
                matchKind = "search-multiple";
            }

            var searchStopwatch = Stopwatch.StartNew();
            var matches = _priceIndex.Search(session.StoreCode, submittedScanText);
            searchStopwatch.Stop();
            searchElapsedMs = searchStopwatch.ElapsedMilliseconds;

            result = ApplyScanMatches(session, matches, submittedScanText, allowAutoAdd: !hasDuplicateExactMatch);
            matchCount = matches.Count;
            autoAdded = result.ClearScanText;
        }

        totalStopwatch.Stop();
        ConsoleLog.Write(
            "PosScan",
            $"barcode={submittedScanText} storeCode={session.StoreCode} source={source} hit={matchKind} matchCount={matchCount} autoAdded={FormatBool(autoAdded)} cartLines={_cart.Lines.Count} exactLookupElapsedMs={exactLookupElapsedMs} searchElapsedMs={searchElapsedMs} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");

        return result;
    }

    public PosTerminalWorkflowResult AddSelectedItem(
        PosSessionState session,
        SellableItemDto item,
        bool clearScanText,
        bool closeMatchesPopup,
        string operation)
    {
        return AddItem(session, item, clearScanText, closeMatchesPopup, operation);
    }

    public PosTerminalWorkflowResult RemoveLine(CartLine? line)
    {
        if (line is null || !_cart.RemoveLine(line))
        {
            return new PosTerminalWorkflowResult();
        }

        return Status("pos.status.ready");
    }

    public PosTerminalWorkflowResult IncreaseLine(CartLine? line)
    {
        if (line is null)
        {
            return new PosTerminalWorkflowResult();
        }

        return !_cart.IncreaseLine(line)
            ? Status("cart.status.quantityMustBeInteger")
            : Status("pos.status.ready") with { SelectedCartLine = line };
    }

    public PosTerminalWorkflowResult DecreaseLine(CartLine? line)
    {
        if (line is null)
        {
            return new PosTerminalWorkflowResult();
        }

        if (!_cart.DecreaseLine(line))
        {
            return Status("cart.status.quantityMustBeInteger");
        }

        return _cart.Lines.Contains(line)
            ? Status("pos.status.ready") with { SelectedCartLine = line }
            : Status("pos.status.ready");
    }

    public PosTerminalWorkflowResult ModifySelectedLineQuantity(CartLine? selectedLine, string keypadBuffer)
    {
        if (!TryGetSelectedLineKeypadValue(selectedLine, keypadBuffer, out var line, out var value, out var failure))
        {
            return failure;
        }

        if (value <= 0m)
        {
            return Status("pos.status.quantityMustBePositive");
        }

        if (!PosCartService.IsPositiveIntegerQuantity(value))
        {
            return Status("cart.status.quantityMustBeInteger");
        }

        if (!_cart.SetLineQuantity(line, value))
        {
            return new PosTerminalWorkflowResult();
        }

        return Status("pos.status.lineQuantityUpdated") with
        {
            SelectedCartLine = line,
            ClearKeypadBuffer = true
        };
    }

    public PosTerminalWorkflowResult ModifySelectedLinePrice(CartLine? selectedLine, string keypadBuffer)
    {
        if (!TryGetSelectedLineKeypadValue(selectedLine, keypadBuffer, out var line, out var value, out var failure))
        {
            return failure;
        }

        if (!_cart.SetLineUnitPrice(line, value))
        {
            return new PosTerminalWorkflowResult();
        }

        return Status("pos.status.linePriceUpdated") with
        {
            SelectedCartLine = line,
            ClearKeypadBuffer = true
        };
    }

    public PosTerminalWorkflowResult ApplySelectedLineDiscountAmount(CartLine? selectedLine, string keypadBuffer, bool isWholeOrderOperation)
    {
        if (isWholeOrderOperation)
        {
            return ApplyWholeOrderDiscountAmount(keypadBuffer);
        }

        if (!TryGetSelectedLineKeypadValue(selectedLine, keypadBuffer, out var line, out var value, out var failure))
        {
            return failure;
        }

        if (value > line.GrossAmount)
        {
            return Status("pos.status.discountAmountTooHigh");
        }

        if (!_cart.SetLineDiscountAmount(line, value))
        {
            return new PosTerminalWorkflowResult();
        }

        return Status("pos.status.lineDiscountUpdated") with
        {
            SelectedCartLine = line,
            ClearKeypadBuffer = true
        };
    }

    public PosTerminalWorkflowResult ApplySelectedLineDiscountPercent(CartLine? selectedLine, string keypadBuffer, bool isWholeOrderOperation)
    {
        if (isWholeOrderOperation)
        {
            return ApplyWholeOrderDiscountPercent(keypadBuffer);
        }

        if (!TryGetSelectedLineKeypadValue(selectedLine, keypadBuffer, out var line, out var value, out var failure))
        {
            return failure;
        }

        return ApplySelectedLineDiscountPercent(line, value);
    }

    public PosTerminalWorkflowResult ApplyQuickDiscountPercent(CartLine? selectedLine, string? value, bool isWholeOrderOperation)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var discountPercent) ||
            discountPercent < 0m)
        {
            return Status("pos.status.invalidKeypadValue");
        }

        if (discountPercent > 100m)
        {
            return Status("pos.status.discountPercentOutOfRange");
        }

        return isWholeOrderOperation
            ? ApplyWholeOrderDiscountPercent(discountPercent)
            : ApplySelectedLineDiscountPercentCore(selectedLine, discountPercent);
    }

    public PosTerminalWorkflowResult ClearCart()
    {
        _cart.Clear();
        return Status("pos.status.cartCleared");
    }

    public PosTerminalWorkflowResult GuardPayment()
    {
        if (_cart.HasNonIntegerQuantity)
        {
            return Status("cart.status.quantityMustBeInteger");
        }

        if (_cart.HasZeroPriceLine)
        {
            return Status("cart.status.zeroPriceItem");
        }

        return new PosTerminalWorkflowResult { PaymentAllowed = true };
    }

    private PosTerminalWorkflowResult ApplyScanMatches(
        PosSessionState session,
        IReadOnlyList<SellableItemDto> matches,
        string submittedScanText,
        bool allowAutoAdd)
    {
        var selectedItem = matches.FirstOrDefault();
        if (selectedItem is null)
        {
            return Status("pos.status.noLocalMatch") with
            {
                Matches = matches,
                MatchesPopupOpen = false
            };
        }

        if (allowAutoAdd && (matches.Count == 1 || IsExactLookup(selectedItem, submittedScanText)))
        {
            var addResult = AddItem(session, selectedItem, clearScanText: true, closeMatchesPopup: false, "scan-auto-add");
            return addResult with
            {
                Matches = matches,
                SelectedItem = selectedItem,
                MatchesPopupOpen = false
            };
        }

        return Status("pos.status.multipleMatches", matches.Count) with
        {
            Matches = matches,
            SelectedItem = selectedItem,
            MatchesPopupOpen = true
        };
    }

    private PosTerminalWorkflowResult AddItem(
        PosSessionState session,
        SellableItemDto item,
        bool clearScanText,
        bool closeMatchesPopup,
        string operation)
    {
        if (!PosCartService.IsPositiveIntegerQuantity(item.QuantityFactor))
        {
            return Status("cart.status.quantityMustBeInteger");
        }

        CartLine line;
        try
        {
            line = _cart.AddItem(item);
        }
        catch (InvalidOperationException)
        {
            return Status("cart.status.quantityMustBeInteger");
        }

        BeginRemoteLookup(session, line, item);
        return Status("pos.status.added", item.DisplayName) with
        {
            SelectedCartLine = line,
            ClearScanText = clearScanText,
            MatchesPopupOpen = closeMatchesPopup ? false : null,
            TouchKeyboardOpen = false
        };
    }

    private PosTerminalWorkflowResult ApplyWholeOrderDiscountAmount(string keypadBuffer)
    {
        if (!TryGetOrderDiscountKeypadValue(keypadBuffer, out var value, out var failure))
        {
            return failure;
        }

        if (value > _cart.TotalAmount)
        {
            return Status("pos.status.discountAmountTooHigh");
        }

        if (!_cart.SetOrderDiscountAmount(value))
        {
            return new PosTerminalWorkflowResult();
        }

        return Status("pos.status.orderDiscountUpdated") with
        {
            WholeOrderOperation = false,
            ClearKeypadBuffer = true
        };
    }

    private PosTerminalWorkflowResult ApplyWholeOrderDiscountPercent(string keypadBuffer)
    {
        if (!TryGetOrderDiscountKeypadValue(keypadBuffer, out var value, out var failure))
        {
            return failure;
        }

        return ApplyWholeOrderDiscountPercent(value);
    }

    private PosTerminalWorkflowResult ApplyWholeOrderDiscountPercent(decimal value)
    {
        if (_cart.IsEmpty)
        {
            return Status("pos.status.selectCartLine");
        }

        if (value > 100m)
        {
            return Status("pos.status.discountPercentOutOfRange");
        }

        if (!_cart.SetOrderDiscountPercent(value))
        {
            return new PosTerminalWorkflowResult();
        }

        return Status("pos.status.orderDiscountUpdated") with
        {
            WholeOrderOperation = false,
            ClearKeypadBuffer = true
        };
    }

    private PosTerminalWorkflowResult ApplySelectedLineDiscountPercentCore(CartLine? selectedLine, decimal value)
    {
        if (selectedLine is null)
        {
            return Status("pos.status.selectCartLine");
        }

        return ApplySelectedLineDiscountPercent((CartLine)selectedLine, value);
    }

    private PosTerminalWorkflowResult ApplySelectedLineDiscountPercent(CartLine line, decimal value)
    {
        if (value > 100m)
        {
            return Status("pos.status.discountPercentOutOfRange");
        }

        if (!_cart.SetLineDiscountPercent(line, value))
        {
            return new PosTerminalWorkflowResult();
        }

        return Status("pos.status.lineDiscountUpdated") with
        {
            SelectedCartLine = line,
            ClearKeypadBuffer = true
        };
    }

    private bool TryGetSelectedLineKeypadValue(
        CartLine? selectedLine,
        string keypadBuffer,
        out CartLine line,
        out decimal value,
        out PosTerminalWorkflowResult failure)
    {
        value = 0m;

        if (selectedLine is null)
        {
            line = null!;
            failure = Status("pos.status.selectCartLine");
            return false;
        }

        line = selectedLine;
        if (!TryGetKeypadValue(keypadBuffer, out value))
        {
            failure = Status("pos.status.invalidKeypadValue");
            return false;
        }

        failure = new PosTerminalWorkflowResult();
        return true;
    }

    private bool TryGetOrderDiscountKeypadValue(
        string keypadBuffer,
        out decimal value,
        out PosTerminalWorkflowResult failure)
    {
        value = 0m;

        if (_cart.IsEmpty)
        {
            failure = Status("pos.status.selectCartLine");
            return false;
        }

        if (!TryGetKeypadValue(keypadBuffer, out value))
        {
            failure = Status("pos.status.invalidKeypadValue");
            return false;
        }

        failure = new PosTerminalWorkflowResult();
        return true;
    }

    private static bool TryGetKeypadValue(string keypadBuffer, out decimal value)
    {
        return decimal.TryParse(keypadBuffer, NumberStyles.Number, CultureInfo.InvariantCulture, out value) &&
            value >= 0m;
    }

    private void BeginRemoteLookup(PosSessionState session, CartLine line, SellableItemDto item)
    {
        if (!session.IsOnline || _remoteLookupRefreshAsync is null)
        {
            return;
        }

        var snapshot = new RemoteLookupCartSnapshot(
            line,
            session.StoreCode,
            item.LookupCode,
            item.ProductCode,
            item.ReferenceCode);
        _ = RefreshRemoteLookupAsync(snapshot);
    }

    private async Task RefreshRemoteLookupAsync(RemoteLookupCartSnapshot snapshot)
    {
        using var timeoutCts = new CancellationTokenSource(RemoteLookupTimeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await _remoteLookupRefreshAsync!(
                snapshot.StoreCode,
                snapshot.LookupCode,
                timeoutCts.Token);
            stopwatch.Stop();

            if (result.Updated && result.Item is not null)
            {
                if (CanApplyRemoteItemToCartLine(snapshot, result.Item))
                {
                    var updated = _cart.UpdateLineFromRemote(snapshot.Line, result.Item);
                    ConsoleLog.Write(
                        "PosScan",
                        $"remote lookup cart update storeCode={snapshot.StoreCode} lookupCode={snapshot.LookupCode} productCode={snapshot.ProductCode} referenceCode={snapshot.ReferenceCode ?? "<null>"} updated={updated} elapsedMs={stopwatch.ElapsedMilliseconds}");
                }
                else
                {
                    ConsoleLog.Write(
                        "PosScan",
                        $"remote lookup ignored for cart storeCode={snapshot.StoreCode} lookupCode={snapshot.LookupCode} localProductCode={snapshot.ProductCode} localReferenceCode={snapshot.ReferenceCode ?? "<null>"} remoteProductCode={result.Item.ProductCode} remoteReferenceCode={result.Item.ReferenceCode ?? "<null>"} elapsedMs={stopwatch.ElapsedMilliseconds}");
                }
            }
            else if (result.Deleted)
            {
                ConsoleLog.Write(
                    "PosScan",
                    $"remote lookup deleted local cache only storeCode={result.StoreCode} lookupCode={result.LookupCode} deletedCount={result.DeletedCount} elapsedMs={stopwatch.ElapsedMilliseconds}");
            }

            var catalogItems = await ReloadCatalogAsync(CancellationToken.None);
            CatalogReloaded?.Invoke(this, new PosTerminalCatalogReloadedEventArgs(catalogItems));
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            stopwatch.Stop();
            ConsoleLog.Write(
                "PosScan",
                $"remote lookup timeout storeCode={snapshot.StoreCode} lookupCode={snapshot.LookupCode} timeoutMs={RemoteLookupTimeout.TotalMilliseconds:0} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ConsoleLog.Write(
                "PosScan",
                $"remote lookup failed storeCode={snapshot.StoreCode} lookupCode={snapshot.LookupCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
        }
    }

    private async Task<IReadOnlyList<SellableItemDto>> ReloadCatalogAsync(CancellationToken cancellationToken)
    {
        return _reloadCatalogAsync is null
            ? _priceIndex.Items
            : await _reloadCatalogAsync(cancellationToken);
    }

    private static bool CanApplyRemoteItemToCartLine(RemoteLookupCartSnapshot snapshot, SellableItemDto item)
    {
        return EqualsIdentity(snapshot.StoreCode, item.StoreCode) &&
            EqualsIdentity(snapshot.ProductCode, item.ProductCode) &&
            EqualsIdentity(snapshot.ReferenceCode, item.ReferenceCode);
    }

    private static bool EqualsIdentity(string? left, string? right)
    {
        return string.Equals(NormalizeIdentity(left), NormalizeIdentity(right), StringComparison.Ordinal);
    }

    private static string NormalizeIdentity(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static bool IsExactLookup(SellableItemDto item, string query)
    {
        var normalized = query.Trim();
        return string.Equals(item.Barcode, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.LookupCode, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.ItemNumber, normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static PosTerminalWorkflowResult Status(string key, params object[] args)
    {
        return new PosTerminalWorkflowResult
        {
            StatusKey = key,
            StatusArgs = args
        };
    }

    private static string FormatBool(bool value)
    {
        return value ? "true" : "false";
    }

    private sealed record RemoteLookupCartSnapshot(
        CartLine Line,
        string StoreCode,
        string LookupCode,
        string ProductCode,
        string? ReferenceCode);
}
