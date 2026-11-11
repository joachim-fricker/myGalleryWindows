using System.Windows;
using System.Windows.Controls;
using MyGallery.Data;
using MyGallery.Domain;

namespace MyGallery.Views;

/// <summary>Zeigt das Vorschaubild eines <see cref="GalleryImage"/> (asynchron, aus dem Cache).</summary>
public sealed class ThumbImage : Image
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(GalleryImage), typeof(ThumbImage),
        new PropertyMetadata(null, (d, _) => ((ThumbImage)d).OnItemChanged()));

    public GalleryImage? Item
    {
        get => (GalleryImage?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private CancellationTokenSource? _cts;

    public ThumbImage()
    {
        Stretch = System.Windows.Media.Stretch.UniformToFill;
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(this, System.Windows.Media.BitmapScalingMode.HighQuality);
        Unloaded += (_, _) => { _cts?.Cancel(); _cts = null; };
        Loaded += (_, _) => { if (Source is null) OnItemChanged(); };
    }

    private async void OnItemChanged()
    {
        _cts?.Cancel();
        _cts = null;
        var item = Item;
        if (item is null)
        {
            Source = null;
            return;
        }
        if (AppServices.Thumbs.TryGetMemory(item) is { } hit)
        {
            Source = hit;
            return;
        }
        Source = null;
        var cts = _cts = new CancellationTokenSource();
        try
        {
            // Beim schnellen Scrollen nicht jedes vorbeiflitzende Bild laden
            await Task.Delay(60, cts.Token);
            var bmp = await AppServices.Thumbs.GetAsync(item, cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(Item, item)) Source = bmp;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write($"ThumbImage {item.Path}: {ex}");
        }
    }
}
