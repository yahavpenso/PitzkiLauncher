using System.Globalization;

namespace PitzkiLauncher;

/// <summary>Small safe math parser: + - * / % ^ ( ) sqrt sin cos tan abs round pi.</summary>
public static class Calculator
{
    public static bool TryEval(string input, out double value, out string text)
    {
        value = 0; text = "";
        var s = input.Trim();
        if (s.StartsWith('=')) s = s[1..];
        s = s.Replace('×', '*').Replace('÷', '/').Replace(",", "").Replace(" ", "").ToLowerInvariant();
        if (s.Length < 2 || !s.Any(char.IsDigit)) return false;

        // needs a real operation, not just a number
        bool hasOp = s.IndexOfAny(new[] { '+', '*', '/', '^', '%', '(' }) >= 0 || s.LastIndexOf('-') > 0
                     || s.Contains("sqrt") || s.Contains("pi");
        if (!hasOp) return false;
        if (s.Any(c => !(char.IsDigit(c) || "+-*/%^().".Contains(c) || (c >= 'a' && c <= 'z')))) return false;

        try
        {
            var p = new Parser(s);
            value = p.Expr();
            if (p.Pos != s.Length || double.IsNaN(value) || double.IsInfinity(value)) return false;
            text = Math.Round(value, 10).ToString("0.##########", CultureInfo.InvariantCulture);
            return true;
        }
        catch { return false; }
    }

    private class Parser(string s)
    {
        public int Pos;
        private char Cur => Pos < s.Length ? s[Pos] : '\0';

        public double Expr()
        {
            double v = Term();
            while (Cur is '+' or '-')
            {
                char op = s[Pos++];
                double r = Term();
                v = op == '+' ? v + r : v - r;
            }
            return v;
        }

        private double Term()
        {
            double v = Unary();
            while (Cur is '*' or '/' or '%')
            {
                char op = s[Pos++];
                double r = Unary();
                v = op == '*' ? v * r : op == '/' ? v / r : v % r;
            }
            return v;
        }

        private double Unary()
        {
            if (Cur == '-') { Pos++; return -Unary(); }
            if (Cur == '+') { Pos++; return Unary(); }
            return Power();
        }

        private double Power()
        {
            double b = Primary();
            if (Cur == '^') { Pos++; return Math.Pow(b, Unary()); }
            return b;
        }

        private double Primary()
        {
            if (Cur == '(')
            {
                Pos++;
                double v = Expr();
                if (Cur != ')') throw new FormatException();
                Pos++;
                return v;
            }
            if (char.IsLetter(Cur))
            {
                int st = Pos;
                while (char.IsLetter(Cur)) Pos++;
                var name = s[st..Pos];
                if (name == "pi") return Math.PI;
                if (name == "e") return Math.E;
                if (Cur != '(') throw new FormatException();
                double a = Primary();
                return name switch
                {
                    "sqrt" => Math.Sqrt(a),
                    "abs" => Math.Abs(a),
                    "round" => Math.Round(a),
                    "sin" => Math.Sin(a * Math.PI / 180),
                    "cos" => Math.Cos(a * Math.PI / 180),
                    "tan" => Math.Tan(a * Math.PI / 180),
                    "log" => Math.Log10(a),
                    "ln" => Math.Log(a),
                    _ => throw new FormatException()
                };
            }
            int start = Pos;
            while (char.IsDigit(Cur) || Cur == '.') Pos++;
            if (start == Pos) throw new FormatException();
            return double.Parse(s[start..Pos], CultureInfo.InvariantCulture);
        }
    }
}
