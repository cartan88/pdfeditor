using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace PdfEditor.Models;

public enum FieldKind { Text, CheckBox, Radio, Choice, Signature, PushButton }

public sealed class FormFieldModel : INotifyPropertyChanged
{
    private string _value = "";

    public required string FullName { get; init; }
    public required FieldKind Kind { get; init; }
    public bool ReadOnly { get; init; }
    public bool Multiline { get; init; }
    public bool Password { get; init; }
    public bool Comb { get; init; }
    public bool IsCombo { get; init; }
    public bool Editable { get; init; }
    /// <summary>Signature field that already holds a certificate-based digital signature.</summary>
    public bool IsSigned { get; init; }
    public int MaxLength { get; init; }
    /// <summary>Font size from the field's /DA string; 0 means auto.</summary>
    public double FontSize { get; init; }
    /// <summary>Quadding: 0 left, 1 center, 2 right.</summary>
    public int Alignment { get; init; }
    /// <summary>Choice options as (export value, display text).</summary>
    public List<(string Export, string Display)> Options { get; init; } = new();
    public List<WidgetModel> Widgets { get; } = new();

    /// <summary>Text / choice value, or the on-state name (or "Off") for check boxes and radios.</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            IsDirty = true;
            OnPropertyChanged();
        }
    }

    public bool IsDirty { get; private set; }

    public void SetInitialValue(string value) { _value = value; IsDirty = false; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class WidgetModel
{
    public required FormFieldModel Field { get; init; }
    public int PageIndex { get; init; }
    /// <summary>Widget rectangle in PDF user space (Rect.Y = bottom edge).</summary>
    public Rect PdfRect { get; init; }
    /// <summary>Appearance state name used when this check box / radio widget is on.</summary>
    public string OnState { get; init; } = "Yes";
    public bool Hidden { get; init; }
}
