using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PortVisualizer.App.Services;
using PortVisualizer.Data;

namespace PortVisualizer.App.Controls;

/// <summary>Real-time area chart of new vs closed connections per polling cycle.</summary>
public sealed class ActivityChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples), typeof(IReadOnlyList<ActivitySample>), typeof(ActivityChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<ActivitySample>? Samples { get => (IReadOnlyList<ActivitySample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }

    private static readonly Dictionary<Color, Brush> Gradients = new();
    private static readonly Dictionary<Color, Pen> Pens = new();
    private static Brush Gradient(Color c)
    {
        if (!Gradients.TryGetValue(c, out var b)) { var g = new LinearGradientBrush(Color.FromArgb(90, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90); g.Freeze(); Gradients[c] = b = g; }
        return b;
    }
    private static Pen LinePen(Color c)
    {
        if (!Pens.TryGetValue(c, out var p)) { var br = new SolidColorBrush(c); br.Freeze(); p = new Pen(br, 2) { LineJoin = PenLineJoin.Round }; p.Freeze(); Pens[c] = p; }
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var border = TryFindResource("BorderBrush") as Brush ?? Brushes.Gray;
        var muted = TryFindResource("MutedBrush") as Brush ?? Brushes.Gray;
        var grid = new Pen(border, 1) { DashStyle = DashStyles.Dot };
        var left = 30.0; var bottom = 18.0; var plotW = Math.Max(10, w - left - 6); var plotH = Math.Max(10, h - bottom - 6);
        var samples = Samples;
        var max = Math.Max(4, samples is { Count: > 0 } ? samples.Max(s => Math.Max(s.New, s.Closed)) : 4);
        max = (int)Math.Ceiling(max / 4.0) * 4;

        for (var i = 0; i <= 4; i++)
        {
            var y = 6 + plotH - plotH * i / 4;
            dc.DrawLine(grid, new Point(left, y), new Point(w - 6, y));
            var ft = new FormattedText((max * i / 4).ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(left - ft.Width - 5, y - ft.Height / 2));
        }
        if (samples is null || samples.Count < 2)
        {
            var ft = new FormattedText("Collecting samples...", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(left + (plotW - ft.Width) / 2, 6 + plotH / 2 - 8));
            return;
        }

        void Series(Func<ActivitySample, int> sel, Color c)
        {
            var step = plotW / Math.Max(1, samples.Count - 1);
            var pts = samples.Select((s, i) => new Point(left + i * step, 6 + plotH - plotH * sel(s) / max)).ToList();
            var fill = new StreamGeometry();
            using (var ctx = fill.Open())
            {
                ctx.BeginFigure(new Point(pts[0].X, 6 + plotH), true, true);
                foreach (var p in pts) ctx.LineTo(p, true, false);
                ctx.LineTo(new Point(pts[^1].X, 6 + plotH), true, false);
            }
            fill.Freeze();
            dc.DrawGeometry(Gradient(c), null, fill);
            var line = new StreamGeometry();
            using (var ctx = line.Open()) { ctx.BeginFigure(pts[0], false, false); foreach (var p in pts.Skip(1)) ctx.LineTo(p, true, true); }
            line.Freeze();
            dc.DrawGeometry(null, LinePen(c), line);
        }
        Series(s => s.Closed, Color.FromRgb(0xFB, 0x92, 0x3C));
        Series(s => s.New, Color.FromRgb(0x00, 0xE5, 0xA0));

        var span = samples[^1].TimeUtc - samples[0].TimeUtc;
        var label = new FormattedText($"last {span.TotalSeconds:0}s", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new Point(left, h - 15));
        var now = new FormattedText("now", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(now, new Point(w - 6 - now.Width, h - 15));
    }
}

/// <summary>Bar timeline of historical events per bucket (opened above, closed below the baseline).</summary>
public sealed class TimelineChart : FrameworkElement
{
    public static readonly DependencyProperty BucketsProperty = DependencyProperty.Register(nameof(Buckets), typeof(IEnumerable), typeof(TimelineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) =>
        {
            if (e.OldValue is System.Collections.Specialized.INotifyCollectionChanged o) o.CollectionChanged -= ((TimelineChart)d).OnChanged;
            if (e.NewValue is System.Collections.Specialized.INotifyCollectionChanged n) n.CollectionChanged += ((TimelineChart)d).OnChanged;
        }));
    public IEnumerable? Buckets { get => (IEnumerable?)GetValue(BucketsProperty); set => SetValue(BucketsProperty, value); }

    private void OnChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var items = Buckets?.Cast<TimelineBucket>().ToList() ?? new();
        var muted = TryFindResource("MutedBrush") as Brush ?? Brushes.Gray;
        var border = TryFindResource("BorderBrush") as Brush ?? Brushes.Gray;
        dc.DrawLine(new Pen(border, 1), new Point(0, h / 2), new Point(w, h / 2));
        if (items.Count == 0)
        {
            var ft = new FormattedText("No events in the selected range", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, h / 2 - 8));
            return;
        }
        var max = Math.Max(1, items.Max(b => Math.Max(b.Opened + b.Other, b.Closed)));
        var slot = w / items.Count;
        var bw = Math.Clamp(slot - 2, 1, 40);
        var green = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xA0));
        var cyan = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xFF));
        var gray = new SolidColorBrush(Color.FromRgb(0x87, 0x93, 0xA0));
        var half = h / 2 - 3;
        for (var i = 0; i < items.Count; i++)
        {
            var b = items[i]; var x = i * slot + (slot - bw) / 2;
            var up = half * b.Opened / max; var other = half * b.Other / max; var down = half * b.Closed / max;
            dc.DrawRectangle(green, null, new Rect(x, h / 2 - up, bw, up));
            dc.DrawRectangle(cyan, null, new Rect(x, h / 2 - up - other, bw, other));
            dc.DrawRectangle(gray, null, new Rect(x, h / 2 + 1, bw, down));
        }
    }
}
