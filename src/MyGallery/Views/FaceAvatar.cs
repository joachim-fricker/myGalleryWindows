using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyGallery.Data;
using MyGallery.Domain;

namespace MyGallery.Views;

/// <summary>Rundes Gesichts-Vorschaubild einer Person (asynchron, aus dem Cache).</summary>
public sealed class FaceAvatar : Image
{
    public static readonly DependencyProperty PersonProperty = DependencyProperty.Register(
        nameof(Person), typeof(PersonInfo), typeof(FaceAvatar),
        new PropertyMetadata(null, (d, _) => ((FaceAvatar)d).OnPersonChanged()));

    public PersonInfo? Person
    {
        get => (PersonInfo?)GetValue(PersonProperty);
        set => SetValue(PersonProperty, value);
    }

    private CancellationTokenSource? _cts;

    public FaceAvatar()
    {
        Stretch = Stretch.UniformToFill;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SizeChanged += (_, e) =>
            Clip = new EllipseGeometry(new Point(e.NewSize.Width / 2, e.NewSize.Height / 2), e.NewSize.Width / 2, e.NewSize.Height / 2);
    }

    private async void OnPersonChanged()
    {
        _cts?.Cancel();
        _cts = null;
        if (Person is not { } p)
        {
            Source = null;
            return;
        }
        if (AppServices.Thumbs.TryGetFaceMemory(p.CoverImage, p.Cover) is { } hit)
        {
            Source = hit;
            return;
        }
        Source = null;
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var bmp = await AppServices.Thumbs.GetFaceAsync(p.CoverImage, p.Cover, cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(Person, p)) Source = bmp;
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>Gesichtsausschnitt (abgerundetes Quadrat) für ein bestimmtes Gesicht auf einem Bild.</summary>
public sealed class FaceCrop : Image
{
    private CancellationTokenSource? _cts;

    public FaceCrop()
    {
        Stretch = Stretch.UniformToFill;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SizeChanged += (_, e) => Clip = new RectangleGeometry(new Rect(e.NewSize), 10, 10);
    }

    public async void Show(GalleryImage image, FaceRef face)
    {
        _cts?.Cancel();
        if (AppServices.Thumbs.TryGetFaceMemory(image, face) is { } hit)
        {
            Source = hit;
            return;
        }
        Source = null;
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var bmp = await AppServices.Thumbs.GetFaceAsync(image, face, cts.Token);
            if (!cts.IsCancellationRequested) Source = bmp;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
