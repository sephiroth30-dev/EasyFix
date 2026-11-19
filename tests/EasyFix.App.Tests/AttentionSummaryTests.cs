using System.Reflection;
using EasyFix.App.ViewModels;
using Xunit;

namespace EasyFix.App.Tests;

/// <summary>
/// El resumen del inicio muestra solo lo que necesita atención — pero <b>nunca</b> esconde lo que
/// no se pudo medir.
/// </summary>
/// <remarks>
/// <para>Es el riesgo que trae resumir: lo primero que uno recortaría para que la pantalla de
/// entrada quede limpia es la lista de «no se pudo determinar», que es ruido visual y no es un
/// problema concreto. Y es exactamente lo que no se puede recortar — una comprobación que falló
/// no es evidencia de que el equipo esté sano, y acá estaría en la cara más visible de la app.</para>
///
/// <para>Estas pruebas invocan <c>BuildAttentionSummary</c> por reflexión. No es lo ideal, pero
/// el método es privado por diseño (lo llama <c>BuildReport</c>) y la alternativa —hacerlo público
/// solo para testear— ensucia el contrato. Lo que importa es que la regla quede fijada.</para>
/// </remarks>
public sealed class AttentionSummaryTests
{
    private static MainViewModel BuildWithRows(
        IEnumerable<ReportRow> checklist,
        IEnumerable<ReportRow> software,
        IEnumerable<ReportRow> hardware,
        IEnumerable<ReportRow> undetermined)
    {
        MainViewModel vm = Harness.Build(new Harness.OkRunner());

        foreach (ReportRow r in checklist) { vm.ChecklistRows.Add(r); }
        foreach (ReportRow r in software) { vm.SoftwareRows.Add(r); }
        foreach (ReportRow r in hardware) { vm.HardwareRows.Add(r); }
        foreach (ReportRow r in undetermined) { vm.UndeterminedRows.Add(r); }

        typeof(MainViewModel)
            .GetMethod("BuildAttentionSummary", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, null);

        return vm;
    }

    private static ReportRow Row(string title, string kind) => new(title, "detalle", null, kind);

    [Fact]
    public void LoQueNoSePudoMedir_SiempreEntraEnElResumen()
    {
        // Ni un solo hallazgo grave, pero dos cosas sin medir: tienen que estar.
        MainViewModel vm = BuildWithRows(
            checklist: new[] { Row("Disco sano", "ok"), Row("Memoria bien", "ok") },
            software: Array.Empty<ReportRow>(),
            hardware: Array.Empty<ReportRow>(),
            undetermined: new[] { Row("Estado SMART", "info"), Row("Batería", "info") });

        Assert.Equal(2, vm.AttentionRows.Count);
        Assert.Contains(vm.AttentionRows, r => r.Title == "Estado SMART");
        Assert.Contains(vm.AttentionRows, r => r.Title == "Batería");
    }

    [Fact]
    public void ConCosasSinMedir_ElTitularNoDiceQueEstaTodoBien()
    {
        MainViewModel vm = BuildWithRows(
            checklist: new[] { Row("Disco sano", "ok") },
            software: Array.Empty<ReportRow>(),
            hardware: Array.Empty<ReportRow>(),
            undetermined: new[] { Row("Estado SMART", "info") });

        Assert.DoesNotContain("está bien", vm.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no se pudo", vm.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoQueEstaBien_NoEntraEnElResumen()
    {
        // El sentido del resumen: las 12 condiciones en verde no se leen en la entrada.
        MainViewModel vm = BuildWithRows(
            checklist: new[] { Row("Disco sano", "ok"), Row("Antivirus único", "ok") },
            software: Array.Empty<ReportRow>(),
            hardware: Array.Empty<ReportRow>(),
            undetermined: Array.Empty<ReportRow>());

        Assert.Empty(vm.AttentionRows);
        Assert.Equal(2, vm.ReviewedCount);
        Assert.Equal(2, vm.GoodCount);
    }

    [Fact]
    public void SinNadaQueAtender_ElTitularTranquiliza()
    {
        MainViewModel vm = BuildWithRows(
            checklist: new[] { Row("Disco sano", "ok") },
            software: Array.Empty<ReportRow>(),
            hardware: Array.Empty<ReportRow>(),
            undetermined: Array.Empty<ReportRow>());

        Assert.Contains("está bien", vm.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoCriticoVaPrimero()
    {
        MainViewModel vm = BuildWithRows(
            checklist: Array.Empty<ReportRow>(),
            software: new[] { Row("Advertencia", "warn") },
            hardware: new[] { Row("Grave", "crit") },
            undetermined: new[] { Row("Sin medir", "info") });

        Assert.Equal("Grave", vm.AttentionRows[0].Title);
        Assert.Equal("Sin medir", vm.AttentionRows[^1].Title); // lo no medido, al final
    }

    [Fact]
    public void ElTitularCuentaSoloLoGrave_NoLoNoMedido()
    {
        MainViewModel vm = BuildWithRows(
            checklist: Array.Empty<ReportRow>(),
            software: new[] { Row("Uno", "warn"), Row("Dos", "crit") },
            hardware: Array.Empty<ReportRow>(),
            undetermined: new[] { Row("Sin medir", "info") });

        Assert.Contains("2", vm.Headline);
    }

    [Fact]
    public void ConElDiscoFallando_NoSeOfreceNingunaAccionPrincipal()
    {
        // Misma compuerta que FixRunner: optimizar un disco que se muere es la peor jugada.
        // La pantalla tiene que respetarla, no solo el motor.
        MainViewModel vm = Harness.Build(new Harness.OkRunner());
        vm.FixesBlocked = true;
        vm.SoftwareRows.Add(Row("Algo grave", "crit"));

        typeof(MainViewModel)
            .GetMethod("BuildAttentionSummary", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, null);

        Assert.False(vm.ShowImproveAsPrimary);
        Assert.False(vm.ShowRepairAsPrimary);
        Assert.Contains("disco", vm.Headline, StringComparison.OrdinalIgnoreCase);
    }
}
