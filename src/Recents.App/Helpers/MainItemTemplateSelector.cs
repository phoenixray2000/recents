using System.Windows;
using System.Windows.Controls;
using Recents.App.ViewModels;

namespace Recents.App.Helpers;

public sealed class MainItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? RecentTemplate { get; set; }
    public DataTemplate? ClipboardTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        return item switch
        {
            ClipboardItemViewModel => ClipboardTemplate,
            RecentItemViewModel => RecentTemplate,
            _ => base.SelectTemplate(item, container)
        };
    }
}
