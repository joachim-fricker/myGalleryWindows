using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace MyGallery;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Zahlen/Datum in Bindings im Windows-Format statt en-US
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));
        base.OnStartup(e);
    }
}
