namespace HydroPilotWeb.Services;

/// <summary>
/// Orden "natural" para nombres con números: "Circuito 2" antes que "Circuito 10" (el orden de texto pone
/// "Circuito 10" antes). Los tramos de dígitos se comparan por valor; el resto, sin distinguir mayúsculas.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            var digits = char.IsDigit(x[i]) && char.IsDigit(y[j]);
            int si = i, sj = j;
            while (i < x.Length && char.IsDigit(x[i]) == digits)
                i++;
            while (j < y.Length && char.IsDigit(y[j]) == digits)
                j++;

            int cmp;
            if (digits)
            {
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                cmp = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
            }
            else
            {
                cmp = string.Compare(x[si..i], y[sj..j], StringComparison.CurrentCultureIgnoreCase);
            }
            if (cmp != 0)
                return cmp;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
