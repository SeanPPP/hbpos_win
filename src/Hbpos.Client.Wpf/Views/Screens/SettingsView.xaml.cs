using System.ComponentModel;
using System.Windows.Controls;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf.Views.Screens;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _viewModel;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel(DataContext as SettingsViewModel);
        AttachViewModel(DataContext as SettingsViewModel);
    }

    private void LinklyCloudPasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_viewModel is null ||
            sender is not PasswordBox passwordBox ||
            string.Equals(_viewModel.LinklyCloudPasswordText, passwordBox.Password, StringComparison.Ordinal))
        {
            return;
        }

        _viewModel.LinklyCloudPasswordText = passwordBox.Password;
    }

    private void AttachViewModel(SettingsViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.LinklyCloudPasswordText) ||
            _viewModel is null ||
            !string.IsNullOrEmpty(_viewModel.LinklyCloudPasswordText))
        {
            return;
        }

        ClearPasswordBox(LinklyCloudPasswordBox);
        ClearPasswordBox(LinklyCloudBackendPasswordBox);
    }

    private static void ClearPasswordBox(PasswordBox passwordBox)
    {
        if (string.IsNullOrEmpty(passwordBox.Password))
        {
            return;
        }

        // 保存后清空界面输入，避免敏感信息继续停留在可见控件里。
        passwordBox.Clear();
    }
}
