using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Duplimate.ViewModels;

namespace Duplimate.Views;

public partial class SourceTreePickerWindow : Window
{
    private readonly SourceTreePickerViewModel _vm;

    public SourceTreePickerWindow() : this(System.Array.Empty<string>()) { }

    public SourceTreePickerWindow(IEnumerable<string> initiallyChecked)
    {
        _vm = new SourceTreePickerViewModel(initiallyChecked);
        DataContext = _vm;
        InitializeComponent();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        _vm.ConfirmCommand.Execute(null);
        Close(_vm.Result);
    }
}
