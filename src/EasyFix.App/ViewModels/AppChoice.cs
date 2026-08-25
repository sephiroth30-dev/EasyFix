using CommunityToolkit.Mvvm.ComponentModel;
using EasyFix.Core.Apps;
using EasyFix.Core.Configuration;

namespace EasyFix.App.ViewModels;

/// <summary>Un programa de la lista de instalación, con su casilla y su estado.</summary>
public sealed partial class AppChoice : ObservableObject
{
    public AppChoice(WingetPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        Package = package;
        IsSelected = package.IsDefault;
    }

    public WingetPackage Package { get; }

    public string Name => Package.Name;

    public string Id => Package.Id;

    public string? Note => Package.Note;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Estado en curso o final: se muestra a la derecha del nombre.</summary>
    [ObservableProperty]
    private string? _status;

    /// <summary>Determina el color del estado: <c>ok</c>, <c>warn</c> o <c>crit</c>.</summary>
    [ObservableProperty]
    private string _statusKind = "info";

    public void MarkInstalling() => Apply("Instalando…", "info");

    public void MarkResult(WingetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        (string text, string kind) = result.Outcome switch
        {
            WingetOutcome.Installed => ("Instalado", "ok"),
            WingetOutcome.AlreadyInstalled => ("Ya estaba", "ok"),
            WingetOutcome.NotFound => ("ID no encontrado", "crit"),
            WingetOutcome.NoApplicableInstaller => ("Sin instalador compatible", "crit"),
            WingetOutcome.WingetMissing => ("Falta winget", "crit"),
            WingetOutcome.TimedOut => ("Se pasó del tiempo", "crit"),
            _ => ("Falló", "crit"),
        };

        Apply(text, kind);
    }

    private void Apply(string text, string kind)
    {
        Status = text;
        StatusKind = kind;
    }
}
