using EasyFix.App.ViewModels;
using Xunit;

namespace EasyFix.App.Tests;

/// <summary>
/// Una corrida de UN solo fix no puede devolver el informe de una reparación completa.
/// </summary>
/// <remarks>
/// <para>Reporte real: «al finalizar Buscar controladores me entregó resultado de la reparación,
/// estado antes y después — eso no lo pedí, y al lado derecho todo mezclado».</para>
///
/// <para>La causa: <c>RunFixesAsync</c> llamaba siempre a <c>VerifyAfterFixesAsync</c>, que vuelve
/// a medir las 12 condiciones del equipo y arma el antes/después. Para «Mejorar rendimiento» eso
/// es exactamente el entregable que justifica el trabajo; para actualizar un driver de red es un
/// informe que nadie pidió sobre cosas que ni se tocaron — y encima tarda sus segundos.</para>
/// </remarks>
public sealed class SingleFixRunTests
{
    [Fact]
    public async Task BuscarControladores_NoDevuelveElChecklistAntesYDespues()
    {
        MainViewModel vm = Harness.Build(new Harness.OkRunner());

        await vm.UpdateDriversCommand.ExecuteAsync(null);

        Assert.False(
            vm.HasChecklistCompareRows,
            "«Buscar controladores» mostró el antes/después de una reparación completa. " +
            "Ese informe es de «Mejorar rendimiento», no de una corrida de un solo fix.");
    }

    [Fact]
    public async Task BuscarControladores_DejaLaAppUsable()
    {
        // Misma invariante que el resto: la pantalla de resultado no puede dejar comandos muertos.
        MainViewModel vm = Harness.Build(new Harness.OkRunner());

        await vm.UpdateDriversCommand.ExecuteAsync(null);

        Assert.False(vm.IsBusy);
        Assert.True(vm.UpdateDriversCommand.CanExecute(null));
        Assert.True(vm.AnalyzeCommand.CanExecute(null));
    }
}
