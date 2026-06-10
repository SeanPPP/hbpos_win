using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf.Services;

public interface ICardRecoveryResultDialogService
{
    event EventHandler<CardRecoveryResultDialogViewModel>? DialogRequested;

    void Show(CardRecoveryResultDialogViewModel dialog);
}

public sealed class CardRecoveryResultDialogService : ICardRecoveryResultDialogService
{
    public event EventHandler<CardRecoveryResultDialogViewModel>? DialogRequested;

    public void Show(CardRecoveryResultDialogViewModel dialog)
    {
        DialogRequested?.Invoke(this, dialog);
    }
}
