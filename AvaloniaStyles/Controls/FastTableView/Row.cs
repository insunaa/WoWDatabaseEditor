using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace AvaloniaStyles.Controls.FastTableView;

public interface ITableRowGroup
{
    public bool IsExpanded => true;
    public IReadOnlyList<ITableRow> Rows { get; }
    event Action<ITableRowGroup, ITableRow>? RowChanged;
    event Action<ITableRowGroup>? RowsChanged;
}

public interface ITableRow
{
    public IReadOnlyList<ITableCell> CellsList { get; }
    event Action<ITableRow>? Changed;
}
/*
public class Row : IRow
{
    public Row(params ICell[] cells)
    {
        Cells = cells.ToList();
        foreach (var cell in Cells)
        {
            cell.PropertyChanged += CellOnPropertyChanged;
        }
    }

    private void CellOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Changed?.Invoke(this);
    }

    public List<ICell> Cells { get; set; }
    public event Action<Row>? Changed;
}
*/
public interface ITableCell : INotifyPropertyChanged
{
    void UpdateFromString(string newValue);
    string? StringValue { get; }
}

public class WdeCell : ITableCell
{
    private object value;

    public WdeCell(object value)
    {
        this.value = value;
    }

    public object Value
    {
        get => value;
        set
        {
            this.value = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void UpdateFromString(string newValue)
    {
        if (value is string s)
            Value = newValue;
        else if (value is bool b)
            Value = newValue is "True" or "true" or "TRUE";
        else if (value is long l && long.TryParse(newValue, out var newLong))
            Value = newLong;
        else if (value is double d && double.TryParse(newValue, out var newDouble))
            Value = newDouble;
        else
            throw new NotImplementedException(value.GetType() + " is not supported");
    }

    public string? StringValue => value.ToString();

    [NotifyPropertyChangedInvocator]
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public override string ToString()
    {
        return value.ToString() ?? "(null)";
    }
}

public class IntCell : ITableCell
{
    private readonly Func<int> getter;
    private readonly Action<int> setter;
    public event PropertyChangedEventHandler? PropertyChanged;
        
    public IntCell(Func<int> getter, Action<int> setter)
    {
        this.getter = getter;
        this.setter = setter;
    }

    public void UpdateFromString(string newValue)
    {
        if (int.TryParse(newValue, out var i))
            setter(i);
    }

    public string? StringValue => getter().ToString();

    public override string ToString() => StringValue ?? "(null)";
}
    
public class UIntCell : ITableCell
{
    private readonly Func<uint> getter;
    private readonly Action<uint> setter;
    public event PropertyChangedEventHandler? PropertyChanged;
        
    public UIntCell(Func<uint> getter, Action<uint> setter)
    {
        this.getter = getter;
        this.setter = setter;
    }

    public void UpdateFromString(string newValue)
    {
        if (uint.TryParse(newValue, out var i))
            setter(i);
    }

    public string? StringValue => getter().ToString();

    public override string ToString() => StringValue ?? "(null)";
}

public class FloatCell : ITableCell
{
    private readonly Func<float> getter;
    private readonly Action<float> setter;
    public event PropertyChangedEventHandler? PropertyChanged;
        
    public FloatCell(Func<float> getter, Action<float> setter)
    {
        this.getter = getter;
        this.setter = setter;
    }

    public void UpdateFromString(string newValue)
    {
        if (float.TryParse(newValue, out var f))
            setter(f);
    }

    public string StringValue => getter().ToString(CultureInfo.InvariantCulture);

    public override string ToString() => StringValue;
}

public class StringCell : ITableCell
{
    private readonly Func<string?> getter;
    private readonly Action<string?> setter;
    public event PropertyChangedEventHandler? PropertyChanged;
        
    public StringCell(Func<string?> getter, Action<string?> setter)
    {
        this.getter = getter;
        this.setter = setter;
    }

    public void UpdateFromString(string newValue)
    {
        setter(newValue);
    }

    public string? StringValue => getter();

    public override string ToString() => StringValue ?? "(null)";
}

