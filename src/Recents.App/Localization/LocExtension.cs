using System.Windows.Markup;
using Binding = System.Windows.Data.Binding;
using BindingMode = System.Windows.Data.BindingMode;

namespace Recents.App.Localization;

// XAML usage: {loc:T Key=Action_Open}
[MarkupExtensionReturnType(typeof(object))]
public class TExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public TExtension() { }
    public TExtension(string key) { Key = key; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationManager.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
