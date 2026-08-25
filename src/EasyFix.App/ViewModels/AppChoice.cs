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

    /// <summary>
    /// Explicación completa del resultado, con el código de error.
    /// </summary>
    /// <remarks>
    /// Se muestra en la fila, no solo en el log. La primera versión mostraba únicamente «Falló» y
    /// dejaba al técnico sin nada con que trabajar: el detalle estaba en el log y nadie lo iba a
    /// abrir en la casa de un cliente.
    /// </remarks>
    [ObservableProperty]
    private string? _statusDetail;

    public void MarkInstalling()
    {
        StatusDetail = null;
        Apply("Instalando…", "info");
    }

    public void MarkResult(WingetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        (string text, string kind) = result.Outcome switch
        {
            WingetOutcome.Installed => ("Instalado", "ok"),
            WingetOutcome.AlreadyInstalled => ("Ya estaba", "ok"),
            WingetOutcome.NotFound => ("No está en el catálogo", "crit"),
            WingetOutcome.NotInRepository => ("No está en winget", "warn"),
            WingetOutcome.SourceUnavailable => ("winget sin catálogo", "crit"),
            WingetOutcome.NoApplicableInstaller => ("Sin instalador compatible", "crit"),
            WingetOutcome.WingetMissing => ("Falta winget", "crit"),
            WingetOutcome.TimedOut => ("Se pasó del tiempo", "crit"),
            _ => ("Falló", "crit"),
        };

        // El motivo va a la vista siempre que no sea un éxito: es lo único con lo que el técnico
        // puede trabajar cuando algo falla en la casa de un cliente.
        StatusDetail = result.PackageAvailable ? null : result.Detail;
        Apply(text, kind);
    }

    private void Apply(string text, string kind)
    {
        Status = text;
        StatusKind = kind;
    }
}
