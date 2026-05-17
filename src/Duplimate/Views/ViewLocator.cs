using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Duplimate.ViewModels;

namespace Duplimate.Views;

/// <summary>
/// Maps ViewModel types to View types by convention:
///   Duplimate.ViewModels.BackupsViewModel → Duplimate.Views.BackupsView
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? data)
    {
        if (data is null) return new TextBlock { Text = "null" };
        var vmType = data.GetType();
        var viewName = vmType.FullName!.Replace(".ViewModels.", ".Views.").Replace("ViewModel", "View");
        var viewType = Type.GetType(viewName);
        if (viewType is null) return new TextBlock { Text = $"View not found: {viewName}" };
        return (Control)Activator.CreateInstance(viewType)!;
    }

    public bool Match(object? data) => data is ViewModelBase;
}
