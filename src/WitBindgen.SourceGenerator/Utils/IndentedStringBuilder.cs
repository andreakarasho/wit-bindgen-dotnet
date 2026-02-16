// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace WitBindgen.SourceGenerator;

public class IndentedStringBuilder
{
    private const byte IndentSize = 4;
    private int _indent;
    private bool _indentPending = true;

    private readonly StringBuilder _stringBuilder = new();

    public StringBuilder Inner => _stringBuilder;

    public virtual int IndentCount
    {
        get => _indent;
        set => _indent = value < 0 ? 0 : value;
    }

    public virtual int Length
    {
        get => _stringBuilder.Length;
        set => _stringBuilder.Length = value;
    }

    public virtual IndentedStringBuilder Append(string value)
    {
        DoIndent();
        _stringBuilder.Append(value);
        return this;
    }

#if NET
    public virtual IndentedStringBuilder Append(ReadOnlySpan<char> value)
    {
        DoIndent();
        _stringBuilder.Append(value);
        return this;
    }
#endif

    public virtual IndentedStringBuilder Append(object value)
    {
        DoIndent();
        _stringBuilder.Append(value);
        return this;
    }

    public virtual IndentedStringBuilder Append(FormattableString value)
    {
        DoIndent();
        _stringBuilder.Append(value);
        return this;
    }

    public virtual IndentedStringBuilder Append(char value)
    {
        DoIndent();
        _stringBuilder.Append(value);
        return this;
    }

    public virtual IndentedStringBuilder Append(int value)
    {
        DoIndent();
        _stringBuilder.Append(value);
        return this;
    }

    public virtual IndentedStringBuilder Append(IEnumerable<string> value)
    {
        DoIndent();
        foreach (var str in value)
        {
            _stringBuilder.Append(str);
        }
        return this;
    }

    public virtual IndentedStringBuilder Append(IEnumerable<char> value)
    {
        DoIndent();
        foreach (var chr in value)
        {
            _stringBuilder.Append(chr);
        }
        return this;
    }

    public virtual IndentedStringBuilder AppendLine()
    {
        AppendLine(string.Empty);
        return this;
    }

    public virtual IndentedStringBuilder AppendLine(string value)
    {
        if (value.Length != 0)
        {
            DoIndent();
        }

        _stringBuilder.AppendLine(value);
        _indentPending = true;
        return this;
    }

    public virtual IndentedStringBuilder AppendLine(FormattableString value)
    {
        DoIndent();
        _stringBuilder.Append(value);
        _indentPending = true;
        return this;
    }

    public virtual IndentedStringBuilder AppendLines(string value, bool skipFinalNewline = false)
    {
        using (var reader = new StringReader(value))
        {
            var first = true;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (first)
                {
                    first = false;
                }
                else
                {
                    AppendLine();
                }

                if (line.Length != 0)
                {
                    Append(line);
                }
            }
        }

        if (!skipFinalNewline)
        {
            AppendLine();
        }

        return this;
    }

    public virtual IndentedStringBuilder AppendJoin(
        IEnumerable<string> values,
        string separator = ", ")
    {
        DoIndent();

        using var enumerator = values.GetEnumerator();

        if (!enumerator.MoveNext())
        {
            return this;
        }

        _stringBuilder.Append(enumerator.Current);

        while (enumerator.MoveNext())
        {
            _stringBuilder.Append(separator);
            _stringBuilder.Append(enumerator.Current);
        }

        return this;
    }

    public virtual IndentedStringBuilder AppendJoin(
        string separator,
        params string[] values)
    {
        DoIndent();

        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                _stringBuilder.Append(separator);
            }

            _stringBuilder.Append(values[i]);
        }

        return this;
    }

    public virtual IndentedStringBuilder Clear()
    {
        _stringBuilder.Clear();
        _indent = 0;
        return this;
    }

    public virtual IndentedStringBuilder IncrementIndent()
    {
        _indent++;
        return this;
    }

    public virtual IndentedStringBuilder DecrementIndent()
    {
        if (_indent > 0)
        {
            _indent--;
        }
        return this;
    }

    public virtual IDisposable Block()
    {
        AppendLine("{");
        return new Indenter(this, "}");
    }

    public virtual IDisposable Block(string startText, string endText)
    {
        AppendLine(startText);
        return new Indenter(this, endText);
    }

    public virtual IDisposable Block(string endText)
    {
        return new Indenter(this, endText);
    }

    public virtual IDisposable Indent()
        => new Indenter(this);

    public virtual IDisposable SuspendIndent()
        => new IndentSuspender(this);

    public virtual IndentedStringBuilder Clone()
    {
        var result = new IndentedStringBuilder();
        result._stringBuilder.Append(_stringBuilder);
        result._indent = _indent;
        result._indentPending = _indentPending;
        return result;
    }

    public override string ToString()
        => _stringBuilder.ToString();

    private void DoIndent()
    {
        if (_indentPending && _indent > 0)
        {
            _stringBuilder.Append(' ', _indent * IndentSize);
        }

        _indentPending = false;
    }

    public Resetter CreateResetter() => new(this);

    private sealed class Indenter : IDisposable
    {
        private readonly IndentedStringBuilder _stringBuilder;
        private readonly string? _closeText;

        public Indenter(IndentedStringBuilder stringBuilder, string? closeText = null)
        {
            _stringBuilder = stringBuilder;
            _closeText = closeText;
            _stringBuilder.IncrementIndent();
        }

        public void Dispose()
        {
            _stringBuilder.DecrementIndent();

            if (_closeText is not null)
            {
                _stringBuilder.AppendLine(_closeText);
            }
        }
    }

    private sealed class IndentSuspender : IDisposable
    {
        private readonly IndentedStringBuilder _stringBuilder;
        private readonly int _indent;

        public IndentSuspender(IndentedStringBuilder stringBuilder)
        {
            _stringBuilder = stringBuilder;
            _indent = _stringBuilder._indent;
            _stringBuilder._indent = 0;
        }

        public void Dispose()
            => _stringBuilder._indent = _indent;
    }

    public struct Resetter
    {
        private readonly IndentedStringBuilder _builder;
        private readonly int _length;
        private readonly int _indent;

        public Resetter(IndentedStringBuilder builder)
        {
            _builder = builder;
            _length = builder.Length;
            _indent = builder.IndentCount;
        }

        public void Reset()
        {
            _builder.Length = _length;
            _builder.IndentCount = _indent;
        }
    }
}
