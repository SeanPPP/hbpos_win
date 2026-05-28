using System.Collections.Concurrent;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class PosTerminalCashPaymentViewModelTests
{
    [Fact]
    public async Task Pos_terminal_open_history_command_invokes_navigation_callback()
    {
        var opened = false;
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            onOpenHistoryAsync: () =>
            {
                opened = true;
                return Task.CompletedTask;
            });

        await viewModel.OpenHistoryCommand.ExecuteAsync(null);

        Assert.True(opened);
    }

    [Fact]
    public async Task Pos_terminal_page_navigation_commands_invoke_callbacks()
    {
        var settingsOpened = false;
        var customerDisplayOpened = false;
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            onOpenSettingsAsync: () =>
            {
                settingsOpened = true;
                return Task.CompletedTask;
            },
            onOpenCustomerDisplay: () => customerDisplayOpened = true);

        await viewModel.OpenSettingsCommand.ExecuteAsync(null);
        viewModel.OpenCustomerDisplayCommand.Execute(null);

        Assert.True(settingsOpened);
        Assert.True(customerDisplayOpened);
    }

    [Fact]
    public void Pos_terminal_scans_exact_barcode_into_cart()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-101", "Sparkling Water", "930101", PriceSourceKind.StoreRetailPrice, 2.5m, itemNumber: "ITEM-101", productImage: "https://images.example/sparkling-water.jpg")]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930101";
        viewModel.ScanCommand.Execute(null);

        Assert.Empty(viewModel.ScanText);
        Assert.Equal(2.5m, viewModel.ActualAmount);
        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Sparkling Water", line.DisplayName);
        Assert.Equal("ITEM-101", line.ItemNumber);
        Assert.Equal("https://images.example/sparkling-water.jpg", line.ProductImage);
        Assert.Same(line, viewModel.SelectedCartLine);
        Assert.Equal("StoreRetailPrice", line.PriceSourceLabel);
    }

    [Fact]
    public void Pos_terminal_scan_command_maps_workflow_result_to_ui_state()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var matchedItem = CreateItem("SKU-101A", "Workflow Match", "930101A", PriceSourceKind.StoreRetailPrice, 2.2m);
        var workflow = new FakePosTerminalWorkflowService
        {
            ProcessScanResult = new PosTerminalWorkflowResult
            {
                StatusKey = "pos.status.multipleMatches",
                StatusArgs = [1],
                Matches = [matchedItem],
                SelectedItem = matchedItem,
                MatchesPopupOpen = true,
                TouchKeyboardOpen = false
            }
        };
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            workflowService: workflow)
        {
            ScanText = "930101A",
            IsTouchKeyboardOpen = true
        };

        viewModel.ScanCommand.Execute(null);

        Assert.Equal("930101A", workflow.LastProcessScanText);
        Assert.True(workflow.LastProcessScanPreferExactLookup is false);
        var match = Assert.Single(viewModel.Matches);
        Assert.Equal("Workflow Match", match.DisplayName);
        Assert.Same(match, viewModel.SelectedItem);
        Assert.True(viewModel.IsMatchesPopupOpen);
        Assert.False(viewModel.IsTouchKeyboardOpen);
        Assert.Equal("pos.status.multipleMatches", viewModel.StatusMessage);
    }

    [Fact]
    public void Pos_terminal_open_payment_obeys_workflow_guard_result()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem("SKU-101B", "Guarded Match", "930101B", PriceSourceKind.StoreRetailPrice, 3.3m));
        var deniedWorkflow = new FakePosTerminalWorkflowService
        {
            GuardPaymentResult = new PosTerminalWorkflowResult
            {
                StatusKey = "cart.status.zeroPriceItem",
                PaymentAllowed = false
            }
        };
        var deniedViewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            cart,
            Session,
            onOpenPayment: null,
            workflowService: deniedWorkflow);
        deniedViewModel.RefreshCart();

        var deniedRaised = false;
        deniedViewModel.PaymentRequested += (_, _) => deniedRaised = true;

        deniedViewModel.OpenPaymentCommand.Execute(null);

        Assert.False(deniedRaised);
        Assert.Equal("cart.status.zeroPriceItem", deniedViewModel.StatusMessage);

        var allowedWorkflow = new FakePosTerminalWorkflowService
        {
            GuardPaymentResult = new PosTerminalWorkflowResult { PaymentAllowed = true }
        };
        var allowedViewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            cart,
            Session,
            onOpenPayment: null,
            workflowService: allowedWorkflow);
        allowedViewModel.RefreshCart();

        var allowedRaised = false;
        allowedViewModel.PaymentRequested += (_, _) => allowedRaised = true;

        allowedViewModel.OpenPaymentCommand.Execute(null);

        Assert.True(allowedRaised);
    }

    [Fact]
    public void Pos_terminal_selects_the_latest_added_cart_line()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(
        [
            CreateItem("SKU-121", "First Item", "930121", PriceSourceKind.StoreRetailPrice, 1.5m),
            CreateItem("SKU-122", "Second Item", "930122", PriceSourceKind.StoreRetailPrice, 2.5m)
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930121";
        viewModel.ScanCommand.Execute(null);
        var firstLine = Assert.Single(viewModel.CartLines);

        viewModel.ScanText = "930122";
        viewModel.ScanCommand.Execute(null);

        var secondLine = Assert.Single(viewModel.CartLines, line => line.LookupCode == "930122");
        Assert.NotSame(firstLine, secondLine);
        Assert.Same(secondLine, viewModel.SelectedCartLine);
    }

    [Fact]
    public void Pos_terminal_reveals_cart_line_added_outside_pos_view_model()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem("SKU-131", "Special Item", "930131", PriceSourceKind.StoreRetailPrice, 3.5m));
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            cart,
            Session,
            onOpenPayment: null);

        viewModel.RevealCartLine(line);

        Assert.Contains(line, viewModel.CartLines);
        Assert.Same(line, viewModel.SelectedCartLine);
    }

    [Fact]
    public void Pos_terminal_keeps_touch_keyboard_and_main_keypad_buffers_separate()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.NumberInputCommand.Execute("A");
        viewModel.NumberInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("9");
        viewModel.KeypadInputCommand.Execute(".");
        viewModel.KeypadInputCommand.Execute("5");

        Assert.Equal("A1", viewModel.ScanText);
        Assert.Equal("9.5", viewModel.KeypadBuffer);

        viewModel.KeypadInputCommand.Execute("Clear");

        Assert.Equal("A1", viewModel.ScanText);
        Assert.Empty(viewModel.KeypadBuffer);
    }

    [Fact]
    public void Pos_terminal_open_item_command_requires_positive_keypad_amount()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("OPEN-SKU", "Open Item", "OPENITEM", PriceSourceKind.StoreRetailPrice, 0m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        Assert.False(viewModel.AddOpenItemCommand.CanExecute(null));

        viewModel.KeypadInputCommand.Execute("0");

        Assert.False(viewModel.AddOpenItemCommand.CanExecute(null));

        viewModel.KeypadInputCommand.Execute("Clear");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("2");
        viewModel.KeypadInputCommand.Execute(".");
        viewModel.KeypadInputCommand.Execute("3");
        viewModel.KeypadInputCommand.Execute("4");

        Assert.True(viewModel.AddOpenItemCommand.CanExecute(null));
    }

    [Fact]
    public void Pos_terminal_open_item_adds_openitem_with_keypad_price_without_merging()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("OPEN-SKU", "Open Item", "OPENITEM", PriceSourceKind.StoreRetailPrice, 0m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("2");
        viewModel.KeypadInputCommand.Execute(".");
        viewModel.KeypadInputCommand.Execute("3");
        viewModel.KeypadInputCommand.Execute("4");
        viewModel.AddOpenItemCommand.Execute(null);
        viewModel.KeypadInputCommand.Execute("5");
        viewModel.AddOpenItemCommand.Execute(null);

        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(2, viewModel.CartLines.Count);
        Assert.All(viewModel.CartLines, line =>
        {
            Assert.Equal(CartLineKind.OpenItem, line.Kind);
            Assert.Equal("OPENITEM", line.LookupCodeNormalized);
            Assert.Equal(1m, line.Quantity);
        });
        Assert.Equal(12.34m, viewModel.CartLines[0].UnitPrice);
        Assert.Equal(5m, viewModel.CartLines[1].UnitPrice);
        Assert.Same(viewModel.CartLines[1], viewModel.SelectedCartLine);
    }

    [Fact]
    public void Pos_terminal_open_item_does_not_add_when_openitem_lookup_is_missing_or_duplicated()
    {
        var cart = new PosCartService();
        var missingIndex = new LocalSellableItemIndex();
        var missingViewModel = new PosTerminalViewModel(
            missingIndex,
            cart,
            Session,
            onOpenPayment: null);
        missingViewModel.KeypadInputCommand.Execute("1");

        missingViewModel.AddOpenItemCommand.Execute(null);

        Assert.Empty(cart.Lines);
        Assert.Equal("pos.status.noLocalMatch", missingViewModel.StatusMessage);

        var duplicateIndex = new LocalSellableItemIndex();
        duplicateIndex.ReplaceAll([
            CreateItem("OPEN-SKU-1", "Open Item 1", "OPENITEM", PriceSourceKind.StoreRetailPrice, 0m),
            CreateItem("OPEN-SKU-2", "Open Item 2", "OPENITEM", PriceSourceKind.StoreRetailPrice, 0m)
        ]);
        var duplicateViewModel = new PosTerminalViewModel(
            duplicateIndex,
            cart,
            Session,
            onOpenPayment: null);
        duplicateViewModel.KeypadInputCommand.Execute("1");

        duplicateViewModel.AddOpenItemCommand.Execute(null);

        Assert.Empty(cart.Lines);
        Assert.Equal("pos.status.multipleMatches", duplicateViewModel.StatusMessage);
    }

    [Fact]
    public void Pos_terminal_touch_keyboard_enter_closes_keyboard_after_search()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-109", "Match Tea", "930109", PriceSourceKind.StoreRetailPrice, 4.8m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null)
        {
            IsTouchKeyboardOpen = true,
            ScanText = "930109",
        };

        viewModel.NumberInputCommand.Execute("Enter");

        Assert.False(viewModel.IsTouchKeyboardOpen);
        Assert.Empty(viewModel.ScanText);
        Assert.Single(viewModel.CartLines);
    }

    [Fact]
    public void Pos_terminal_touch_keyboard_typing_keeps_keyboard_open()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null)
        {
            IsTouchKeyboardOpen = true,
        };

        viewModel.NumberInputCommand.Execute("A");
        viewModel.NumberInputCommand.Execute("1");

        Assert.True(viewModel.IsTouchKeyboardOpen);
        Assert.Equal("A1", viewModel.ScanText);
    }

    [Fact]
    public void Pos_terminal_adds_item_from_raw_scanner_event()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var scanner = new FakeRawScannerService();
        var localization = new LocalizationService();
        index.ReplaceAll([CreateItem("SKU-110", "Scanner Apple", "930110", PriceSourceKind.StoreRetailPrice, 1.8m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            localization: localization,
            rawScannerService: scanner);
        scanner.SetActivePage(PosTerminalViewModel.PageId);

        scanner.Emit("930110");

        Assert.Empty(viewModel.ScanText);
        Assert.Equal(1.8m, viewModel.ActualAmount);
        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Scanner Apple", line.DisplayName);
        Assert.False(viewModel.IsMatchesPopupOpen);
        Assert.Equal("Scan 930110: Added Scanner Apple", viewModel.StatusMessage);
    }

    [Fact]
    public void Pos_terminal_raw_scanner_uses_exact_lookup_without_keyword_search()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var scanner = new FakeRawScannerService();
        index.ReplaceAll([CreateItem("SKU-113", "Scanner Keyword Match", "930113", PriceSourceKind.StoreRetailPrice, 1.8m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            rawScannerService: scanner);
        scanner.SetActivePage(PosTerminalViewModel.PageId);

        scanner.Emit("Keyword");

        Assert.Empty(viewModel.CartLines);
        Assert.Empty(viewModel.Matches);
        Assert.False(viewModel.IsMatchesPopupOpen);
        Assert.Equal("Keyword", viewModel.ScanText);
    }

    [Fact]
    public void Pos_terminal_raw_scanner_duplicate_exact_lookup_requires_selection()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var scanner = new FakeRawScannerService();
        var localization = new LocalizationService();
        index.ReplaceAll(
        [
            CreateItem("SKU-114", "Scanner Apple Small", "930114", PriceSourceKind.StoreRetailPrice, 1.8m),
            CreateItem("SKU-115", "Scanner Apple Large", "930114", PriceSourceKind.StoreRetailPrice, 2.8m)
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            localization: localization,
            rawScannerService: scanner);
        scanner.SetActivePage(PosTerminalViewModel.PageId);

        scanner.Emit("930114");

        Assert.Empty(viewModel.CartLines);
        Assert.Equal(2, viewModel.Matches.Count);
        Assert.True(viewModel.IsMatchesPopupOpen);
        Assert.Equal("930114", viewModel.ScanText);
        Assert.Equal("Scan 930114: Found 2 items. Select one to add.", viewModel.StatusMessage);
    }

    [Fact]
    public void Pos_terminal_scan_success_plays_success_feedback_and_marks_success_status()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var feedback = new FakeUserFeedbackService();
        index.ReplaceAll([CreateItem("SKU-114A", "Feedback Apple", "930114A", PriceSourceKind.StoreRetailPrice, 1.8m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            userFeedbackService: feedback)
        {
            ScanText = "930114A"
        };

        viewModel.ScanCommand.Execute(null);

        Assert.Equal(StatusFeedbackKind.Success, viewModel.StatusFeedbackKind);
        Assert.Equal(1, viewModel.StatusPulseToken);
        Assert.Equal([UserFeedbackCue.ScanAdded], feedback.Cues);
    }

    [Fact]
    public void Pos_terminal_keyboard_fallback_scanner_shows_barcode_and_no_match_result()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var localization = new LocalizationService();
        var feedback = new FakeUserFeedbackService();
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            localization: localization,
            userFeedbackService: feedback);

        viewModel.ProcessScannerBarcode("930999", "keyboard-focus-fallback", "keyboard-fallback");

        Assert.Empty(viewModel.CartLines);
        Assert.Equal("930999", viewModel.ScanText);
        Assert.Equal("Scan 930999: No local item found. Checkout can continue offline.", viewModel.StatusMessage);
        Assert.Equal(StatusFeedbackKind.Error, viewModel.StatusFeedbackKind);
        Assert.Equal(1, viewModel.StatusPulseToken);
        Assert.Equal([UserFeedbackCue.ScanNoMatch], feedback.Cues);
    }

    [Fact]
    public void Pos_terminal_raw_scanner_multiple_match_plays_warning_feedback_once()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var scanner = new FakeRawScannerService();
        var localization = new LocalizationService();
        var feedback = new FakeUserFeedbackService();
        index.ReplaceAll(
        [
            CreateItem("SKU-114B", "Feedback Apple Small", "930114B", PriceSourceKind.StoreRetailPrice, 1.8m),
            CreateItem("SKU-114C", "Feedback Apple Large", "930114B", PriceSourceKind.StoreRetailPrice, 2.8m)
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            localization: localization,
            userFeedbackService: feedback,
            rawScannerService: scanner);
        scanner.SetActivePage(PosTerminalViewModel.PageId);

        scanner.Emit("930114B");

        Assert.Equal(StatusFeedbackKind.Warning, viewModel.StatusFeedbackKind);
        Assert.Equal(1, viewModel.StatusPulseToken);
        Assert.Equal([UserFeedbackCue.ScanMultipleMatches], feedback.Cues);
    }

    [Fact]
    public void Pos_terminal_raw_scanner_uses_lookup_code_when_metadata_barcode_is_shared()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var scanner = new FakeRawScannerService();
        index.ReplaceAll(
        [
            CreateItem("SKU-117", "Base Barcode Item", "9525812460346", PriceSourceKind.StoreRetailPrice, 1.8m, productBarcode: "9525812460346"),
            CreateItem("SKU-118", "Variant Metadata Item", "HB246-GJ-013", PriceSourceKind.StoreRetailPrice, 2.8m, productBarcode: "9525812460346")
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            rawScannerService: scanner)
        {
            IsTouchKeyboardOpen = true
        };
        scanner.SetActivePage(PosTerminalViewModel.PageId);

        scanner.Emit("9525812460346");

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Base Barcode Item", line.DisplayName);
        Assert.Empty(viewModel.ScanText);
        Assert.False(viewModel.IsMatchesPopupOpen);
        Assert.False(viewModel.IsTouchKeyboardOpen);
    }

    [Fact]
    public void Pos_terminal_select_match_adds_item_and_closes_popup_input_and_keyboard()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var first = CreateItem("SKU-119", "Scanner Apple Small", "930119", PriceSourceKind.StoreRetailPrice, 1.8m);
        var second = CreateItem("SKU-120", "Scanner Apple Large", "930119", PriceSourceKind.StoreRetailPrice, 2.8m);
        index.ReplaceAll([first, second]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null)
        {
            IsMatchesPopupOpen = true,
            IsTouchKeyboardOpen = true,
            ScanText = "930119"
        };

        viewModel.SelectMatchCommand.Execute(second);

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Scanner Apple Large", line.DisplayName);
        Assert.Same(line, viewModel.SelectedCartLine);
        Assert.Empty(viewModel.ScanText);
        Assert.False(viewModel.IsMatchesPopupOpen);
        Assert.False(viewModel.IsTouchKeyboardOpen);
    }

    [Fact]
    public void Pos_terminal_repeated_scan_keeps_same_cart_line_and_updates_quantity()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var scanner = new FakeRawScannerService();
        index.ReplaceAll([CreateItem("SKU-116", "Scanner Pear", "930116", PriceSourceKind.StoreRetailPrice, 2m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            rawScannerService: scanner);
        scanner.SetActivePage(PosTerminalViewModel.PageId);
        var firstScanAt = DateTimeOffset.Now;

        scanner.Emit("930116", firstScanAt);
        var firstLine = Assert.Single(viewModel.CartLines);
        scanner.Emit("930116", firstScanAt.AddMilliseconds(100));

        var line = Assert.Single(viewModel.CartLines);
        Assert.Same(firstLine, line);
        Assert.Equal(2m, line.Quantity);
        Assert.Same(line, viewModel.SelectedCartLine);
        Assert.Equal(4m, viewModel.ActualAmount);
    }

    [Fact]
    public void Pos_terminal_keypad_caps_decimal_input_at_two_places()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute(".");
        viewModel.KeypadInputCommand.Execute("2");
        viewModel.KeypadInputCommand.Execute("3");
        viewModel.KeypadInputCommand.Execute("4");

        Assert.Equal("1.23", viewModel.KeypadBuffer);
    }

    [Fact]
    public void Pos_terminal_keypad_quick_decimal_buttons_replace_decimal_places()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("2");
        viewModel.KeypadInputCommand.Execute("QuickHalf");

        Assert.Equal("12.50", viewModel.KeypadBuffer);

        viewModel.KeypadInputCommand.Execute("QuickNinetyNine");

        Assert.Equal("12.99", viewModel.KeypadBuffer);
    }

    [Fact]
    public void Pos_terminal_keypad_can_modify_selected_line_quantity_and_price()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-129", "Adjustable Tea", "930129", PriceSourceKind.StoreRetailPrice, 2m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930129";
        viewModel.ScanCommand.Execute(null);
        var line = Assert.Single(viewModel.CartLines);

        viewModel.KeypadInputCommand.Execute("2");
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);

        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(4m, viewModel.ActualAmount);
        Assert.Same(line, viewModel.SelectedCartLine);

        viewModel.KeypadInputCommand.Execute("3");
        viewModel.ModifySelectedLinePriceCommand.Execute(null);

        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(3m, line.UnitPrice);
        Assert.Equal(6m, viewModel.ActualAmount);
        Assert.Same(line, viewModel.SelectedCartLine);
    }

    [Fact]
    public void Pos_terminal_keypad_rejects_decimal_quantity_updates()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-136", "Integer Tea", "930136", PriceSourceKind.StoreRetailPrice, 2m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930136";
        viewModel.ScanCommand.Execute(null);
        var line = Assert.Single(viewModel.CartLines);

        viewModel.KeypadInputCommand.Execute("2");
        viewModel.KeypadInputCommand.Execute(".");
        viewModel.KeypadInputCommand.Execute("5");
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);

        Assert.Equal("2.5", viewModel.KeypadBuffer);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(2m, viewModel.ActualAmount);
        Assert.Equal("cart.status.quantityMustBeInteger", viewModel.StatusMessage);
    }

    [Fact]
    public void Pos_terminal_keypad_can_apply_selected_line_discount_amount_and_percent()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-130", "Discount Tea", "930130", PriceSourceKind.StoreRetailPrice, 10m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930130";
        viewModel.ScanCommand.Execute(null);
        var line = Assert.Single(viewModel.CartLines);

        viewModel.KeypadInputCommand.Execute("2");
        viewModel.ApplySelectedLineDiscountAmountCommand.Execute(null);

        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(2m, line.DiscountAmount);
        Assert.Equal(8m, viewModel.ActualAmount);

        viewModel.KeypadInputCommand.Execute("8");
        viewModel.KeypadInputCommand.Execute(".");
        viewModel.KeypadInputCommand.Execute("5");
        viewModel.ApplySelectedLineDiscountPercentCommand.Execute(null);

        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(0.85m, line.DiscountAmount);
        Assert.Equal("-8.5%", line.DiscountRateText);
        Assert.Equal(9.15m, viewModel.ActualAmount);
        Assert.Same(line, viewModel.SelectedCartLine);
    }

    [Fact]
    public void Pos_terminal_keypad_commands_ignore_missing_selection_or_invalid_input()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var feedback = new FakeUserFeedbackService();
        index.ReplaceAll([CreateItem("SKU-131", "Guarded Tea", "930131", PriceSourceKind.StoreRetailPrice, 5m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            userFeedbackService: feedback);

        viewModel.KeypadInputCommand.Execute("2");
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);

        Assert.Equal("2", viewModel.KeypadBuffer);
        Assert.Equal("pos.status.selectCartLine", viewModel.StatusMessage);

        viewModel.ScanText = "930131";
        viewModel.ScanCommand.Execute(null);
        var line = Assert.Single(viewModel.CartLines);
        viewModel.KeypadInputCommand.Execute("Clear");

        viewModel.ApplySelectedLineDiscountAmountCommand.Execute(null);

        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal(5m, viewModel.ActualAmount);
        Assert.Equal("pos.status.invalidKeypadValue", viewModel.StatusMessage);
        Assert.Equal(
            [UserFeedbackCue.OperationError, UserFeedbackCue.ScanAdded, UserFeedbackCue.OperationError],
            feedback.Cues);
    }

    [Fact]
    public void Pos_terminal_repeated_same_error_still_pulses_and_replays_feedback()
    {
        var feedback = new FakeUserFeedbackService();
        var viewModel = new PosTerminalViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            Session,
            onOpenPayment: null,
            userFeedbackService: feedback);

        viewModel.KeypadInputCommand.Execute("2");
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);

        Assert.Equal(StatusFeedbackKind.Error, viewModel.StatusFeedbackKind);
        Assert.Equal(2, viewModel.StatusPulseToken);
        Assert.Equal([UserFeedbackCue.OperationError, UserFeedbackCue.OperationError], feedback.Cues);
    }

    [Fact]
    public void Pos_terminal_non_error_line_update_does_not_play_extra_feedback()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var feedback = new FakeUserFeedbackService();
        index.ReplaceAll([CreateItem("SKU-129A", "Quiet Tea", "930129A", PriceSourceKind.StoreRetailPrice, 2m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            userFeedbackService: feedback)
        {
            ScanText = "930129A"
        };

        viewModel.ScanCommand.Execute(null);
        feedback.Cues.Clear();

        viewModel.KeypadInputCommand.Execute("3");
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);

        Assert.Empty(feedback.Cues);
        Assert.Equal(StatusFeedbackKind.Neutral, viewModel.StatusFeedbackKind);
    }

    [Fact]
    public void Pos_terminal_keypad_commands_show_friendly_messages_for_zero_quantity_and_invalid_discounts()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-134", "Friendly Tea", "930134", PriceSourceKind.StoreRetailPrice, 10m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930134";
        viewModel.ScanCommand.Execute(null);
        var line = Assert.Single(viewModel.CartLines);

        viewModel.KeypadInputCommand.Execute("0");
        viewModel.ModifySelectedLineQuantityCommand.Execute(null);

        Assert.Equal("0", viewModel.KeypadBuffer);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal("pos.status.quantityMustBePositive", viewModel.StatusMessage);

        viewModel.KeypadInputCommand.Execute("Clear");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.ApplySelectedLineDiscountAmountCommand.Execute(null);

        Assert.Equal("11", viewModel.KeypadBuffer);
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal("pos.status.discountAmountTooHigh", viewModel.StatusMessage);

        viewModel.KeypadInputCommand.Execute("Clear");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("0");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.ApplySelectedLineDiscountPercentCommand.Execute(null);

        Assert.Equal("101", viewModel.KeypadBuffer);
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal("pos.status.discountPercentOutOfRange", viewModel.StatusMessage);
    }

    [Fact]
    public void Pos_terminal_whole_order_discount_shows_friendly_messages_for_invalid_values()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-135", "Order Friendly Tea", "930135", PriceSourceKind.StoreRetailPrice, 10m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930135";
        viewModel.ScanCommand.Execute(null);

        viewModel.IsWholeOrderOperation = true;
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.ApplySelectedLineDiscountAmountCommand.Execute(null);

        Assert.True(viewModel.IsWholeOrderOperation);
        Assert.Equal("11", viewModel.KeypadBuffer);
        Assert.Equal(0m, viewModel.DiscountAmount);
        Assert.Equal("pos.status.discountAmountTooHigh", viewModel.StatusMessage);

        viewModel.KeypadInputCommand.Execute("Clear");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("0");
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.ApplySelectedLineDiscountPercentCommand.Execute(null);

        Assert.True(viewModel.IsWholeOrderOperation);
        Assert.Equal("101", viewModel.KeypadBuffer);
        Assert.Equal(0m, viewModel.DiscountAmount);
        Assert.Equal("pos.status.discountPercentOutOfRange", viewModel.StatusMessage);
    }

    [Fact]
    public void Pos_terminal_whole_order_toggle_applies_one_time_order_discount()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(
        [
            CreateItem("SKU-132", "Small Tea", "930132", PriceSourceKind.StoreRetailPrice, 10m),
            CreateItem("SKU-133", "Large Tea", "930133", PriceSourceKind.StoreRetailPrice, 30m)
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930132";
        viewModel.ScanCommand.Execute(null);
        viewModel.ScanText = "930133";
        viewModel.ScanCommand.Execute(null);

        viewModel.IsWholeOrderOperation = true;
        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("0");
        viewModel.ApplySelectedLineDiscountPercentCommand.Execute(null);

        var first = Assert.Single(viewModel.CartLines, line => line.LookupCode == "930132");
        var second = Assert.Single(viewModel.CartLines, line => line.LookupCode == "930133");
        Assert.False(viewModel.IsWholeOrderOperation);
        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(1m, first.DiscountAmount);
        Assert.Equal(3m, second.DiscountAmount);
        Assert.Equal(4m, viewModel.DiscountAmount);
        Assert.Equal(36m, viewModel.ActualAmount);
        Assert.Equal("pos.status.orderDiscountUpdated", viewModel.StatusMessage);
    }

    [Fact]
    public void Pos_terminal_blocks_payment_until_zero_price_line_is_fixed()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var feedback = new FakeUserFeedbackService();
        var openedPayment = false;
        index.ReplaceAll([CreateItem("SKU-140", "Zero Price Tea", "930140", PriceSourceKind.StoreRetailPrice, 0m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: () => openedPayment = true,
            userFeedbackService: feedback);

        viewModel.ScanText = "930140";
        viewModel.ScanCommand.Execute(null);
        var line = Assert.Single(viewModel.CartLines);

        viewModel.OpenPaymentCommand.Execute(null);

        Assert.False(openedPayment);
        Assert.True(line.HasZeroUnitPrice);
        Assert.Equal("cart.status.zeroPriceItem", viewModel.StatusMessage);
        Assert.Equal(StatusFeedbackKind.Error, viewModel.StatusFeedbackKind);
        Assert.Equal(2, viewModel.StatusPulseToken);
        Assert.Equal([UserFeedbackCue.ScanAdded, UserFeedbackCue.OperationError], feedback.Cues);

        viewModel.KeypadInputCommand.Execute("2");
        viewModel.ModifySelectedLinePriceCommand.Execute(null);
        viewModel.OpenPaymentCommand.Execute(null);

        Assert.True(openedPayment);
        Assert.False(line.HasZeroUnitPrice);
    }

    [Fact]
    public void Pos_terminal_quick_discount_applies_percent_to_selected_line()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-136", "Quick Discount Tea", "930136", PriceSourceKind.StoreRetailPrice, 10m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930136";
        viewModel.ScanCommand.Execute(null);
        var line = Assert.Single(viewModel.CartLines);
        viewModel.KeypadInputCommand.Execute("9");

        viewModel.ApplyQuickDiscountPercentCommand.Execute("20");

        Assert.Empty(viewModel.KeypadBuffer);
        Assert.False(viewModel.IsWholeOrderOperation);
        Assert.Equal(2m, line.DiscountAmount);
        Assert.Equal(8m, viewModel.ActualAmount);
        Assert.Equal("pos.status.lineDiscountUpdated", viewModel.StatusMessage);
    }

    [Fact]
    public void Pos_terminal_quick_discount_applies_percent_to_whole_order_and_turns_mode_off()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(
        [
            CreateItem("SKU-137", "Quick Small Tea", "930137", PriceSourceKind.StoreRetailPrice, 10m),
            CreateItem("SKU-138", "Quick Large Tea", "930138", PriceSourceKind.StoreRetailPrice, 30m)
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930137";
        viewModel.ScanCommand.Execute(null);
        viewModel.ScanText = "930138";
        viewModel.ScanCommand.Execute(null);
        viewModel.IsWholeOrderOperation = true;
        viewModel.KeypadInputCommand.Execute("9");

        viewModel.ApplyQuickDiscountPercentCommand.Execute("50");

        var first = Assert.Single(viewModel.CartLines, line => line.LookupCode == "930137");
        var second = Assert.Single(viewModel.CartLines, line => line.LookupCode == "930138");
        Assert.False(viewModel.IsWholeOrderOperation);
        Assert.Empty(viewModel.KeypadBuffer);
        Assert.Equal(5m, first.DiscountAmount);
        Assert.Equal(15m, second.DiscountAmount);
        Assert.Equal(20m, viewModel.ActualAmount);
        Assert.Equal("pos.status.orderDiscountUpdated", viewModel.StatusMessage);
    }

    [Fact]
    public void Pos_terminal_quick_discount_requires_selection_or_cart()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ApplyQuickDiscountPercentCommand.Execute("10");

        Assert.Equal("pos.status.selectCartLine", viewModel.StatusMessage);

        viewModel.IsWholeOrderOperation = true;
        viewModel.ApplyQuickDiscountPercentCommand.Execute("10");

        Assert.True(viewModel.IsWholeOrderOperation);
        Assert.Equal("pos.status.selectCartLine", viewModel.StatusMessage);
    }

    [Fact]
    public void Pos_terminal_remove_line_command_removes_entire_cart_line_and_recalculates_total()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(
        [
            CreateItem("SKU-111", "Apples", "930111", PriceSourceKind.StoreRetailPrice, 2m),
            CreateItem("SKU-112", "Bananas", "930112", PriceSourceKind.StoreRetailPrice, 3m)
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930111";
        viewModel.ScanCommand.Execute(null);
        viewModel.ScanText = "930111";
        viewModel.ScanCommand.Execute(null);
        viewModel.ScanText = "930112";
        viewModel.ScanCommand.Execute(null);

        Assert.Equal(2, viewModel.CartLines.Count);
        Assert.Equal(7m, viewModel.ActualAmount);

        var appleLine = Assert.Single(viewModel.CartLines, line => line.LookupCode == "930111");
        Assert.Equal(2m, appleLine.Quantity);
        viewModel.RemoveLineCommand.Execute(appleLine);

        var remaining = Assert.Single(viewModel.CartLines);
        Assert.Equal("Bananas", remaining.DisplayName);
        Assert.Equal(3m, viewModel.ActualAmount);
    }

    [Fact]
    public void Pos_terminal_line_quantity_commands_update_totals_selection_and_counts()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-123", "Touch Apples", "930123", PriceSourceKind.StoreRetailPrice, 2m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930123";
        viewModel.ScanCommand.Execute(null);
        var line = Assert.Single(viewModel.CartLines);

        Assert.Equal(1m, viewModel.CartItemQuantity);
        Assert.Equal(1, viewModel.CartSkuCount);

        viewModel.IncreaseLineCommand.Execute(line);

        Assert.Same(line, viewModel.SelectedCartLine);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(2m, viewModel.CartItemQuantity);
        Assert.Equal(1, viewModel.CartSkuCount);
        Assert.Equal(4m, viewModel.ActualAmount);

        viewModel.DecreaseLineCommand.Execute(line);

        Assert.Same(line, viewModel.SelectedCartLine);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(1m, viewModel.CartItemQuantity);
        Assert.Equal(1, viewModel.CartSkuCount);
        Assert.Equal(2m, viewModel.ActualAmount);

        viewModel.DecreaseLineCommand.Execute(line);

        Assert.Empty(viewModel.CartLines);
        Assert.Null(viewModel.SelectedCartLine);
        Assert.Equal(0m, viewModel.CartItemQuantity);
        Assert.Equal(0, viewModel.CartSkuCount);
        Assert.Equal(0m, viewModel.ActualAmount);
    }

    [Fact]
    public void Pos_terminal_cart_operations_write_diagnostic_logs()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var logs = new ConcurrentQueue<string>();
        var paymentOpenCount = 0;
        index.ReplaceAll([CreateItem("SKU-129", "Logged Apples", "930129", PriceSourceKind.StoreRetailPrice, 2m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: () => paymentOpenCount++);

        using var logCapture = CaptureClientLog(logs);
        viewModel.ScanText = "930129";
        viewModel.ScanCommand.Execute(null);
        var line = Assert.Single(viewModel.CartLines);
        viewModel.IncreaseLineCommand.Execute(line);
        viewModel.OpenPaymentCommand.Execute(null);

        Assert.Equal(1, paymentOpenCount);
        Assert.True(HasLog(logs, "[CartPerf]"));
        Assert.True(HasLog(logs, "operation=cart-changed"));
        Assert.True(HasLog(logs, "operation=scan-auto-add"));
        Assert.True(HasLog(logs, "operation=increase-line"));
        Assert.True(HasLog(logs, "operation=open-payment"));
        Assert.True(HasLog(logs, "syncCartElapsedMs="));
        Assert.True(HasLog(logs, "stateRefreshElapsedMs="));
        Assert.True(HasLog(logs, "totalElapsedMs="));
    }

    [Fact]
    public void Pos_terminal_counts_quantity_and_sku_lines_separately()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll(
        [
            CreateItem("SKU-124", "Repeated Item", "930124", PriceSourceKind.StoreRetailPrice, 2m),
            CreateItem("SKU-125", "Second Item", "930125", PriceSourceKind.StoreRetailPrice, 3m)
        ]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930124";
        viewModel.ScanCommand.Execute(null);
        viewModel.ScanText = "930124";
        viewModel.ScanCommand.Execute(null);
        viewModel.ScanText = "930125";
        viewModel.ScanCommand.Execute(null);

        Assert.Equal(3m, viewModel.CartItemQuantity);
        Assert.Equal(2, viewModel.CartSkuCount);
    }

    [Fact]
    public void Pos_terminal_clear_search_command_clears_input_and_closes_popups()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null)
        {
            ScanText = "apple",
            IsMatchesPopupOpen = true,
            IsTouchKeyboardOpen = true
        };

        viewModel.ClearSearchCommand.Execute(null);

        Assert.Empty(viewModel.ScanText);
        Assert.False(viewModel.IsMatchesPopupOpen);
        Assert.False(viewModel.IsTouchKeyboardOpen);
    }

    [Fact]
    public async Task Pos_terminal_keeps_local_add_when_remote_lookup_fails()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var item = CreateItem("SKU-104", "Local Coffee", "930104", PriceSourceKind.StoreRetailPrice, 6.5m);
        var remoteLookup = new TaskCompletionSource<RemoteLookupRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new ConcurrentQueue<string>();
        index.ReplaceAll([item]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            remoteLookupRefreshAsync: (_, _, _) => remoteLookup.Task);

        using var logCapture = CaptureClientLog(logs);

        viewModel.ScanText = "930104";
        viewModel.ScanCommand.Execute(null);

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Local Coffee", line.DisplayName);
        Assert.Equal(6.5m, viewModel.ActualAmount);

        remoteLookup.SetException(new InvalidOperationException("remote unavailable"));
        await WaitUntilAsync(() => HasLog(logs, "remote lookup failed"));

        line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Local Coffee", line.DisplayName);
        Assert.Equal(6.5m, viewModel.ActualAmount);
        Assert.DoesNotContain("Remote lookup failed", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pos_terminal_remote_deleted_does_not_remove_matching_cart_line()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var item = CreateItem("SKU-105", "Retired Snack", "930105", PriceSourceKind.StoreRetailPrice, 4.2m);
        var remoteLookup = new TaskCompletionSource<RemoteLookupRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new ConcurrentQueue<string>();
        index.ReplaceAll([item]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            remoteLookupRefreshAsync: (_, _, _) => remoteLookup.Task,
            reloadCatalogAsync: _ =>
            {
                index.ReplaceAll([]);
                return Task.FromResult<IReadOnlyList<SellableItemDto>>([]);
            });

        using var logCapture = CaptureClientLog(logs);

        viewModel.ScanText = "930105";
        viewModel.ScanCommand.Execute(null);

        Assert.Single(viewModel.CartLines);

        remoteLookup.SetResult(new RemoteLookupRefreshResult("S001", "930105", Found: false, Item: null, DeletedCount: 1));
        await WaitUntilAsync(() => HasLog(logs, "remote lookup deleted local cache only"));

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Retired Snack", line.DisplayName);
        Assert.Single(cart.Lines);
        Assert.Empty(viewModel.Matches);
    }

    [Fact]
    public async Task Pos_terminal_remote_lookup_updates_cart_only_when_identity_matches()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var localItem = CreateItem(
            "SKU-126",
            "Local Juice",
            "930126",
            PriceSourceKind.ProductBase,
            3m,
            referenceCode: "REF-126");
        var remoteItem = CreateItem(
            "SKU-126",
            "Remote Juice",
            "930126",
            PriceSourceKind.StoreRetailPrice,
            3.8m,
            referenceCode: "REF-126",
            productImage: "https://images.example/juice.jpg");
        var remoteLookup = new TaskCompletionSource<RemoteLookupRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.ReplaceAll([localItem]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            remoteLookupRefreshAsync: (_, _, _) => remoteLookup.Task);

        viewModel.ScanText = "930126";
        viewModel.ScanCommand.Execute(null);
        viewModel.ScanText = "930126";
        viewModel.ScanCommand.Execute(null);
        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal(2m, line.Quantity);

        remoteLookup.SetResult(new RemoteLookupRefreshResult("S001", "930126", Found: true, Item: remoteItem, DeletedCount: 0));
        await WaitUntilAsync(() => viewModel.CartLines.Single().DisplayName == "Remote Juice");

        line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Remote Juice", line.DisplayName);
        Assert.Equal(3.8m, line.UnitPrice);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal("https://images.example/juice.jpg", line.ProductImage);
    }

    [Fact]
    public async Task Pos_terminal_remote_lookup_ignores_cart_update_when_identity_differs()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var localItem = CreateItem(
            "SKU-127",
            "Local Snack",
            "930127",
            PriceSourceKind.ProductBase,
            2.5m,
            referenceCode: "REF-127");
        var remoteItem = CreateItem(
            "SKU-OTHER",
            "Remote Snack",
            "930127",
            PriceSourceKind.StoreRetailPrice,
            3.2m,
            referenceCode: "REF-127");
        var remoteLookup = new TaskCompletionSource<RemoteLookupRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new ConcurrentQueue<string>();
        index.ReplaceAll([localItem]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            remoteLookupRefreshAsync: (_, _, _) => remoteLookup.Task);

        using var logCapture = CaptureClientLog(logs);

        viewModel.ScanText = "930127";
        viewModel.ScanCommand.Execute(null);

        remoteLookup.SetResult(new RemoteLookupRefreshResult("S001", "930127", Found: true, Item: remoteItem, DeletedCount: 0));
        await WaitUntilAsync(() => HasLog(logs, "remote lookup ignored for cart"));

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Local Snack", line.DisplayName);
        Assert.Equal("SKU-127", line.ProductCode);
        Assert.Equal(2.5m, line.UnitPrice);
    }

    [Fact]
    public async Task Pos_terminal_remote_lookup_timeout_keeps_cart_unchanged()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var item = CreateItem("SKU-128", "Timeout Tea", "930128", PriceSourceKind.StoreRetailPrice, 5.5m);
        var logs = new ConcurrentQueue<string>();
        index.ReplaceAll([item]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            remoteLookupRefreshAsync: async (_, _, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                return new RemoteLookupRefreshResult("S001", "930128", Found: false, Item: null, DeletedCount: 1);
            });

        using var logCapture = CaptureClientLog(logs);

        viewModel.ScanText = "930128";
        viewModel.ScanCommand.Execute(null);

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Timeout Tea", line.DisplayName);

        await WaitUntilAsync(() => HasLog(logs, "remote lookup timeout"));

        line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Timeout Tea", line.DisplayName);
        Assert.Equal(5.5m, viewModel.ActualAmount);
        Assert.DoesNotContain("timeout", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pos_terminal_sync_command_refreshes_matches_and_index()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var oldItem = CreateItem("SKU-106", "Old Tea", "930106", PriceSourceKind.ProductBase, 3m);
        var syncedItem = CreateItem("SKU-107", "Synced Tea", "930107", PriceSourceKind.StoreRetailPrice, 3.5m);
        index.ReplaceAll([oldItem]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            syncCatalogAsync: _ =>
            {
                index.ReplaceAll([syncedItem]);
                return Task.FromResult<IReadOnlyList<SellableItemDto>>([syncedItem]);
            });
        viewModel.LoadMatches([oldItem]);

        await viewModel.SyncCommand.ExecuteAsync(null);

        var indexedItem = Assert.Single(index.Search("930107"));
        Assert.Equal("Synced Tea", indexedItem.DisplayName);
        var match = Assert.Single(viewModel.Matches);
        Assert.Equal("Synced Tea", match.DisplayName);
        Assert.Contains("completed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pos_terminal_reset_catalog_command_uses_reset_callback()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var oldItem = CreateItem("SKU-108", "Old Reset Tea", "930108", PriceSourceKind.ProductBase, 3m);
        var resetItem = CreateItem("SKU-109", "Reset Tea", "930109", PriceSourceKind.StoreRetailPrice, 4.5m);
        var syncCalled = false;
        var resetCalled = false;
        index.ReplaceAll([oldItem]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            syncCatalogAsync: _ =>
            {
                syncCalled = true;
                return Task.FromResult<IReadOnlyList<SellableItemDto>>([oldItem]);
            },
            resetCatalogAsync: _ =>
            {
                resetCalled = true;
                index.ReplaceAll([resetItem]);
                return Task.FromResult<IReadOnlyList<SellableItemDto>>([resetItem]);
            });
        viewModel.LoadMatches([oldItem]);

        await viewModel.ResetCatalogCommand.ExecuteAsync(null);

        Assert.False(syncCalled);
        Assert.True(resetCalled);
        var indexedItem = Assert.Single(index.Search("930109"));
        Assert.Equal("Reset Tea", indexedItem.DisplayName);
        var match = Assert.Single(viewModel.Matches);
        Assert.Equal("Reset Tea", match.DisplayName);
        Assert.Contains("reset completed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Payment_page_recalculates_change_from_cash_tenders()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-102", "Orange Juice", "930102", PriceSourceKind.ProductBase, 7.8m));
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session);

        viewModel.TenderAmountText = "10";
        await viewModel.AddTenderCommand.ExecuteAsync(null);

        Assert.Equal(2.2m, viewModel.ChangeDue);
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public void Payment_page_does_not_show_confirm_payment_from_keyboard_input_only()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-109", "Keyboard Cash Tea", "930109", PriceSourceKind.ProductBase, 7.8m));
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session);

        viewModel.TenderAmountText = "10";

        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.False(viewModel.IsConfirmPaymentVisible);
    }

    [Fact]
    public async Task Payment_page_refresh_cart_recalculates_change_after_cart_update()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-108", "Rice", "930108", PriceSourceKind.ProductBase, 7m));
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session);
        viewModel.TenderAmountText = "10";
        await viewModel.AddTenderCommand.ExecuteAsync(null);

        cart.UpdateLineFromRemote(CreateItem("SKU-108", "Rice", "930108", PriceSourceKind.StoreRetailPrice, 8m));
        viewModel.RefreshCart();

        Assert.Equal(2m, viewModel.ChangeDue);
        Assert.Equal(8m, viewModel.ActualAmount);
    }

    [Fact]
    public async Task Cash_payment_blocks_zero_price_cart_without_saving_or_clearing()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-141", "Zero Payment Tea", "930141", PriceSourceKind.StoreRetailPrice, 0m));
        var orders = new InMemoryOrderRepository();
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            orders,
            new InMemorySyncQueueRepository(),
            Session)
        {
            TenderAmountText = "0"
        };

        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.Equal("cart.status.zeroPriceItem", viewModel.StatusMessage);

        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.Null(orders.LastOrder);
        Assert.Single(cart.Lines);
        Assert.Equal("cart.status.zeroPriceItem", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Cash_payment_blocks_non_integer_quantity_cart_without_saving_or_clearing()
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem("SKU-142", "Fractional Tea", "930142", PriceSourceKind.StoreRetailPrice, 5m));
        SetUnsafeQuantity(line, 1.5m);
        var orders = new InMemoryOrderRepository();
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            orders,
            new InMemorySyncQueueRepository(),
            Session)
        {
            TenderAmountText = "10"
        };

        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.Equal("cart.status.quantityMustBeInteger", viewModel.StatusMessage);

        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.Null(orders.LastOrder);
        Assert.Single(cart.Lines);
        Assert.Equal(1.5m, line.Quantity);
        Assert.Equal("cart.status.quantityMustBeInteger", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Refund_mode_adds_negative_cash_tender_and_allows_confirmation()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND-1",
            null,
            "Refund Tea",
            "930142R",
            "ITEM-REFUND-1",
            null,
            1m,
            7.82m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VM-1",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var orders = new InMemoryOrderRepository();
        var viewModel = new PaymentViewModel(
            cart,
            new CashPaymentWorkflowService(
                new CashCheckoutService(),
                orders,
                new InMemorySyncQueueRepository()),
            Session);

        viewModel.PrepareForEntry(Session);

        Assert.Equal(PaymentEntryMode.Refund, viewModel.PaymentMode);
        Assert.True(viewModel.IsRefundMode);
        Assert.Equal("7.80", viewModel.TenderAmountText);
        Assert.Equal(7.82m, viewModel.RemainingAmount);
        Assert.True(viewModel.AddTenderCommand.CanExecute(null));

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Cash, tender.Method);
        Assert.Equal(-7.80m, tender.Amount);
        Assert.Equal(0m, viewModel.RemainingAmount);
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));

        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.NotNull(orders.LastOrder);
        Assert.All(orders.LastOrder!.Payments, payment => Assert.True(payment.Amount < 0m));
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public async Task Refund_mode_adds_negative_voucher_tender_without_source_voucher_code()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND-VOUCHER",
            null,
            "Refund Voucher Tea",
            "930142V",
            "ITEM-REFUND-VOUCHER",
            null,
            1m,
            8.5m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VM-VOUCHER",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var viewModel = new PaymentViewModel(
            cart,
            new CashPaymentWorkflowService(
                new CashCheckoutService(),
                new InMemoryOrderRepository(),
                new InMemorySyncQueueRepository()),
            Session);

        viewModel.PrepareForEntry(Session);

        Assert.Equal(PaymentEntryMode.Refund, viewModel.PaymentMode);
        Assert.Equal(string.Empty, viewModel.VoucherCodeText);
        Assert.True(viewModel.SelectVoucherCommand.CanExecute(null));

        await viewModel.SelectVoucherCommand.ExecuteAsync(null);

        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Voucher, tender.Method);
        Assert.Equal(-8.5m, tender.Amount);
        Assert.Equal("VOUCHER_REFUND_PENDING", tender.Reference);
    }

    [Fact]
    public async Task Refund_mode_card_button_uses_each_original_card_capacity()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND-CARD",
            null,
            "Refund Card Tea",
            "930142C",
            "ITEM-REFUND-CARD",
            null,
            1m,
            12m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VM-CARD",
            Guid.NewGuid(),
            Guid.NewGuid()));
        cart.AddReturnPaymentCapacities(
        [
            new OrderReturnPaymentCapacityDto(PaymentMethodKind.Card, 5m, 0m, 5m, "SQ:card-1"),
            new OrderReturnPaymentCapacityDto(PaymentMethodKind.Card, 7m, 0m, 7m, "SQ:card-2")
        ]);
        var cardTerminal = new ApprovedCardTerminalClient("CARD-REFUND");
        var viewModel = new PaymentViewModel(
            cart,
            new CashPaymentWorkflowService(
                new CashCheckoutService(),
                new InMemoryOrderRepository(),
                new InMemorySyncQueueRepository(),
                cardTerminalClient: cardTerminal),
            Session);

        viewModel.PrepareForEntry(Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        var firstTender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(-5m, firstTender.Amount);
        Assert.True(CardRefundReference.TryGetOriginalReference(firstTender.Reference, out var firstOriginal));
        Assert.Equal("SQ:card-1", firstOriginal);
        Assert.Equal(["SQ:card-1"], cardTerminal.RefundOriginalReferences);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.PaymentTenders.Count);
        var secondTender = viewModel.PaymentTenders[1];
        Assert.Equal(-7m, secondTender.Amount);
        Assert.True(CardRefundReference.TryGetOriginalReference(secondTender.Reference, out var secondOriginal));
        Assert.Equal("SQ:card-2", secondOriginal);
        Assert.Equal(["SQ:card-1", "SQ:card-2"], cardTerminal.RefundOriginalReferences);
        Assert.Equal(0m, viewModel.RemainingAmount);
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refund_mode_card_button_limits_original_card_to_that_orders_return_amount()
    {
        var originalOrderA = Guid.NewGuid();
        var originalOrderB = Guid.NewGuid();
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND-CARD-A",
            null,
            "Refund Card Tea A",
            "930142CA",
            "ITEM-REFUND-CARD-A",
            null,
            1m,
            10m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VM-CARD-A",
            originalOrderA,
            Guid.NewGuid()));
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-REFUND-CARD-B",
            null,
            "Refund Card Tea B",
            "930142CB",
            "ITEM-REFUND-CARD-B",
            null,
            1m,
            90m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VM-CARD-B",
            originalOrderB,
            Guid.NewGuid()));
        cart.AddReturnPaymentCapacities(
        [
            new OrderReturnPaymentCapacityDto(PaymentMethodKind.Card, 100m, 0m, 100m, "SQ:card-a", OriginalOrderGuid: originalOrderA),
            new OrderReturnPaymentCapacityDto(PaymentMethodKind.Card, 90m, 0m, 90m, "SQ:card-b", OriginalOrderGuid: originalOrderB)
        ]);
        var cardTerminal = new ApprovedCardTerminalClient("CARD-REFUND");
        var viewModel = new PaymentViewModel(
            cart,
            new CashPaymentWorkflowService(
                new CashCheckoutService(),
                new InMemoryOrderRepository(),
                new InMemorySyncQueueRepository(),
                cardTerminalClient: cardTerminal),
            Session);

        viewModel.PrepareForEntry(Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        var firstTender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(-10m, firstTender.Amount);
        Assert.True(CardRefundReference.TryGetOriginalReference(firstTender.Reference, out var firstOriginal));
        Assert.Equal("SQ:card-a", firstOriginal);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.PaymentTenders.Count);
        var secondTender = viewModel.PaymentTenders[1];
        Assert.Equal(-90m, secondTender.Amount);
        Assert.True(CardRefundReference.TryGetOriginalReference(secondTender.Reference, out var secondOriginal));
        Assert.Equal("SQ:card-b", secondOriginal);
        Assert.Equal(["SQ:card-a", "SQ:card-b"], cardTerminal.RefundOriginalReferences);
    }

    [Fact]
    public async Task Zero_settlement_mode_completes_without_tenders_and_saves_empty_payments()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-ZERO-1", "Zero Tea", "930ZERO1", PriceSourceKind.StoreRetailPrice, 5m));
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-ZERO-RET",
            null,
            "Zero Return Tea",
            "930ZERO2",
            "ITEM-ZERO-RET",
            null,
            1m,
            5m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-ZERO-1",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var orders = new InMemoryOrderRepository();
        var viewModel = new PaymentViewModel(
            cart,
            new CashPaymentWorkflowService(
                new CashCheckoutService(),
                orders,
                new InMemorySyncQueueRepository()),
            Session);

        viewModel.PrepareForEntry(Session);

        Assert.Equal(PaymentEntryMode.ZeroSettlement, viewModel.PaymentMode);
        Assert.True(viewModel.IsZeroSettlementMode);
        Assert.False(viewModel.AddTenderCommand.CanExecute(null));
        Assert.False(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.Empty(viewModel.PaymentTenders);

        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.NotNull(orders.LastOrder);
        Assert.Empty(orders.LastOrder!.Payments);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public async Task Cash_payment_confirmation_saves_order_snapshot_and_refreshes_pending_sync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-cash-vm-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var syncQueue = new SyncQueueRepository(store);
            var cart = new PosCartService();
            cart.AddItem(CreateItem("SKU-103", "Whole Milk", "930103", PriceSourceKind.StoreClearancePrice, 4.4m));
            var viewModel = new PaymentViewModel(cart, new CashCheckoutService(), orders, syncQueue, Session);
            PaymentCompletedEventArgs? completed = null;
            viewModel.PaymentCompleted += (_, args) => completed = args;

            await schema.InitializeAsync();
            viewModel.TenderAmountText = "5";
            await viewModel.AddTenderCommand.ExecuteAsync(null);
            await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

            Assert.NotNull(completed);
            Assert.Equal(0.6m, completed.ChangeAmount);
            Assert.Equal(4.4m, completed.Order.ActualAmount);
            Assert.Empty(cart.Lines);
            Assert.Equal(1, viewModel.PendingSyncCount);
            Assert.Equal(1, await syncQueue.CountPendingAsync());
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Payment_page_blocks_card_overpay_but_allows_cash_change()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-143", "Card And Cash Tea", "930143", PriceSourceKind.StoreRetailPrice, 7.8m));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-143"));
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        viewModel.TenderAmountText = "10";
        viewModel.SelectedPaymentMethod = PaymentMethodKind.Card;

        Assert.False(viewModel.AddTenderCommand.CanExecute(null));

        viewModel.TenderAmountText = "5";
        await viewModel.SelectCardCommand.ExecuteAsync(null);
        viewModel.TenderAmountText = "5";
        await viewModel.SelectCashCommand.ExecuteAsync(null);

        Assert.Equal(2.2m, viewModel.ChangeDue);
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_method_buttons_add_tenders_with_default_amounts()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-146", "Default Card Tea", "930146", PriceSourceKind.StoreRetailPrice, 7.83m));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-146"));
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        Assert.Equal(string.Empty, viewModel.TenderAmountText);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Card, tender.Method);
        Assert.Equal(7.83m, tender.Amount);
        Assert.Equal(0m, viewModel.RemainingAmount);
        Assert.Equal(string.Empty, viewModel.TenderAmountText);
    }

    [Fact]
    public async Task Payment_page_cash_button_uses_australian_cash_rounding_when_amount_is_empty()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-147", "Rounded Cash Tea", "930147", PriceSourceKind.StoreRetailPrice, 7.83m));
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session);

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Cash, tender.Method);
        Assert.Equal(7.85m, tender.Amount);
        Assert.Equal(0m, viewModel.RemainingAmount);
        Assert.Equal(0m, viewModel.ChangeDue);
    }

    [Fact]
    public async Task Payment_page_cash_button_adds_typed_amount_and_clears_input()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-148", "Typed Cash Tea", "930148", PriceSourceKind.StoreRetailPrice, 7.8m));
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session)
        {
            TenderAmountText = "10"
        };

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        Assert.Equal(10m, Assert.Single(viewModel.PaymentTenders).Amount);
        Assert.Equal(2.2m, viewModel.ChangeDue);
        Assert.Equal(string.Empty, viewModel.TenderAmountText);
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_card_button_rejects_typed_overpay()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-149", "Card Overpay Tea", "930149", PriceSourceKind.StoreRetailPrice, 7.8m));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-149"));
        var viewModel = new PaymentViewModel(cart, workflow, Session)
        {
            TenderAmountText = "10"
        };

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal("payment.status.cardExceedsRemaining", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_card_button_does_not_treat_cash_rounding_shortfall_as_paid()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-151", "Card Boundary Tea", "930151", PriceSourceKind.StoreRetailPrice, 7.82m));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-151"));
        var viewModel = new PaymentViewModel(cart, workflow, Session)
        {
            TenderAmountText = "7.80"
        };

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(0.02m, viewModel.RemainingAmount);
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_quick_cash_options_add_cash_tender()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-150", "Quick Cash Tea", "930150", PriceSourceKind.StoreRetailPrice, 4.4m));
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session);
        var quickFive = Assert.Single(viewModel.QuickCashAmounts.Where(option => option.Amount == 5m));

        await viewModel.QuickCashCommand.ExecuteAsync(quickFive);

        var tender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Cash, tender.Method);
        Assert.Equal(5m, tender.Amount);
        Assert.Equal(0.6m, viewModel.ChangeDue);
        Assert.Equal("$5", quickFive.Label);
    }

    [Fact]
    public async Task Payment_page_allows_only_one_tender_per_payment_method_and_reenables_after_remove()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-152", "Single Method Tea", "930152", PriceSourceKind.StoreRetailPrice, 15m));
        var workflow = new FakeCashPaymentWorkflowService();
        var viewModel = new PaymentViewModel(cart, workflow, Session)
        {
            TenderAmountText = "5"
        };

        await viewModel.SelectCashCommand.ExecuteAsync(null);

        var cashTender = Assert.Single(viewModel.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Cash, cashTender.Method);
        Assert.False(viewModel.SelectCashCommand.CanExecute(null));

        viewModel.TenderAmountText = "5";
        await viewModel.SelectCashCommand.ExecuteAsync(null);

        Assert.Single(viewModel.PaymentTenders);

        viewModel.RemoveTenderCommand.Execute(cashTender);

        Assert.Empty(viewModel.PaymentTenders);
        viewModel.TenderAmountText = "5";
        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_allows_only_one_card_and_one_voucher_tender()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-153", "Mixed Single Method Tea", "930153", PriceSourceKind.StoreRetailPrice, 20m));
        var workflow = new FakeCashPaymentWorkflowService();
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        viewModel.TenderAmountText = "5";
        await viewModel.SelectCardCommand.ExecuteAsync(null);
        viewModel.TenderAmountText = "5";
        viewModel.VoucherCodeText = "VOUCHER-153";
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.PaymentTenders.Count);
        Assert.Contains(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Card);
        Assert.Contains(viewModel.PaymentTenders, tender => tender.Method == PaymentMethodKind.Voucher);
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.SelectVoucherCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_locks_interactions_while_card_payment_is_pending()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-154", "Pending Card Tea", "930154", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        Assert.True(viewModel.IsCardPaymentInProgress);
        Assert.True(viewModel.IsPaymentInteractionLocked);
        Assert.False(viewModel.NumberInputCommand.CanExecute("1"));
        Assert.False(viewModel.SelectCashCommand.CanExecute(null));
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.SelectVoucherCommand.CanExecute(null));
        Assert.False(viewModel.QuickCashCommand.CanExecute(viewModel.QuickCashAmounts[0]));
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.Equal("payment.status.cardProcessing", viewModel.StatusMessage);

        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 10m, "CARD-154"),
            "payment.status.cardTenderAdded"));
        await paymentTask;

        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.Single(viewModel.PaymentTenders);
    }

    [Fact]
    public async Task Payment_page_manual_card_cancel_restores_interactions_without_adding_tender()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-155", "Cancelled Card Tea", "930155", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);
        var cancelledNavigation = false;
        viewModel.PaymentCancelled += (_, _) => cancelledNavigation = true;

        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        viewModel.CancelCommand.Execute(null);
        await paymentTask;

        Assert.False(cancelledNavigation);
        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.Equal("payment.status.cardCancelled", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_allows_second_cancel_during_late_card_approval_and_skips_tender()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-157", "Late Card Tea", "930157", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);
        var cancelledNavigation = false;
        viewModel.PaymentCancelled += (_, _) => cancelledNavigation = true;

        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        viewModel.CancelCommand.Execute(null);

        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.False(viewModel.SelectCardCommand.CanExecute(null));
        Assert.False(viewModel.AddTenderCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
        Assert.True(viewModel.CancelCommand.CanExecute(null));

        viewModel.CancelCommand.Execute(null);

        Assert.True(cancelledNavigation);

        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 10m, "CARD-LATE"),
            "payment.status.cardTenderAdded"));
        await paymentTask;

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal("payment.status.cardCancelled", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_prepare_for_entry_ignores_late_card_approval_from_previous_entry()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-161", "Stale Card Tea", "930161", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        var nextSession = Session with { DeviceCode = "POS-02" };
        viewModel.PrepareForEntry(nextSession);
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 10m, "CARD-STALE"),
            "payment.status.cardTenderAdded"));
        await paymentTask;

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal(nextSession, viewModel.Session);
        Assert.Equal("payment.status.ready", viewModel.StatusMessage);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_prepare_for_entry_after_second_cancel_allows_new_tender_before_late_card_result()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-162", "Reentry Tea", "930162", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);
        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        viewModel.CancelCommand.Execute(null);
        viewModel.CancelCommand.Execute(null);
        viewModel.PrepareForEntry(Session);

        Assert.True(viewModel.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.AddTenderCommand.CanExecute(null));

        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Success(
            new PaymentTender(PaymentMethodKind.Card, 10m, "CARD-LATE"),
            "payment.status.cardTenderAdded"));
        await paymentTask;

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal("payment.status.ready", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_manual_card_cancel_shows_unconfirmed_cancel_outcome()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-159", "Unconfirmed Cancel Tea", "930159", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        var paymentTask = viewModel.SelectCardCommand.ExecuteAsync(null);
        await workflow.AddTenderStarted.Task;

        viewModel.CancelCommand.Execute(null);
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Fail(
            "payment.status.cardDeclined",
            "ANZ Linkly cancellation outcome could not be confirmed."));
        await paymentTask;

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.Equal("ANZ Linkly cancellation outcome could not be confirmed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_card_terminal_cancel_result_does_not_add_tender()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-160", "Terminal Cancel Tea", "930160", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Fail(
            "payment.status.cardDeclined",
            "CANCELLED (C0)"));
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.Equal("payment.status.cardCancelled", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_square_card_cancel_result_uses_cancelled_status()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-160A", "Square Terminal Cancel Tea", "930160A", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Fail(
            "payment.status.cardDeclined",
            "Square checkout was canceled."));
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.False(viewModel.IsPaymentInteractionLocked);
        Assert.Equal("payment.status.cardCancelled", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_card_timeout_exception_restores_interactions_without_adding_tender()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-156", "Timed Out Card Tea", "930156", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderException = new OperationCanceledException()
        };
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.Equal("payment.status.cardTimedOut", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_card_timeout_failure_result_uses_timeout_status()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-158", "Timeout Result Card Tea", "930158", PriceSourceKind.StoreRetailPrice, 10m));
        var workflow = new FakeCashPaymentWorkflowService
        {
            AddTenderResult = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        workflow.AddTenderResult.SetResult(PaymentTenderAttemptResult.Fail(
            "payment.status.cardDeclined",
            "ANZ Linkly transaction timed out."));
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        await viewModel.SelectCardCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.False(viewModel.IsCardPaymentInProgress);
        Assert.True(viewModel.SelectCardCommand.CanExecute(null));
        Assert.Equal("payment.status.cardTimedOut", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_page_requires_voucher_code_before_adding_voucher()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-144", "Voucher Tea", "930144", PriceSourceKind.StoreRetailPrice, 5m));
        var viewModel = new PaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session);

        viewModel.TenderAmountText = "5";
        viewModel.SelectedPaymentMethod = PaymentMethodKind.Voucher;

        Assert.False(viewModel.AddTenderCommand.CanExecute(null));

        await viewModel.SelectVoucherCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal("payment.status.voucherCodeRequired", viewModel.StatusMessage);

        viewModel.VoucherCodeText = "ABC123";

        Assert.True(viewModel.AddTenderCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_page_prepare_for_entry_resets_tenders_and_pending_voucher_retry_state()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-145", "Retry Voucher Tea", "930145", PriceSourceKind.StoreRetailPrice, 5m));
        var workflow = new FakeCashPaymentWorkflowService();
        var viewModel = new PaymentViewModel(cart, workflow, Session);

        viewModel.TenderAmountText = "5";
        viewModel.VoucherCodeText = "ABC123";
        await viewModel.SelectVoucherCommand.ExecuteAsync(null);
        workflow.ThrowOnComplete = new PaymentUploadFailedException(Guid.NewGuid(), 5m, 0m, "upload failed");
        await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

        Assert.Equal("upload failed", viewModel.StatusMessage);
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));

        var updatedSession = Session with { PendingSyncCount = 3 };
        viewModel.PrepareForEntry(updatedSession);

        Assert.Empty(viewModel.PaymentTenders);
        Assert.Equal(string.Empty, viewModel.VoucherCodeText);
        Assert.Equal(PaymentMethodKind.Cash, viewModel.SelectedPaymentMethod);
        Assert.Equal(updatedSession.PendingSyncCount, viewModel.PendingSyncCount);
        Assert.Equal("5.00", viewModel.TenderAmountText);
        Assert.Equal("payment.status.ready", viewModel.StatusMessage);
        Assert.True(viewModel.AddTenderCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    private static PosSessionState Session => new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    private static SellableItemDto CreateItem(
        string productCode,
        string name,
        string barcode,
        PriceSourceKind priceSource,
        decimal price,
        string? referenceCode = null,
        string? itemNumber = null,
        string? productBarcode = null,
        string? productImage = null)
    {
        return new SellableItemDto(
            StoreCode: "S001",
            ProductCode: productCode,
            ReferenceCode: referenceCode,
            DisplayName: name,
            LookupCode: barcode,
            ItemNumber: itemNumber ?? productCode,
            Barcode: productBarcode ?? barcode,
            RetailPrice: price,
            PriceSource: priceSource,
            PriceSourceLabel: priceSource.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: productImage);
    }

    private static void SetUnsafeQuantity(CartLine line, decimal quantity)
    {
        var field = typeof(CartLine).GetField("_quantity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(line, quantity);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static IDisposable CaptureClientLog(ConcurrentQueue<string> lines)
    {
        void Capture(string line)
        {
            lines.Enqueue(line);
        }

        ConsoleLog.LineWritten += Capture;
        return new DisposableAction(() => ConsoleLog.LineWritten -= Capture);
    }

    private static bool HasLog(ConcurrentQueue<string> lines, string text)
    {
        return lines.Any(line => line.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class DisposableAction(Action dispose) : IDisposable
    {
        public void Dispose()
        {
            dispose();
        }
    }

    private sealed class InMemoryOrderRepository : ILocalOrderRepository
    {
        public LocalOrder? LastOrder { get; private set; }

        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            LastOrder = order;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return GetRecentOrdersAsync(take, cancellationToken);
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalOrder?>(LastOrder?.OrderGuid == orderGuid ? LastOrder : null);
        }
    }

    private sealed class InMemorySyncQueueRepository : ISyncQueueRepository
    {
        public Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1);
        }

        public Task<SyncQueueOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SyncQueueOverview(1, 0, 0, null));
        }

        public Task<IReadOnlyList<SyncQueueListItem>> GetActiveItemsAsync(int take = 20, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SyncQueueListItem>>([]);
        }
    }

    private sealed class FakePosTerminalWorkflowService : IPosTerminalWorkflowService
    {
        public event EventHandler<PosTerminalCatalogReloadedEventArgs>? CatalogReloaded;

        public PosTerminalWorkflowResult ProcessScanResult { get; set; } = new();

        public PosTerminalWorkflowResult AddSelectedItemResult { get; set; } = new();

        public PosTerminalWorkflowResult AddOpenItemResult { get; set; } = new();

        public PosTerminalWorkflowResult RemoveLineResult { get; set; } = new();

        public PosTerminalWorkflowResult IncreaseLineResult { get; set; } = new();

        public PosTerminalWorkflowResult DecreaseLineResult { get; set; } = new();

        public PosTerminalWorkflowResult ModifySelectedLineQuantityResult { get; set; } = new();

        public PosTerminalWorkflowResult ModifySelectedLinePriceResult { get; set; } = new();

        public PosTerminalWorkflowResult ApplySelectedLineDiscountAmountResult { get; set; } = new();

        public PosTerminalWorkflowResult ApplySelectedLineDiscountPercentResult { get; set; } = new();

        public PosTerminalWorkflowResult ApplyQuickDiscountPercentResult { get; set; } = new();

        public PosTerminalWorkflowResult ClearCartResult { get; set; } = new();

        public PosTerminalWorkflowResult GuardPaymentResult { get; set; } = new();

        public string? LastProcessScanText { get; private set; }

        public bool? LastProcessScanPreferExactLookup { get; private set; }

        public PosTerminalWorkflowResult ProcessScan(PosSessionState session, string scanText, bool preferExactLookup, string source, string? traceId = null)
        {
            LastProcessScanText = scanText;
            LastProcessScanPreferExactLookup = preferExactLookup;
            return ProcessScanResult;
        }

        public PosTerminalWorkflowResult AddSelectedItem(PosSessionState session, SellableItemDto item, bool clearScanText, bool closeMatchesPopup, string operation)
        {
            return AddSelectedItemResult;
        }

        public PosTerminalWorkflowResult AddOpenItem(PosSessionState session, string keypadBuffer)
        {
            return AddOpenItemResult;
        }

        public PosTerminalWorkflowResult RemoveLine(CartLine? line)
        {
            return RemoveLineResult;
        }

        public PosTerminalWorkflowResult IncreaseLine(CartLine? line)
        {
            return IncreaseLineResult;
        }

        public PosTerminalWorkflowResult DecreaseLine(CartLine? line)
        {
            return DecreaseLineResult;
        }

        public PosTerminalWorkflowResult ModifySelectedLineQuantity(CartLine? selectedLine, string keypadBuffer)
        {
            return ModifySelectedLineQuantityResult;
        }

        public PosTerminalWorkflowResult ModifySelectedLinePrice(CartLine? selectedLine, string keypadBuffer)
        {
            return ModifySelectedLinePriceResult;
        }

        public PosTerminalWorkflowResult ApplySelectedLineDiscountAmount(CartLine? selectedLine, string keypadBuffer, bool isWholeOrderOperation)
        {
            return ApplySelectedLineDiscountAmountResult;
        }

        public PosTerminalWorkflowResult ApplySelectedLineDiscountPercent(CartLine? selectedLine, string keypadBuffer, bool isWholeOrderOperation)
        {
            return ApplySelectedLineDiscountPercentResult;
        }

        public PosTerminalWorkflowResult ApplyQuickDiscountPercent(CartLine? selectedLine, string? value, bool isWholeOrderOperation)
        {
            return ApplyQuickDiscountPercentResult;
        }

        public PosTerminalWorkflowResult ClearCart()
        {
            return ClearCartResult;
        }

        public PosTerminalWorkflowResult GuardPayment()
        {
            return GuardPaymentResult;
        }

        public void RaiseCatalogReloaded(IReadOnlyList<SellableItemDto> catalogItems)
        {
            CatalogReloaded?.Invoke(this, new PosTerminalCatalogReloadedEventArgs(catalogItems));
        }
    }

    private sealed class FakeCashPaymentWorkflowService : ICashPaymentWorkflowService
    {
        public PaymentTender TenderToAdd { get; set; } = new(PaymentMethodKind.Voucher, 5m, "ABC123");

        public PaymentUploadFailedException? ThrowOnComplete { get; set; }

        public TaskCompletionSource? AddTenderStarted { get; set; }

        public TaskCompletionSource<PaymentTenderAttemptResult>? AddTenderResult { get; set; }

        public Exception? AddTenderException { get; set; }

        public bool IgnoreCancellation { get; set; }

        public bool TryParseTenderedAmount(string? amountTenderedText, out decimal tenderedAmount)
        {
            return decimal.TryParse(amountTenderedText, out tenderedAmount);
        }

        public decimal CalculateChange(string? amountTenderedText, decimal actualAmount)
        {
            return TryParseTenderedAmount(amountTenderedText, out var tenderedAmount)
                ? decimal.Round(tenderedAmount - actualAmount, 2, MidpointRounding.AwayFromZero)
                : 0m;
        }

        public decimal CalculateTenderedAmount(IReadOnlyList<PaymentTender> tenders)
        {
            return tenders.Sum(tender => tender.Amount);
        }

        public decimal CalculateRemainingAmount(decimal actualAmount, IReadOnlyList<PaymentTender> tenders)
        {
            return Math.Max(0m, actualAmount - CalculateTenderedAmount(tenders));
        }

        public decimal CalculateChange(IReadOnlyList<PaymentTender> tenders, decimal actualAmount)
        {
            return Math.Max(0m, CalculateTenderedAmount(tenders) - actualAmount);
        }

        public async Task<PaymentTenderAttemptResult> AddTenderAsync(
            PaymentMethodKind method,
            PosSessionState session,
            decimal actualAmount,
            IReadOnlyList<PaymentTender> currentTenders,
            string? amountText,
            string? referenceText = null,
            CancellationToken cancellationToken = default)
        {
            AddTenderStarted?.SetResult();

            if (AddTenderException is not null)
            {
                throw AddTenderException;
            }

            if (AddTenderResult is not null)
            {
                return IgnoreCancellation
                    ? await AddTenderResult.Task
                    : await AddTenderResult.Task.WaitAsync(cancellationToken);
            }

            var tender = TenderToAdd.Method == method
                ? TenderToAdd
                : new PaymentTender(method, decimal.TryParse(amountText, out var amount) ? amount : 5m, referenceText);

            return PaymentTenderAttemptResult.Success(tender, "payment.status.tenderAdded");
        }

        public Task<CashPaymentWorkflowResult> CompleteAsync(
            PosCartService cart,
            PosSessionState session,
            string? amountTenderedText,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<CashPaymentWorkflowResult> CompletePaymentAsync(
            PosCartService cart,
            PosSessionState session,
            IReadOnlyList<PaymentTender> tenders,
            decimal cashTenderedAmount,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnComplete is not null)
            {
                throw ThrowOnComplete;
            }

            throw new NotSupportedException();
        }

        public Task<CashPaymentWorkflowResult> RetryVoucherUploadAsync(
            Guid orderGuid,
            PosCartService cart,
            PosSessionState session,
            decimal tenderedAmount,
            decimal changeAmount,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeRawScannerService : IRawScannerService
    {
        private readonly Dictionary<string, Action<RawBarcodeScannedEventArgs>> _handlers = [];
        private string? _activePageId;

        public bool IsActive { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void Subscribe(string pageId, Action<RawBarcodeScannedEventArgs> handler)
        {
            _handlers[pageId] = handler;
        }

        public void Unsubscribe(string pageId)
        {
            _handlers.Remove(pageId);
        }

        public void SetActivePage(string? pageId)
        {
            _activePageId = pageId;
        }

        public void Start(IntPtr hwnd)
        {
            IsActive = true;
        }

        public void Stop()
        {
            IsActive = false;
        }

        public Task ResetBindingAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public IntPtr ProcessWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            return IntPtr.Zero;
        }

        public void Emit(string barcode, DateTimeOffset? scannedAt = null)
        {
            if (_activePageId is not null && _handlers.TryGetValue(_activePageId, out var handler))
            {
                handler(new RawBarcodeScannedEventArgs(barcode, "scanner-device", scannedAt ?? DateTimeOffset.Now));
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeUserFeedbackService : IUserFeedbackService
    {
        public List<UserFeedbackCue> Cues { get; } = [];

        public void Play(UserFeedbackCue cue)
        {
            Cues.Add(cue);
        }
    }

    private sealed class ApprovedCardTerminalClient(string reference) : ICardTerminalClient
    {
        public List<string?> RefundOriginalReferences { get; } = [];

        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(true, reference));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            RefundOriginalReferences.Add(originalReference);
            return Task.FromResult(new PaymentAuthorizationResult(true, $"REFUND:{originalReference}", AuthorizedAmount: amount));
        }
    }
}
