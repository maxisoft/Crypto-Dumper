﻿using System.Text.RegularExpressions;
using Maxisoft.Utils.Collections.Dictionaries;
using Maxisoft.Utils.Collections.Dictionaries.Specialized;
using Maxisoft.Utils.Collections.LinkedLists;
using Maxisoft.Utils.Empties;

namespace CryptoDumper.Pairs;

public struct PairFilterEntry : IEquatable<PairFilterEntry>
{
    public readonly string Text;
    public readonly Regex? Regex;

    internal PairFilterEntry(string text, Regex? regex = null)
    {
        Text = text;
        Regex = regex;
    }

    public bool IsRegex => Regex is { };

    public bool Match(string s)
    {
        if (Regex is { })
        {
            return Regex!.IsMatch(s);
        }

        return Text == s;
    }

    # region IEquatable

    public bool Equals(PairFilterEntry other) => Text == other.Text && Equals(Regex, other.Regex);

    public override bool Equals(object? obj) => obj is PairFilterEntry other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Text, Regex);

    public static bool operator ==(PairFilterEntry left, PairFilterEntry right) => left.Equals(right);

    public static bool operator !=(PairFilterEntry left, PairFilterEntry right) => !left.Equals(right);

    # endregion
}

public interface IPairFilter
{
    bool Match(string input);
}

public class PairFilter : IPairFilter
{
    public static readonly Regex DetectRegex = new Regex(@"^[a-zA-Z][\w:/-]+$",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private OrderedDequeDictionary<string, EmptyStruct> _pairsSet = new OrderedDequeDictionary<string, EmptyStruct>(StringComparer.InvariantCultureIgnoreCase);

    private LinkedListAsIList<PairFilterEntry> _entries = new LinkedListAsIList<PairFilterEntry>();
    private static readonly char[] Separator = new[] { '\r', '\n', ';' };


    public void AddAll(string input, bool allowRegex = true)
    {
        var es = new EmptyStruct();
        foreach (var s in input.Split(Separator,
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (s.StartsWith('#') || s.StartsWith("//", StringComparison.InvariantCulture)) continue;
            if (_pairsSet.TryAdd(s, es) && allowRegex && !DetectRegex.IsMatch(s))
            {
                _entries.AddLast(new PairFilterEntry(s,
                    new Regex(s,
                        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant |
                        RegexOptions.IgnoreCase)));
            }
        }
    }

    internal void CopyFrom(PairFilter other)
    {
        if (ReferenceEquals(this, other)) return;
        // it's a shallow copy so a AddAll() call populate both this and other
        _pairsSet = other._pairsSet;
        _entries = other._entries;
    }

    public bool Match(string input)
    {
        if (_pairsSet.ContainsKey(input)) return true;
        var node = _entries.First;
        while (node is {})
        {
            if (node.Value.Match(input))
            {
                // lru behavior
                _entries.Remove(node);
                _entries.AddFirst(node.Value);
                return true;
            }

            node = node.Next;
        }

        return !_pairsSet.Any();
    }
}