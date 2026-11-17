using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Brim.Views;

/// <summary>
/// Drag to reorder a row, with movement you can follow: the item you grab lifts up (a little bigger, with a shadow)
/// and follows the pointer, the others slide aside to make room, and on letting go it settles into its new place.
/// Works on any horizontal panel. The panel holds the mouse capture while dragging, so items can move around
/// inside it without dropping the drag.
/// </summary>
sealed class LiveReorder
{
    readonly Panel panel;
    readonly Func<FrameworkElement, bool> include;
    readonly Action<FrameworkElement, int, int>? move;
    readonly Action<bool> finished;
    readonly double lift, liftScale;

    FrameworkElement? item;
    double grab;
    int startIndex;

    public bool Active => item != null;

    /// <summary>Items that wrap onto several rows (the Shortcuts tab): the carried item follows the pointer anywhere, and
    /// goes to whichever place it is nearest.</summary>
    public bool Wrap { get; set; }
    Vector grab2;

    static Point Mid(FrameworkElement el) { var s = Slot(el); return new Point(s.X + s.Width / 2, s.Y + s.Height / 2); }

    /// <summary>A column instead of a row (the dock on the side of the screen): everything runs along Y, and the lift is sideways.</summary>
    public bool Vertical { get; set; }

    double Along(Point p) => Vertical ? p.Y : p.X;
    double Start(FrameworkElement el) => Vertical ? Slot(el).Y : Slot(el).X;
    double Length(FrameworkElement el) => Vertical ? Slot(el).Height : Slot(el).Width;
    DependencyProperty AlongProp => Vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
    DependencyProperty AcrossProp => Vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty;
    double MovedAlong(TranslateTransform t) => Vertical ? t.Y : t.X;

    static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };
    static readonly ConditionalWeakTable<FrameworkElement, Parts> parts = new();
    sealed record Parts(TransformGroup Group, ScaleTransform Scale, TranslateTransform Move);

    /// <param name="panel">The row the items sit in.</param>
    /// <param name="include">Which children take part (others, like an "add" button, stay where they are).</param>
    /// <param name="move">Moves the item from one place to another among the included items; null moves it among the panel's children.</param>
    /// <param name="finished">Called once you let go; true when the order changed.</param>
    /// <param name="lift">How far the item rises while carried (negative is up).</param>
    public LiveReorder(Panel panel, Func<FrameworkElement, bool> include, Action<FrameworkElement, int, int>? move, Action<bool> finished,
                       double lift = -4, double liftScale = 1.1)
    {
        this.panel = panel;
        this.include = include;
        this.move = move;
        this.finished = finished;
        this.lift = lift;
        this.liftScale = liftScale;
        panel.PreviewMouseMove += (_, e) => { if (Active) { Drag(e.GetPosition(panel)); e.Handled = true; } };
        panel.PreviewMouseLeftButtonUp += (_, e) => { if (Active) { e.Handled = true; End(); } };
        // Only when the row itself loses the mouse: a button inside letting go of it (as the drag starts) bubbles up here too
        panel.LostMouseCapture += (_, e) => { if (Active && e.OriginalSource == panel) End(); };
    }

    List<FrameworkElement> Items => panel.Children.OfType<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible && include(c)).ToList();

    static Rect Slot(FrameworkElement el) => LayoutInformation.GetLayoutSlot(el);

    static Parts PartsOf(FrameworkElement el)
    {
        if (parts.TryGetValue(el, out var p) && el.RenderTransform == p.Group) return p;
        var group = new TransformGroup();
        // Keep whatever the item already does (a hover zoom, say) and add ours after it
        if (el.RenderTransform is { } existing && existing != Transform.Identity && !existing.IsFrozen) group.Children.Add(existing);
        var scale = new ScaleTransform(1, 1);
        var moveBy = new TranslateTransform();
        group.Children.Add(scale);
        group.Children.Add(moveBy);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = group;
        p = new Parts(group, scale, moveBy);
        parts.AddOrUpdate(el, p);
        return p;
    }

    static void Animate(Animatable target, DependencyProperty prop, double to, double ms, double? from = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease };
        if (from != null) a.From = from;
        target.BeginAnimation(prop, a);
    }

    /// <summary>Pick up <paramref name="el"/>; <paramref name="pointer"/> is where the pointer is, in the panel.</summary>
    public void Begin(FrameworkElement el, Point pointer)
    {
        if (Active || !include(el)) return;
        item = el;
        startIndex = Items.IndexOf(el);
        grab = Along(pointer) - Start(el);
        grab2 = pointer - Slot(el).TopLeft;
        var p = PartsOf(el);
        Panel.SetZIndex(el, 100);
        Animate(p.Scale, ScaleTransform.ScaleXProperty, liftScale, 160);
        Animate(p.Scale, ScaleTransform.ScaleYProperty, liftScale, 160);
        if (!Wrap) Animate(p.Move, AcrossProp, Vertical ? 0 : lift, 160);
        el.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = 0.55, Color = Colors.Black };
        el.Opacity = 0.96;
        panel.CaptureMouse();
        Drag(pointer);
    }

    public void Drag(Point pointer)
    {
        if (item == null) return;
        var items = Items;
        int from = items.IndexOf(item);
        if (from < 0) { End(); return; }
        if (Wrap) { DragWrapped(items, from, pointer); return; }
        // Where the carried item is, kept within the row: it can't be pulled off either end of it
        double minStart = items.Min(Start), maxEnd = items.Max(o => Start(o) + Length(o));
        double pos = Math.Clamp(Along(pointer) - grab, minStart, Math.Max(minStart, maxEnd - Length(item)));
        // Where the carried item's middle is now, against the middles of the others
        double centre = pos + Length(item) / 2;
        int to = items.Where(o => o != item).Count(o => Start(o) + Length(o) / 2 < centre);
        if (to != from)
        {
            // Remember where everything is on screen, move, then slide each one from there to its new place
            var before = items.ToDictionary(o => o, o => Start(o) + MovedAlong(PartsOf(o).Move));
            if (move != null) move(item, from, to);
            else
            {
                var others = items.Where(o => o != item).ToList();
                panel.Children.Remove(item);
                int at = to < others.Count ? panel.Children.IndexOf(others[to]) : panel.Children.IndexOf(others[^1]) + 1;
                panel.Children.Insert(at, item);
            }
            panel.UpdateLayout();
            var now = Items;
            if (to < now.Count && now[to] != item) item = now[to]; // a list that made a new element for it
            foreach (var o in now)
            {
                if (o == item || !before.TryGetValue(o, out var was)) continue;
                double start = was - Start(o);
                if (Math.Abs(start) > 0.5) Animate(PartsOf(o).Move, AlongProp, 0, 200, start);
            }
        }
        // The carried item sits under the pointer
        var t = PartsOf(item).Move;
        t.BeginAnimation(AlongProp, null);
        t.SetValue(AlongProp, pos - Start(item));
    }

    /// <summary>The top-left corner the carried tile would have under the pointer, kept inside the area the tiles fill.</summary>
    Point Clamped(List<FrameworkElement> items, Point pointer)
    {
        var area = Slot(items[0]);
        foreach (var o in items.Skip(1)) area.Union(Slot(o));
        var s = Slot(item!);
        return new Point(Math.Clamp(pointer.X - grab2.X, area.Left, Math.Max(area.Left, area.Right - s.Width)),
                         Math.Clamp(pointer.Y - grab2.Y, area.Top, Math.Max(area.Top, area.Bottom - s.Height)));
    }

    void DragWrapped(List<FrameworkElement> items, int from, Point pointer)
    {
        var corner = Clamped(items, pointer);
        var centre = new Point(corner.X + Slot(item!).Width / 2, corner.Y + Slot(item).Height / 2);
        // The place whose middle is nearest (its own counts too, so it stays put until it is clearly closer to another)
        int to = items.Select((o, i) => (i, d: (Mid(o) - centre).LengthSquared)).OrderBy(x => x.d).First().i;
        if (to != from)
        {
            var before = items.ToDictionary(o => o, o => new Point(Slot(o).X + PartsOf(o).Move.X, Slot(o).Y + PartsOf(o).Move.Y));
            if (move != null) move(item, from, to);
            else
            {
                panel.Children.Remove(item);
                var others = items.Where(o => o != item).ToList();
                int at = to < others.Count ? panel.Children.IndexOf(others[to]) : panel.Children.IndexOf(others[^1]) + 1;
                panel.Children.Insert(at, item);
            }
            panel.UpdateLayout();
            var now = Items;
            if (to < now.Count && now[to] != item) item = now[to];
            foreach (var o in now)
            {
                if (o == item || !before.TryGetValue(o, out var was)) continue;
                var s = Slot(o);
                if (Math.Abs(was.X - s.X) > 0.5) Animate(PartsOf(o).Move, TranslateTransform.XProperty, 0, 220, was.X - s.X);
                if (Math.Abs(was.Y - s.Y) > 0.5) Animate(PartsOf(o).Move, TranslateTransform.YProperty, 0, 220, was.Y - s.Y);
            }
        }
        var t = PartsOf(item).Move;
        t.BeginAnimation(TranslateTransform.XProperty, null);
        t.BeginAnimation(TranslateTransform.YProperty, null);
        corner = Clamped(Items, pointer); // (the tiles may have moved)
        t.X = corner.X - Slot(item).X;
        t.Y = corner.Y - Slot(item).Y;
    }

    /// <summary>Dev check: where an item's transforms are now.</summary>
    public static string Probe(FrameworkElement el) => parts.TryGetValue(el, out var p)
        ? $"x={p.Move.X:0.0} y={p.Move.Y:0.0} scale={p.Scale.ScaleX:0.00} group={(el.RenderTransform == p.Group)} z={Panel.GetZIndex(el)} fx={(el.Effect != null)}"
        : "no parts";

    public void End()
    {
        var el = item;
        if (el == null) return;
        item = null;
        if (panel.IsMouseCaptured) panel.ReleaseMouseCapture();
        bool changed = Items.IndexOf(el) != startIndex;
        // Settle into place
        var p = PartsOf(el);
        if (Wrap)
        {
            Animate(p.Move, TranslateTransform.XProperty, 0, 220, p.Move.X);
            Animate(p.Move, TranslateTransform.YProperty, 0, 220, p.Move.Y);
        }
        else
        {
            Animate(p.Move, AlongProp, 0, 220, MovedAlong(p.Move));
            Animate(p.Move, AcrossProp, 0, 220);
        }
        Animate(p.Scale, ScaleTransform.ScaleXProperty, 1, 220);
        var settle = new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = Ease };
        settle.Completed += (_, _) => { if (item != el) { el.Effect = null; Panel.SetZIndex(el, 0); } };
        p.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
        el.Opacity = 1;
        finished(changed);
    }
}
