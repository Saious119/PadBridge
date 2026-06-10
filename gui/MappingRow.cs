using System.ComponentModel;
using System.Runtime.CompilerServices;
using PadBridge.Gui.Evdev;

namespace PadBridge.Gui;

/// <summary>One row of the mapping table: a controller input and its output binding.</summary>
public sealed class MappingRow : INotifyPropertyChanged
{
    private int? _targetCode;
    private bool _isHighlighted;
    private bool _isCapturing;

    public MappingRow(int sourceCode) => SourceCode = sourceCode;

    public int SourceCode { get; }

    public string SourceLabel =>
        $"{EventCodes.FriendlyName(SourceCode)}  ({EventCodes.CanonicalName(SourceCode)})";

    public int? TargetCode
    {
        get => _targetCode;
        set
        {
            if (_targetCode == value) return;
            _targetCode = value;
            Notify();
            Notify(nameof(TargetLabel));
            Notify(nameof(HasTarget));
        }
    }

    public bool HasTarget => _targetCode.HasValue;

    public string TargetLabel =>
        IsCapturing ? "Press a button or key..." :
        _targetCode is { } t ? $"{EventCodes.FriendlyName(t)}  ({EventCodes.CanonicalName(t)})" :
        "(not mapped)";

    /// <summary>True while the physical button is held; drives the row highlight.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set { if (_isHighlighted != value) { _isHighlighted = value; Notify(); } }
    }

    /// <summary>True while this row is waiting for the user to press the new binding.</summary>
    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (_isCapturing == value) return;
            _isCapturing = value;
            Notify();
            Notify(nameof(TargetLabel));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
