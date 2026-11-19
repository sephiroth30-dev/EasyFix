using EasyFix.App.ViewModels;
using Xunit;

namespace EasyFix.App.Tests;

/// <summary>
/// <b>La invariante más importante de toda la app: pase lo que pase, al terminar una operación
/// la interfaz tiene que quedar usable.</b>
/// </summary>
/// <remarks>
/// <para>Esta clase existe por un fallo reportado desde un equipo real: falló la instalación de
/// RustDesk y, a partir de ahí, «Buscar controladores» dejó de responder — «solo funciona
/// Analizar el equipo».</para>
///
/// <para>La pista estaba en cuál seguía andando. <c>AnalyzeCommand</c> es el ÚNICO comando sin
/// <c>CanExecute</c>: todos los demás exigen <c>!IsBusy</c>. Que justo ese siguiera funcionando
/// apunta derecho a <c>IsBusy</c> quedándose en <c>true</c>. Un técnico en la casa de un cliente
/// no tiene forma de diagnosticar eso: ve una app que dejó de responder y la cierra.</para>
///
/// <para>Ningún test de <c>EasyFix.Core</c> podía detectarlo, porque no es un bug de ninguna
/// pieza: es del pegamento entre todas.</para>
/// </remarks>
public sealed class UsableAfterFailureTests
{
    /// <summary>
    /// Todos los comandos que el técnico puede necesitar después de una operación. Si alguno
    /// queda deshabilitado, la app está rota para él aunque no haya tirado ninguna excepción.
    /// </summary>
    private static void AssertUsable(MainViewModel vm, string afterWhat)
    {
        Assert.False(vm.IsBusy, $"IsBusy quedó en true después de {afterWhat}.");

        Assert.True(vm.AnalyzeCommand.CanExecute(null), $"Analizar quedó muerto después de {afterWhat}.");
        Assert.True(vm.UpdateDriversCommand.CanExecute(null), $"Buscar controladores quedó muerto después de {afterWhat}.");
        Assert.True(vm.RunInstallCommand.CanExecute(null), $"Instalar quedó muerto después de {afterWhat}.");
        Assert.True(vm.UpgradeInstalledCommand.CanExecute(null), $"Actualizar quedó muerto después de {afterWhat}.");
        Assert.True(vm.DiagnoseWingetCommand.CanExecute(null), $"Diagnosticar quedó muerto después de {afterWhat}.");
    }

    private static void SelectFirstApp(MainViewModel vm)
    {
        foreach (AppChoice app in vm.Apps)
        {
            app.IsSelected = false;
        }

        vm.Apps[0].IsSelected = true;
    }

    [Fact]
    public async Task TrasUnaInstalacionQueFalla_LaAppSigueUsable()
    {
        // El escenario exacto del reporte: el instalador devuelve 1639 y falla.
        MainViewModel vm = Harness.Build(new Harness.FailingRunner());
        SelectFirstApp(vm);

        await vm.RunInstallCommand.ExecuteAsync(null);

        AssertUsable(vm, "una instalación fallida");
    }

    [Fact]
    public async Task TrasUnaInstalacionQueRevienta_LaAppSigueUsable()
    {
        // Peor caso: el proceso ni se lanza y la excepción sube por todo el flujo.
        MainViewModel vm = Harness.Build(new Harness.ThrowingRunner());
        SelectFirstApp(vm);

        await vm.RunInstallCommand.ExecuteAsync(null);

        AssertUsable(vm, "una instalación que tiró excepción");
    }

    [Fact]
    public async Task TrasUnaInstalacionExitosa_LaAppSigueUsable()
    {
        MainViewModel vm = Harness.Build(new Harness.OkRunner());
        SelectFirstApp(vm);

        await vm.RunInstallCommand.ExecuteAsync(null);

        AssertUsable(vm, "una instalación exitosa");
    }

    [Fact]
    public async Task SinInternet_LaAppSigueUsable()
    {
        MainViewModel vm = Harness.Build(new Harness.OkRunner(), online: false);
        SelectFirstApp(vm);

        await vm.RunInstallCommand.ExecuteAsync(null);

        AssertUsable(vm, "intentar instalar sin internet");
    }

    [Fact]
    public async Task SinNingunProgramaMarcado_LaAppSigueUsable()
    {
        // Camino de retorno temprano: devuelve antes del try. Si IsBusy se hubiera puesto en true
        // antes de ese return, la app quedaría trabada sin haber hecho nada.
        MainViewModel vm = Harness.Build(new Harness.OkRunner());

        foreach (AppChoice app in vm.Apps)
        {
            app.IsSelected = false;
        }

        await vm.RunInstallCommand.ExecuteAsync(null);

        AssertUsable(vm, "apretar Instalar sin nada marcado");
    }

    [Fact]
    public async Task TrasActualizarLoInstalado_LaAppSigueUsable()
    {
        MainViewModel vm = Harness.Build(new Harness.FailingRunner());

        await vm.UpgradeInstalledCommand.ExecuteAsync(null);

        AssertUsable(vm, "una actualización fallida");
    }

    [Fact]
    public async Task BuscarControladoresSinHaberAnalizado_NoDejaLaAppTrabada()
    {
        // RunFixesAsync devuelve temprano si no hay análisis previo. Ese camino NO puede dejar
        // IsBusy en true — y además tiene que decir algo, no fallar en silencio.
        MainViewModel vm = Harness.Build(new Harness.OkRunner());

        await vm.UpdateDriversCommand.ExecuteAsync(null);

        AssertUsable(vm, "buscar controladores sin analizar antes");
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
    }

    [Fact]
    public async Task DosOperacionesSeguidas_LaSegundaTambienFunciona()
    {
        // El reporte real fue justamente este orden: instalar (falla) → buscar controladores.
        MainViewModel vm = Harness.Build(new Harness.FailingRunner());
        SelectFirstApp(vm);

        await vm.RunInstallCommand.ExecuteAsync(null);
        await vm.UpdateDriversCommand.ExecuteAsync(null);

        AssertUsable(vm, "instalar y después buscar controladores");
    }
}
