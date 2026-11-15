using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brim.Core;

namespace Brim.Views;

/// <summary>
/// The notch's calculator: type a sum or tap the keys. The answer shows as you go; = (or Enter) keeps it and
/// puts it in the box to carry on from. The last few sums sit underneath: click one to use its answer again.
/// </summary>
sealed class CalculatorView : Grid
{
    // Kept while Brim runs, so closing the notch doesn't lose your sum or your history
    static string text = "";
    static readonly List<(string sum, string answer)> history = new();

    readonly TextBox box = new() { FontSize = 17, Padding = new Thickness(10, 7, 10, 7), TextAlignment = TextAlignment.Right };
    readonly TextBlock hint = new() { Text = "Type a sum, or tap the keys", FontSize = 13, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly TextBlock answer = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 4, 0), Cursor = Cursors.Hand };
    readonly TextBlock status = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0) };
    readonly StackPanel past = new() { Margin = new Thickness(0, 4, 0, 0) };
    readonly Action<TextBox> startTyping;
    bool justAnswered; // after =: a digit starts a new sum, an operator carries on from the answer

    public CalculatorView(Action<TextBox> startTyping)
    {
        this.startTyping = startTyping;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ---- left: the sum, the answer, the last few sums
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition());
        box.SetResourceReference(StyleProperty, "Field");
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        answer.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Text");
        status.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
        left.Children.Add(box);
        left.Children.Add(hint);
        SetRow(answer, 1); left.Children.Add(answer);
        SetRow(status, 2); left.Children.Add(status);
        SetRow(past, 3); left.Children.Add(past);
        Children.Add(left);

        // ---- right: the keys
        var keys = new Grid { VerticalAlignment = VerticalAlignment.Stretch };
        for (int c = 0; c < 6; c++) keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        for (int r = 0; r < 4; r++) keys.RowDefinitions.Add(new RowDefinition());
        void AddKey(string label, int row, int col, Action act, string kind = "digit", int span = 1, string? tip = null)
        {
            var b = new Button { Content = label, FontSize = kind is "op" or "equals" ? 19 : 16, Padding = new Thickness(0), Margin = new Thickness(2.5), ToolTip = tip };
            b.SetResourceReference(StyleProperty, "Pill");
            if (kind == "equals")
            {
                b.SetResourceReference(Control.BackgroundProperty, "Brim.Accent");
                b.SetResourceReference(Control.ForegroundProperty, "Brim.OnAccent");
                b.FontWeight = FontWeights.SemiBold;
            }
            else if (kind == "op") b.SetResourceReference(Control.ForegroundProperty, "Brim.Accent");
            else if (kind == "edit") b.SetResourceReference(Control.ForegroundProperty, "Brim.Dim");
            b.Click += (_, _) => act();
            SetRow(b, row); SetColumn(b, col); SetColumnSpan(b, span);
            keys.Children.Add(b);
        }
        string[,] digits = { { "7", "8", "9" }, { "4", "5", "6" }, { "1", "2", "3" } };
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++) { var d = digits[r, c]; AddKey(d, r, c, () => Type(d)); }
        AddKey("0", 3, 0, () => Type("0"), span: 2);
        AddKey(Calc.Format(0.5m)[1].ToString(), 3, 2, () => Type(Calc.Format(0.5m)[1].ToString())); // the PC's decimal point
        AddKey("÷", 0, 3, () => Type("÷"), "op");
        AddKey("×", 1, 3, () => Type("×"), "op");
        AddKey("−", 2, 3, () => Type("−"), "op");
        AddKey("+", 3, 3, () => Type("+"), "op");
        AddKey("(", 0, 4, () => Type("("), "op");
        AddKey(")", 0, 5, () => Type(")"), "op");
        AddKey("%", 1, 4, () => Type("%"), "op", tip: "Percent: 50% is 0.5, and 200 + 10% is 220");
        AddKey("√", 2, 4, () => Type("√"), "op", tip: "Square root");
        AddKey("⌫", 1, 5, Backspace, "edit", tip: "Delete one");
        AddKey("C", 2, 5, Clear, "edit", tip: "Clear");
        AddKey("=", 3, 4, Answer, "equals", span: 2);
        SetColumn(keys, 2);
        Children.Add(keys);

        box.Text = text;
        box.CaretIndex = text.Length;
        Update();
        // The notch never takes the keyboard on its own (it would steal it from your game or app): only when you click in here
        box.PreviewMouseLeftButtonDown += (_, _) => startTyping(box);
        box.TextChanged += (_, _) => { text = box.Text; Update(); };
        box.PreviewTextInput += (_, e) =>
        {
            // Typed keys become the signs on the keys; = answers; letters and the like are ignored
            var t = e.Text;
            e.Handled = true;
            if (t == "=") { Answer(); return; }
            var mapped = t switch { "*" => "×", "x" or "X" => "×", "/" => "÷", "-" => "−", _ => t };
            if (mapped.All(ch => char.IsDigit(ch) || "+−×÷().,%^√".Contains(ch))) Type(mapped);
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return) { Answer(); e.Handled = true; }
            else if (e.Key == Key.Space) e.Handled = true;
        };
        answer.MouseLeftButtonUp += (_, _) => CopyResult();
        answer.ToolTip = "Click to copy";
    }

    public void Focus(Action<TextBox> start) { start(box); box.CaretIndex = box.Text.Length; }

    public bool HasHistory => history.Count > 0;
    public bool HasResult => Calc.Evaluate(box.Text) != null;

    /// <summary>Put text in at the cursor (or over what's selected).</summary>
    void Type(string s)
    {
        bool op = s is "+" or "−" or "×" or "÷" or "%" or "^" or ")";
        if (justAnswered && !op) box.Text = ""; // a new sum
        justAnswered = false;
        int at = Math.Min(box.CaretIndex, box.Text.Length);
        if (box.SelectionLength > 0) { at = box.SelectionStart; box.Text = box.Text.Remove(at, box.SelectionLength); }
        box.Text = box.Text.Insert(at, s);
        box.CaretIndex = at + s.Length;
    }

    void Backspace()
    {
        justAnswered = false;
        if (box.SelectionLength > 0) { int at = box.SelectionStart; box.Text = box.Text.Remove(at, box.SelectionLength); box.CaretIndex = at; return; }
        int i = Math.Min(box.CaretIndex, box.Text.Length);
        if (i == 0) return;
        box.Text = box.Text.Remove(i - 1, 1);
        box.CaretIndex = i - 1;
    }

    public void Clear()
    {
        justAnswered = false;
        box.Text = "";
        status.Text = "";
    }

    void Answer()
    {
        var sum = box.Text.Trim();
        if (sum.Length == 0) return;
        if (Calc.Evaluate(sum) is not decimal v) { status.Text = "That sum isn't finished"; return; }
        var a = Calc.Format(v);
        if (sum != a)
        {
            history.RemoveAll(h => h.sum == sum);
            history.Insert(0, (sum, a));
            if (history.Count > 20) history.RemoveAt(history.Count - 1);
        }
        box.Text = a;
        box.CaretIndex = a.Length;
        justAnswered = true;
        ShowHistory();
    }

    public void CopyResult()
    {
        if (Calc.Evaluate(box.Text) is not decimal v) return;
        try { Clipboard.SetText(Calc.Format(v)); status.Text = "Copied ✓"; } catch { }
    }

    public void ClearHistory() { history.Clear(); ShowHistory(); }

    void Update()
    {
        hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var v = Calc.Evaluate(box.Text);
        // The answer as you type; nothing while the sum is unfinished (so it doesn't flicker to an error)
        answer.Text = v is decimal d ? "= " + Calc.Format(d) : box.Text.Length == 0 ? "0" : answer.Text;
        answer.Opacity = v != null || box.Text.Length == 0 ? 1 : 0.35;
        status.Text = "";
        ShowHistory();
    }

    void ShowHistory()
    {
        past.Children.Clear();
        foreach (var (sum, a) in history.Take(3))
        {
            var row = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 1, 4, 1), Cursor = Cursors.Hand, ToolTip = "Use this answer" };
            row.Inlines.Add(new System.Windows.Documents.Run(sum + " = "));
            var bold = new System.Windows.Documents.Run(a);
            bold.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brim.Text");
            row.Inlines.Add(bold);
            row.SetResourceReference(TextBlock.ForegroundProperty, "Brim.Dim");
            row.MouseLeftButtonUp += (_, _) => { Type(a); };
            past.Children.Add(row);
        }
    }
}
