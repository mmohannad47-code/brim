using System;
using System.Globalization;

namespace Brim.Core;

/// <summary>
/// The calculator tab's maths: + − × ÷ (also * / x), brackets, powers (^), square roots (√) and percentages.
/// Works in decimal, so 0.1 + 0.2 is 0.3, not 0.30000000000000004. Percentages work like a phone's calculator:
/// 50% is 0.5, and 200 + 10% is 220 (ten percent of 200 added on).
/// </summary>
public static class Calc
{
    /// <summary>The answer, or null when the sum isn't finished or makes no sense (yet).</summary>
    public static decimal? Evaluate(string text)
    {
        try
        {
            var p = new Parser(Normalise(text));
            if (p.AtEnd) return null;
            var v = p.Sum();
            return p.AtEnd ? Math.Round(v.value, 12) : null;
        }
        catch { return null; } // unfinished ("3 +"), divide by zero, too big
    }

    /// <summary>A result to show and to type with again: plain digits, the decimal point your PC uses, no grouping.</summary>
    public static string Format(decimal v)
    {
        var s = v.ToString("0.############", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s.Replace(".", DecimalPoint);
    }

    static string DecimalPoint => CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator == "," ? "," : ".";

    /// <summary>One spelling for everything: × and x for times, ÷ for divide, − for minus, the PC's decimal point.</summary>
    static string Normalise(string s)
    {
        s = s.Replace('×', '*').Replace('x', '*').Replace('X', '*').Replace('÷', '/').Replace('−', '-').Replace('–', '-')
             .Replace(" ", "").Replace(" ", "");
        // A comma is the decimal point where the PC writes numbers that way; elsewhere it's just a thousands separator
        return DecimalPoint == "," ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
    }

    sealed class Parser
    {
        readonly string s;
        int i;
        public Parser(string s) => this.s = s;
        public bool AtEnd => i >= s.Length;
        char Peek => i < s.Length ? s[i] : '\0';

        // value, and whether it was written as a percentage (so "+ 10%" can mean ten percent of what came before)
        public (decimal value, bool percent) Sum()
        {
            var left = Product();
            while (Peek is '+' or '-')
            {
                char op = s[i++];
                var right = Product();
                decimal r = right.percent ? left.value * right.value : right.value;
                left = (op == '+' ? left.value + r : left.value - r, false);
            }
            return left;
        }

        (decimal value, bool percent) Product()
        {
            var left = Power();
            while (Peek is '*' or '/' || Peek == '(' || Peek == '√')
            {
                // "2(3+4)" and "2√9" multiply, like on paper
                char op = Peek is '*' or '/' ? s[i++] : '*';
                var right = Power();
                left = (op == '*' ? left.value * right.value : left.value / right.value, false);
            }
            return left;
        }

        (decimal value, bool percent) Power()
        {
            var b = Unary();
            if (Peek != '^') return b;
            i++;
            var e = Power(); // right to left: 2^3^2 is 2^9
            return ((decimal)Math.Pow((double)b.value, (double)e.value), false);
        }

        (decimal value, bool percent) Unary()
        {
            if (Peek == '-') { i++; var v = Unary(); return (-v.value, v.percent); }
            if (Peek == '+') { i++; return Unary(); }
            if (Peek == '√')
            {
                i++;
                var v = Unary();
                if (v.value < 0) throw new ArithmeticException();
                return ((decimal)Math.Sqrt((double)v.value), false);
            }
            return Postfix();
        }

        (decimal value, bool percent) Postfix()
        {
            decimal v = Atom();
            bool percent = false;
            while (Peek == '%') { i++; v /= 100; percent = true; }
            return (v, percent);
        }

        decimal Atom()
        {
            if (Peek == '(')
            {
                i++;
                var v = Sum().value;
                if (Peek == ')') i++; // a bracket left open closes at the end, like most calculators
                return v;
            }
            int start = i;
            while (char.IsDigit(Peek) || Peek == '.') i++;
            if (i == start) throw new FormatException();
            return decimal.Parse(s[start..i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        }
    }
}
