using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Kampf.ViewModels;

namespace Kampf;

public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        // View models and views mirror each other's folders (Shell, Library,
        // Graph, …), but a view's namespace stays Kampf.Views, so the folder is
        // dropped and only the type name is mapped.
        var viewModelName = param.GetType();
        var name = viewModelName.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type is null)
        {
            var leaf = viewModelName.Name.Replace("ViewModel", "View", StringComparison.Ordinal);
            type = Type.GetType("Kampf.Views." + leaf);
            name = "Kampf.Views." + leaf;
        }

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}