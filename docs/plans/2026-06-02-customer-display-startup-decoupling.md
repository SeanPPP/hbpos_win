# Customer Display Startup Decoupling Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Reduce main-window perceived stutter caused by customer display prewarm/open while preserving automatic customer display behavior and adding evidence-driven diagnostics.

**Architecture:** Keep business state in `MainViewModel` and `CustomerDisplayOrchestrator`, but move expensive customer-display window work out of the immediate POS-visible path. Treat customer-display prewarm, first open, advertisement refresh, and post-show startup work as separate timed phases so future logs can identify the exact source of delay.

**Tech Stack:** WPF / .NET 8, CommunityToolkit MVVM, xUnit, existing `ConsoleLog` diagnostics.

---

## Evidence From Current Log

- Startup prewarm costs about 0.5-0.6s.
- `CustomerDisplayWindowService.Show` and fullscreen layout cost about 0.6-0.8s after the main window is shown.
- Local catalog load is larger at about 13.8-14.6s, but it is a separate startup readiness gate.
- Advertisement API refresh takes about 3.8-4.7s and fails with invalid JSON; it is asynchronous, but it should stay isolated from customer-display open latency.

## Subagent Assignment

### Agent A: Startup Orchestration

**Ownership:**
- `src/Hbpos.Client.Wpf/MainWindow.xaml.cs`
- `src/Hbpos.Client.Wpf/ViewModels/MainViewModel.cs`
- `tests/Hbpos.Client.Tests/MainViewModelScannerTests.cs`

**Questions:**
- Should `PrewarmCustomerDisplay()` stay inside `InitializePosExperienceAsync()` or move to a dispatcher-idle delayed phase?
- Should automatic `OpenCustomerDisplayWindow(owner)` run immediately in `ContinueStartupAfterShownCoreAsync()` or after main-window idle/render stabilization?
- How do we preserve existing behavior for preview mode, registration activation, and manual customer-display commands?

**Deliverable:**
- A minimal startup scheduling patch plus tests proving POS navigation is not blocked by customer-display prewarm/open.

### Agent B: Customer Display Window Service

**Ownership:**
- `src/Hbpos.Client.Wpf/Services/CustomerDisplayWindowService.cs`
- `src/Hbpos.Client.Wpf/Views/Windows/CustomerDisplayWindow.xaml`
- `src/Hbpos.Client.Wpf/Views/Windows/CustomerDisplayWindow.xaml.cs`
- `src/Hbpos.Client.Wpf/Views/Screens/CustomerDisplayView.xaml`
- `src/Hbpos.Client.Wpf/Views/Screens/CustomerDisplayView.xaml.cs`
- `tests/Hbpos.Client.Tests/CustomerDisplayWindowServiceTests.cs`

**Questions:**
- Can prewarm create a lighter window shell first and defer layout-heavy content until actual show?
- Can `RefreshContentLayout()` and owner reactivation be delayed to idle priority without changing visible behavior?
- Are Normal and Fullscreen mode transitions still deterministic after deferring layout work?

**Deliverable:**
- A window-service patch that reduces synchronous show/apply-mode time, with layout-plan tests or focused WPF-safe tests.

### Agent C: Advertisement Refresh Isolation

**Ownership:**
- `src/Hbpos.Client.Wpf/Services/CustomerDisplayOrchestrator.cs`
- advertisement API client classes if needed
- `tests/Hbpos.Client.Tests/CustomerDisplayOrchestratorTests.cs`

**Questions:**
- Confirm advertisement refresh never blocks customer-display `SetMode`.
- Ensure invalid JSON is logged at API/client boundary with enough URL/store context.
- Avoid high-frequency cart-change logging and keep refresh failures non-blocking.

**Deliverable:**
- A small isolation/correctness patch only if needed; otherwise a review note saying current behavior is acceptable after log noise suppression.

## Implementation Tasks

### Task 1: Add Failing Startup Decoupling Test

**Files:**
- Modify: `tests/Hbpos.Client.Tests/MainViewModelScannerTests.cs`

**Step 1: Write a test proving customer-display prewarm does not block POS startup.**

Create a fake `ICustomerDisplayWindowService` whose `Prewarm` can be blocked by `TaskCompletionSource` or delayed through an injected scheduler seam. The test should initialize an authorized `MainViewModel`, release catalog load, and assert the POS screen becomes active before the customer-display prewarm completes.

**Step 2: Run the targeted test and confirm it fails.**

Run:

```powershell
dotnet test tests\Hbpos.Client.Tests\Hbpos.Client.Tests.csproj --filter "CustomerDisplay|ContinueStartupAfterShownAsync" --artifacts-path .artifacts\customer-display-decoupling
```

Expected: the new test fails until startup prewarm is moved off the hot path.

### Task 2: Move Startup Prewarm To Deferred Idle Work

**Files:**
- Modify: `src/Hbpos.Client.Wpf/ViewModels/MainViewModel.cs`
- Test: `tests/Hbpos.Client.Tests/MainViewModelScannerTests.cs`

**Step 1: Replace synchronous `PrewarmCustomerDisplay()` in `InitializePosExperienceAsync()` with a deferred method.**

The deferred method should:
- skip preview mode;
- run only once per POS experience;
- use the current `CustomerDisplay`, `Session`, and cart snapshot;
- log start/completion/failure;
- not block `BeginStartupCatalogIndexLoadAsync()` or `NavigateFromStartup(...)`.

**Step 2: Preserve manual open behavior.**

If the cashier manually opens customer display before deferred prewarm completes, `SetCustomerDisplayWindowMode` should still load cart state and call `SetMode` normally.

**Step 3: Run targeted tests.**

Expected: existing customer-display tests pass, and the new test proves POS startup no longer waits for customer-display prewarm.

### Task 3: Delay Automatic First Open Until Main Window Is Idle

**Files:**
- Modify: `src/Hbpos.Client.Wpf/MainWindow.xaml.cs`
- Modify: `src/Hbpos.Client.Wpf/ViewModels/MainViewModel.cs`
- Test: `tests/Hbpos.Client.Tests/MainViewModelScannerTests.cs`

**Step 1: Add a small idle/render delay before automatic customer-display open.**

Candidate flow:
- `ContinueStartupAfterShownCoreAsync()` waits for `DispatcherPriority.ContextIdle`;
- it then starts customer-display open as a separate task;
- special-products preload, connectivity, and catalog sync are not delayed by customer-display open.

**Step 2: Keep errors contained.**

Customer-display open failure should log and update status if appropriate, but should not stop connectivity timer or initial catalog sync.

**Step 3: Test behavior.**

Assert automatic open still requests `Fullscreen` when a second display exists, and that repeated `ContinueStartupAfterShownAsync` calls still only schedule one open.

### Task 4: Reduce Window Apply-Mode Synchronous Work

**Files:**
- Modify: `src/Hbpos.Client.Wpf/Services/CustomerDisplayWindowService.cs`
- Possibly modify: `src/Hbpos.Client.Wpf/Views/Windows/CustomerDisplayWindow.xaml.cs`
- Test: `tests/Hbpos.Client.Tests/CustomerDisplayWindowServiceTests.cs`

**Step 1: Keep mode semantics unchanged.**

Do not change:
- closed mode;
- no owner;
- no second display;
- Normal -> Fullscreen -> Closed cycling;
- owner activation behavior.

**Step 2: Move layout refresh to idle where safe.**

`window.RefreshContentLayout()` already uses `Dispatcher.BeginInvoke`. Verify whether `SetTitleBarVisible` calls cause immediate layout work; if so, defer nonessential refresh after `Show`.

**Step 3: Add tests around layout plan, not pixel rendering.**

Use existing `GetLayoutPlan` tests for deterministic mode behavior. Avoid brittle WPF visual assertions unless there is already a pattern.

### Task 5: Isolate Advertisement Refresh Failures

**Files:**
- Modify: `src/Hbpos.Client.Wpf/Services/CustomerDisplayOrchestrator.cs`
- Possibly modify advertisement API client classes
- Test: `tests/Hbpos.Client.Tests/CustomerDisplayOrchestratorTests.cs`

**Step 1: Prove `SetMode` does not await advertisement refresh.**

Add or update a test where advertisement API blocks, then assert `SetMode` returns immediately and window service receives the mode request.

**Step 2: Keep invalid JSON diagnostics at API boundary.**

If current API logging lacks endpoint/store context, add it there. Keep orchestrator failure log concise.

**Step 3: Do not log cache hits or cart-change refresh skips.**

Avoid synchronous `ConsoleLog.Write` in high-frequency cart-change paths.

### Task 6: Integration Verification

**Files:**
- No code ownership; main agent only.

**Step 1: Run targeted tests.**

```powershell
dotnet test tests\Hbpos.Client.Tests\Hbpos.Client.Tests.csproj --filter "CustomerDisplay|ContinueStartupAfterShownAsync" --artifacts-path .artifacts\customer-display-decoupling
```

**Step 2: Run broader client tests if targeted tests pass.**

```powershell
dotnet test tests\Hbpos.Client.Tests\Hbpos.Client.Tests.csproj --artifacts-path .artifacts\client-tests
```

**Step 3: Manual log validation.**

Start the app with `HBPOS_CLIENT_LOG_FILE` configured, then verify:
- POS screen becomes visible before deferred customer-display prewarm/open completes;
- logs show separate `startup prewarm`, `post-show open`, `window show`, `window apply-mode`, and advertisement refresh timings;
- cart changes do not produce customer-display log spam.

## Review Gates

- Reviewer 1: Check startup behavior did not regress registration, preview mode, scanner readiness, or manual customer-display commands.
- Reviewer 2: Check window-service mode semantics are unchanged.
- Reviewer 3: Check logging does not add synchronous high-frequency work.

## Rollback Plan

If deferred prewarm/open introduces a visible flicker or missed customer display opening, keep the diagnostics and revert only the scheduling changes. The current logging already proves where the remaining cost is.
