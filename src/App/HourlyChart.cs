using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace CodexTpsFloat.App;

public sealed class HourlyChart : Canvas
{
    static readonly IBrush WeightedBrush = new SolidColorBrush(Color.Parse("#007AFF"));
    static readonly IBrush MedianBrush = new SolidColorBrush(Color.Parse("#AF52DE"));
    static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#85858D"));
    static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#EEEEF2"));
    static readonly IBrush HoverBrush = new SolidColorBrush(Color.FromArgb(12, 0, 122, 255));

    public IReadOnlyList<HourStats> Hours { get; private set; } = Array.Empty<HourStats>();

    public HourlyChart()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;
        SizeChanged += (_, _) => DrawChart();
    }

    public void SetHours(IReadOnlyList<HourStats> hours)
    {
        Hours = hours.OrderBy(h => h.Hour, StringComparer.Ordinal).ToArray();
        DrawChart();
    }

    public void Clear()
    {
        Hours = Array.Empty<HourStats>();
        Children.Clear();
    }

    void Label(string text, double x, double y, double width, TextAlignment alignment = TextAlignment.Center)
    {
        var label = new TextBlock
        {
            Text = text,
            Width = width,
            FontSize = 10,
            Foreground = MutedBrush,
            TextAlignment = alignment,
            IsHitTestVisible = false
        };
        SetLeft(label, x);
        SetTop(label, y);
        Children.Add(label);
    }

    static string Value(double? value) => value?.ToString("F1", CultureInfo.InvariantCulture) ?? "—";

    void DrawChart()
    {
        Children.Clear();
        const double left = 38, right = 22, top = 18, bottom = 26;
        double width = Bounds.Width - left - right, height = Bounds.Height - top - bottom;
        if (width <= 0 || height <= 0) return;

        double maximum = Hours.SelectMany(h => new[] { h.Weighted, h.Median })
            .Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        double rawStep = Math.Max(1, maximum) / 4;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        double normalized = rawStep / magnitude;
        double step = (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;
        double ceiling = step * 4;
        double X(int index) => left + (Hours.Count <= 1 ? width / 2 : index * width / (Hours.Count - 1));
        double Y(double value) => top + height * (1 - value / ceiling);

        Label("TPS", 0, 0, left - 6, TextAlignment.Right);
        for (int tick = 0; tick <= 4; tick++)
        {
            double value = step * tick, y = Y(value);
            Children.Add(new Line
            {
                StartPoint = new Point(left, y),
                EndPoint = new Point(left + width, y),
                Stroke = GridBrush,
                StrokeThickness = 1,
                IsHitTestVisible = false
            });
            string text = value >= 1000
                ? (value / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k"
                : value.ToString("0.##", CultureInfo.InvariantCulture);
            Label(text, 0, y - 7, left - 7, TextAlignment.Right);
        }
        int ticks = Math.Min(5, Hours.Count);
        for (int tick = 0; tick < ticks; tick++)
        {
            int index = ticks == 1 ? 0 : (int)Math.Round(tick * (Hours.Count - 1d) / (ticks - 1));
            Label(Hours[index].Hour[..5], X(index) - 22, top + height + 7, 44);
        }

        DrawSeries(h => h.Weighted, WeightedBrush, false);
        DrawSeries(h => h.Median, MedianBrush, true);

        if (!Hours.Any(h => h.Weighted.HasValue || h.Median.HasValue))
            Label("暂无有效计时样本", left, top + height / 2 - 8, width);

        for (int index = 0; index < Hours.Count; index++)
        {
            double start = index == 0 ? left : (X(index - 1) + X(index)) / 2;
            double end = index == Hours.Count - 1 ? left + width : (X(index) + X(index + 1)) / 2;
            var region = new Rectangle { Width = end - start, Height = height, Fill = Brushes.Transparent };
            SetLeft(region, start);
            SetTop(region, top);
            var hour = Hours[index];
            ToolTip.SetTip(region, $"{hour.Hour}（北京时间）\n样本 {hour.Samples}\n加权 TPS：{Value(hour.Weighted)}\n中位数：{Value(hour.Median)}");
            region.PointerEntered += (_, _) => region.Fill = HoverBrush;
            region.PointerExited += (_, _) => region.Fill = Brushes.Transparent;
            Children.Add(region);
        }

        void DrawSeries(Func<HourStats, double?> select, IBrush brush, bool dashed)
        {
            var segment = new List<Point>();
            var markers = new List<Point>();
            for (int index = 0; index < Hours.Count; index++)
            {
                if (select(Hours[index]) is double value)
                {
                    var point = new Point(X(index), Y(value));
                    segment.Add(point);
                    markers.Add(point);
                }
                else FinishSegment();
            }
            FinishSegment();
            foreach (var point in markers)
            {
                double diameter = dashed ? 5 : 8;
                var marker = new Ellipse
                {
                    Width = diameter,
                    Height = diameter,
                    Fill = dashed ? brush : Brushes.White,
                    Stroke = brush,
                    StrokeThickness = dashed ? 1 : 2,
                    IsHitTestVisible = false
                };
                SetLeft(marker, point.X - diameter / 2);
                SetTop(marker, point.Y - diameter / 2);
                Children.Add(marker);
            }

            void FinishSegment()
            {
                if (segment.Count > 1)
                {
                    var line = new Polyline
                    {
                        Points = segment.ToArray(),
                        Stroke = brush,
                        StrokeThickness = 2,
                        StrokeLineCap = PenLineCap.Round,
                        StrokeJoin = PenLineJoin.Round,
                        IsHitTestVisible = false
                    };
                    if (dashed) line.StrokeDashArray = new AvaloniaList<double> { 4, 3 };
                    Children.Add(line);
                }
                segment.Clear();
            }
        }
    }
}
