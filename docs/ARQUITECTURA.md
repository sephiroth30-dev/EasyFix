# Arquitectura de EasyFix

Módulo por módulo: qué hace, por qué está así, y qué lo mantiene honesto.

El [`README.md`](../README.md) es la entrada para quien llega nuevo; [`PLAN.md`](PLAN.md) es el
documento de trabajo con el estado y lo que falta. **Esto es la referencia estructural**: cambia
poco y explica las decisiones que no son obvias leyendo el código.

---

## El principio que ordena todo

> **Un dato que no se pudo medir nunca se cuenta como verde.
> Un éxito falso es peor que un fallo falso.**

Esto no es una aspiración: está codificado. Los campos que pueden fallar al leerse son `nullable`,
el checklist tiene tres estados en vez de dos, y cada consumidor de un valor desconocido elige la
opción conservadora. Si alguna vez hay que elegir entre una API más cómoda y esta regla, gana la
regla.

El contexto lo explica: el trabajo se hace **una vez, en la casa del cliente**, y no se puede volver
fácil. Un «todo bien» inventado hace que el técnico cierre el caso con el problema adentro.

---

## Los dos proyectos

```
src/EasyFix.App/     WPF. Ventana, XAML, ViewModels.
src/EasyFix.Core/    Toda la lógica. Sin una sola referencia a WPF.
```

**La separación la verifica un test, no la disciplina.** `ViewModelSinWpfTests` falla si aparece un
tipo de `System.Windows` en el ViewModel, y el proyecto de pruebas de la app compila los fuentes del
ViewModel **directamente**, sin `UseWPF`:

```xml
<TargetFramework>net8.0-windows</TargetFramework>
<ItemGroup>
  <Compile Include="..\..\src\EasyFix.App\ViewModels\MainViewModel.cs" Link="..." />
</ItemGroup>
```

Referenciar el proyecto de WPF arrastraría `Microsoft.WindowsDesktop.App`, que no existe fuera de
Windows — y entonces las pruebas de la interfaz solo correrían en Windows, que es justo donde menos
se desarrolla. Esta guarda ya hizo su trabajo: al agregar los logos, un `BitmapSource` en el
ViewModel rompió la compilación. La conversión se mudó a un converter de la vista y el ViewModel se
quedó con la **ruta** del instalador, que es un `string`.

---

## `EasyFix.Core` — módulo por módulo

### `Abstractions/`

`IProcessRunner` e `IFileTree`. Existen para poder probar sin Windows y para hacer imposibles
algunas cosas peligrosas.

`IProcessRunner.RunAsync` toma **la ruta absoluta** del ejecutable y **un elemento por argumento**.
Nunca un nombre suelto a resolver por `PATH`: un `winget.exe` puesto en el directorio actual
secuestraría la llamada, y el equipo que estamos reparando puede estar comprometido.

`IFileTree` tiene **nueve métodos y ninguno borra recursivo**. Es deliberado: el borrado peligroso
no se puede invocar por descuido porque no existe en la API.

> Desviación consciente del plan original: se usa un puerto propio en vez de
> `System.IO.Abstractions`, y `System.Text.Json` en vez de `Microsoft.Extensions.Configuration`.
> Motivo: menos superficie de API que auditar.

### `Diagnostics/`

| Pieza | Qué hace |
|---|---|
| `DiagnosticEngine` | Corre los chequeos en paralelo, con timeout por chequeo y presupuesto global |
| `SystemProbe` | **La única capa que toca Windows**: WMI, registro, Event Log, contadores |
| `SystemSnapshot` | El resultado. Nullable donde el dato puede faltar |
| `CrashAnalysis` | Pantallazos azules: bugcheck, WHEA, cruce con actualizaciones recientes |
| `SoftwareFindings` | Reglas sobre el snapshot: inicio, BitLocker, dominio |
| `Finding` | Un hallazgo, con severidad y si se puede arreglar |

**Por qué toda la suciedad de Windows vive en un archivo.** `SystemProbe` concentra cada llamada a
WMI, al registro y al Event Log. Eso lo deja sin pruebas unitarias —a propósito— y a cambio hace
que **correr el `.exe` una vez en un equipo real valide la capa entera**. Todo lo que *decide* está
afuera y sí está probado.

**Los nombres de contador de rendimiento están localizados.** En un Windows en español
`\Memory\Committed Bytes` no existe. Por eso se usan las clases `Win32_PerfFormattedData_*`, cuyos
nombres de propiedad son iguales en cualquier idioma. Es el tipo de trampa que el spike
(`tools/spike/Verify-WindowsApis.ps1`) existe para encontrar.

**El snapshot distingue «no medido» de un valor.** `IsDomainJoined`, `BitLockerActive`,
`HasPrinters`, `HasBluetoothAdapter`, `ActiveAntivirusCount` son nullable; `IsSsd` es `bool?`; SMART
viaja en `SmartPredictsFailure` y **ya no asciende `Unknown` a `Healthy`**. `SnapshotHonestyTests`
lo fija.

### `Classification/`

El clasificador de tres capas para programas de inicio. **No se usa match por nombre de archivo**:
`*VPN*` protegería a un malware llamado *MyVPN* y no protegería a *Pulse Secure*.

1. **Bloqueo duro** — producto de seguridad en `root\SecurityCenter2`, firmado por Microsoft
   Windows, con servicios dependientes corriendo, o con un driver asociado. Intocable.
2. **Lista blanca positiva** — solo los pares *(publisher Authenticode, producto)* de
   `appsettings.json` **con firma válida** se desactivan en automático.
3. **Zona gris** — todo lo demás: checkbox, nunca automático. Los binarios sin firmar caen acá y se
   marcan en rojo.

La primera capa que diga «no» gana. **Desconocido = pedir permiso.**

`StartupApproval.IsEnabled` es la **única fuente** de la regla del bit de `StartupApproved`.
`StartupPreview` muestra en el análisis, con nombres, qué va a pasar al apretar «Mejorar» — sin eso,
en equipos de oficina donde la lista blanca no tiene match, la acción se sentía como «no hace nada».

> **El fallo de v0.9.2, que vale recordar.** `StartupDisableFix` desactiva escribiendo el flag de
> `StartupApproved` sin borrar la clave `Run` (correcto: así se puede re-habilitar desde Windows),
> pero la sonda contaba `Run` **sin mirar ese flag**. El checklist mostraba el mismo número antes y
> después. El software funcionaba; la medición mentía por omisión.
>
> Lección: cuando algo «parece no funcionar», comparar la medición de antes contra la de después
> campo por campo. Si un número no se mueve cuando debería, **sospechar de la sonda antes que del
> fix**.

### `Reputation/`

`ReputationEvaluator` cruza lo instalado contra `Reputation.KnownBad` de `appsettings.json` — 51
entradas, match por **publisher + producto**, separando *confirmado dañino* de *dudoso*.

`InstalledProgramsReader` lee las claves `Uninstall` del registro. **Jamás `Win32_Product`**:
consultar esa clase dispara una reconfiguración de cada MSI instalado, que es lento y puede romper
instalaciones.

**Nunca desinstala solo.** Reporta y el técnico decide. Un desinstalador silencioso que se equivoca
en el equipo de un cliente borra una herramienta de trabajo.

### `Verification/`

`Checklist` + `ChecklistEvaluator`: **12 condiciones** con tres estados — verde, rojo y **no
determinado**. Se evalúan al analizar y se **re-miden automáticamente** después de mejorar o
reparar, y la interfaz las muestra como `antes ✗ → después ✓`.

```
disk.health   disk.space    disk.events     filesystem.clean
memory.pressure              boot.time       startup.light
antivirus.single             crashes.none    devices.ok
network.online               system.files
```

Una sonda caída **degrada sus condiciones a «no determinado»**, no a verde: el evaluador recibe los
`CheckFailure` para no leer un `CrashData.Empty` como «cero pantallazos». Es la regla del proyecto
aplicada al detalle más fácil de arruinar.

### `Fixes/`

`IFix` con `CanApplyAsync` / `ApplyAsync`. `FixRunner` los orquesta detrás de **cuatro compuertas**:
punto de restauración verificado, BitLocker, dominio, y el journal escrito antes del cambio.

`ServiceConditionEvaluator` decide si un servicio se puede tocar según condiciones declaradas en la
configuración — por ejemplo `SysMain` en un disco mecánico. **Un typo en `appsettings.json` tiene
que resultar en «no hago nada», jamás en «lo desactivo»**, y hay un test que lo fija.

### `Drivers/`

`WindowsUpdateDriverCatalog` consulta Windows Update por COM (`Microsoft.Update.Session`, late
binding con `dynamic`) — **no** un repositorio de terceros. `DriverRiskClassifier` reparte en tres
clases:

| Clase | Qué incluye | Qué se hace |
|---|---|---|
| `Safe` | Red, GPU, audio, USB, Bluetooth, impresora, entrada | Se instala solo; se revierte desde el Administrador de dispositivos o el punto de restauración |
| `NeedsConfirmation` | Almacenamiento (SCSI/RAID/HDC) y todo lo que no matcheó | Se nombra; no se instala |
| `Firmware` | BIOS, firmware de placa | **Nunca**, ni con confirmación. Un flasheo fallido es daño físico |

Que el reparto sea por **lista blanca de clases seguras** y no por lista negra es lo que hace que
una clase de dispositivo desconocida caiga en `NeedsConfirmation` en vez de instalarse.

### `Rollback/`

| Pieza | Qué hace |
|---|---|
| `RunJournal` / `JournalStore` / `FileJournalSink` | Registro JSONL por corrida, escrito **antes** de cada cambio |
| `JournalReader` | Lo lee de vuelta, incluso de otra sesión |
| `RestorePointService` / `RestorePointPolicy` | Punto de restauración **verificado**; si no se puede crear, se aborta |
| `UndoEngine` + `RegistryUndoHandlers` | Revierte lo revertible |

El journal se escribe antes porque si la app muere a mitad de camino el undo tiene que seguir
funcionando. El borrado de archivos se marca `reversible: false` y **se dice en el reporte**: no se
miente sobre el alcance del undo.

> CI en `windows-latest` existe por este módulo. 497 pruebas verdes en macOS no vieron que
> `File.ReadAllLines` no puede abrir un archivo que el sink tiene abierto: en Unix no hay locks
> obligatorios de archivo, en Windows sí. El primer run en Windows lo encontró en el acto.

### `Cleaning/` y `Safety/`

`JunctionSafeCleaner` enumera a mano comprobando `FileAttributes.ReparsePoint`. Un junction dentro
de `%TEMP%` apuntando a `Documents` convertiría la limpieza en **borrado de los datos del cliente**.
Hay un test que crea un symlink real en disco.

`PathGuard` canonicaliza y verifica que toda ruta esté bajo su raíz esperada antes de borrar o
copiar.

### `Recommendations/`

`HardwareAdvisor` (HDD→SSD, RAM, batería gastada), `SaturationAdvisor` (predicción de llenado de
disco) y `SoftwareSelectionAdvisor` (qué software según el equipo). Son recomendaciones: EasyFix no
compra discos.

### `Apps/` — instalación de programas

| Pieza | Qué hace |
|---|---|
| `WingetService` | Orquesta la tanda: elige camino, reporta avance, mantiene el orden |
| `DirectDownloadInstaller` | Baja del origen oficial y ejecuta en silencio |
| `WingetBootstrapper` | Instala `Microsoft.DesktopAppInstaller` si winget falta |
| `WingetLocator` | Encuentra `winget.exe` en un proceso elevado |
| `WingetResultParser` / `WingetErrorCodes` | Interpreta la salida |
| `InstalledProgramsReader` | Qué hay instalado, para no reinstalar |
| `DefaultAppsConfigurator` | Chrome como navegador, Acrobat para los PDF |

Esta parte tiene su propio documento, porque es la que más se toca:
**[`CATALOGO.md`](CATALOGO.md)**.

Tres cosas que no son obvias:

**Encontrar `winget.exe` elevado.** winget se instala como paquete MSIX **por usuario** y se invoca
por un alias en `%LOCALAPPDATA%\Microsoft\WindowsApps`. EasyFix corre elevado, y al elevar esa
variable puede resolver al perfil del administrador, **donde el alias no existe** — falla justo en
el escenario real: equipo de cliente, cuenta normal, app elevada por UAC. Se busca primero
`C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*\winget.exe`, que es global; el alias
queda de respaldo. Nunca por `PATH`.

**En serie, nunca en paralelo.** Dos instaladores de Windows se pelean por el mutex de Windows
Installer: uno falla, o peor, queda a medias. Fijado por test (`MaxConcurrent == 1`).

**«Ya estaba instalado» no es un fallo**, y el resumen lo separa. Decirle al técnico que instaló
siete programas cuando instaló cuatro es mentirle sobre lo que hizo.

### `Network/`

`ConnectivityCheck` existe por un caso concreto: un `SocketException 11001` resolviendo
`dl.google.com` se reportaba como «No se pudo descargar Google Chrome: Host desconocido». Es cierto,
pero le deja al técnico el trabajo de traducir «host desconocido» a «este equipo no tiene internet»
— y en ese equipo nadie lo hizo: el diagnóstico siguió como si el problema fuera de winget.

Ahora, sin internet, se dice sin internet, y no se intenta nada que lo necesite.

### `Processes/`

`SafeProcessRunner`: siempre `ArgumentList` (nunca strings concatenados), `UseShellExecute = false`,
ruta absoluta, timeout por llamada, y captura de stdout/stderr. `ProcessOutput` normaliza la salida.

`benignExitCodes` existe porque `net stop` devuelve 2 cuando el servicio ya estaba detenido —el caso
habitual— y eso ensuciaba el log con advertencias en cada corrida.

---

## `EasyFix.App` — la interfaz

```
App.xaml.cs                 Composición: todo el grafo de dependencias se arma acá
ViewModels/MainViewModel    Orquestación de pantallas y acciones
ViewModels/AppChoice        Una fila de la lista de programas, con su avance
ViewModels/Converters       Lo que es puramente de presentación, incluido el icono
ViewModels/InstallerIcon    P/Invoke a shell32 para sacar el logo del instalador
Views/MainWindow.xaml       Una sola ventana
Styles/Palette.{Dark,Light} Dos paletas, misma lista de claves
Styles/Theme.xaml           Estilos
```

### Reglas de diseño que vienen del uso real

**Sin explicaciones en pantalla.** Nada de párrafos ni descargos técnicos: el porqué va al código y
a estos documentos. La pantalla dice qué pasó, no por qué el programador lo hizo así.

**El análisis corre solo al abrir.** Antes había que apretar «Analizar» y hasta entonces el resto de
las acciones estaban bloqueadas — lo que se sentía como «solo funciona un botón».

**Solo se destaca lo que necesita atención.** Lo que está bien se cuenta, no se enumera.

**Una barra de avance por programa**, con sus MB. Una barra global no dice cuál está tardando ni
cuánto le falta al que está en curso.

**El logo sale del instalador recién descargado.** Es el logo auténtico, está en disco justo cuando
hace falta, y no implica empaquetar binarios de marcas ajenas ni bajar nada extra. Si no se puede
sacar —paquetes de winget, instaladores sin icono— queda el monograma, que por eso no se elimina.

**La historia dura lo suficiente para leerse.** 7-Zip instala en segundos; sin un tiempo mínimo en
pantalla, su historia aparecía y desaparecía.

**Las dos paletas tienen las mismas claves**, y lo verifica `PaletteParityTests`. Una clave que
exista solo en una deja ese elemento sin color en el otro tema.

> **Lección cara, anotada en el `.csproj`.** v0.9.5 adoptó WPF-UI y la app quedó ilegible:
> `ApplicationThemeManager.Apply(this)` sin un tema registrado aplicaba el tema **claro** sobre el
> fondo oscuro. Se descubrió en el equipo de un cliente, no en el build. La librería se quitó
> entera en v0.10.1.
>
> **Nunca adoptar una dependencia visual que no se puede ver.** Desde macOS, un cambio de estilo
> compila igual se vea bien o se vea un desastre.

---

## Configuración — `appsettings.json`

Nada de esto va hardcodeado en C#.

| Clave | Qué contiene |
|---|---|
| `Branding` | Nombre del producto y del técnico |
| `Thresholds` | 16 umbrales: disco bajo, RAM mínima, latencia, presupuesto de escaneo… |
| `Classifier` | Capa 1 (bloqueo duro) y capa 2 (lista blanca) del clasificador |
| `Bloatware` | Candidatos a quitar |
| `Reputation.KnownBad` | 51 PUPs, por publisher + producto |
| `Services` | Condiciones por servicio |
| `Network` | Destinos y timeouts de la prueba de conectividad |
| `WingetPackages` | Los 10 programas, con origen, historia, color e inicial |
| `Serilog` | Nivel de log |

Si hay un `appsettings.json` junto al `.exe`, gana sobre el embebido. Eso permite ajustar umbrales o
agregar un programa en un equipo sin recompilar nada.

Varias claves `$comment` documentan las reglas dentro del propio archivo — por ejemplo que las
historias llevan **hechos verificables, nunca anécdotas inventadas**.

---

## Pruebas — 619, y qué protege cada grupo

| Archivo | N | Qué protege |
|---|--:|---|
| `WingetServiceTests` | 39 | La tanda de instalación entera, más guardas sobre el catálogo real |
| `ServiceAndWingetTests` | 36 | Condiciones de servicio; un typo nunca desactiva nada |
| `FixRunnerTests` | 28 | Las cuatro compuertas de seguridad |
| `CrashAnalyzerTests` | 27 | Bugcheck, WHEA, cruce con actualizaciones |
| `HardwareAdvisorTests` | 26 | Cuándo se recomienda cambiar hardware |
| `StartupEntryTests` | 23 | Lectura de entradas de inicio y el bit de aprobación |
| `SafetyAndConfigTests` | 23 | `PathGuard`, IDs válidos, configuración coherente |
| `StartupClassifierTests` | 19 | Las tres capas |
| `ChecklistEvaluatorTests` | 19 | Que «no medido» nunca sea verde |
| `JournalAndUndoTests` | 18 | Deshacer, incluso entre sesiones |
| `RestorePointPolicyTests` | 16 | Cuándo se exige punto de restauración |
| `SnapshotHonestyTests` | 14 | Nullables y fallo cerrado |
| `DriverUpdateFixTests` | 13 | Que solo se instalen los `Safe` |
| `DiagnosticEngineTests` | 13 | Paralelismo, timeouts, fallo parcial |
| `AdvisorsTests` | 13 | Saturación y selección de software |
| `WingetBootstrapperTests` | 12 | Instalar winget cuando falta |
| `ReputationEvaluatorTests` | 12 | Match por publisher + producto |
| `ProgressTests` | 12 | Que el avance llegue a la interfaz |
| `NetworkDiagnosisTests` · `NetworkFindingTests` | 22 | Sin internet se dice sin internet |
| `JunctionSafeCleanerTests` | 11 | Incluye un symlink **real** en disco |
| `JournalStoreTests` | 11 | Ida y vuelta a disco real |
| `WingetNetworkGateTests` | 10 | Sin internet no se descarga nada |
| `ProcessOutputTests` | 9 | Normalización de salida |
| `PhysicalFileTreeIntegrationTests` | 9 | Disco real |
| `DirectDownloadResolveTests` | 9 | Resolución de origen + 4 guardas de catálogo |
| `DriverRiskClassifierTests` | 8 | El reparto en tres clases |
| `UsableAfterFailureTests` | 8 | La app sigue usable después de **cualquier** fallo |
| `AttentionSummaryTests` | 7 | Qué se destaca y qué se cuenta |
| `PaletteParityTests` | 3 | Las dos paletas con las mismas claves |
| `ViewModelSinWpfTests` | 2 | Que no entre WPF al ViewModel |
| `SingleFixRunTests` | 2 | Una corrida a la vez |

Dos se omiten fuera de Windows; en CI corren.

### Los tres niveles

- **Unitario** — lógica pura, sin disco ni red. La mayoría.
- **Integración** — disco real: journal, limpieza, symlinks.
- **E2E (`EasyFix.App.Tests`)** — maneja el `MainViewModel` como lo haría una persona, con dobles de
  proceso, descarga y conectividad. Sirve para reproducir un fallo reportado **sin internet y sin
  Windows**.

```bash
dotnet test --filter 'Category!=RequiresWindows'   # lo que corre en cualquier sistema
dotnet test --filter 'Category!=Integration'       # solo lógica pura
```

---

## Entrega

`Directory.Build.props` tiene `<Version>` en **un solo lugar**; `AssemblyVersion` y `FileVersion`
derivan de ahí y **nunca se escriben a mano** — estuvieron clavadas en `0.6.0.0` mientras la ventana
decía otra cosa, y era imposible saber qué binario estaba corriendo.

```
ci.yml       windows-latest, en cada push y PR: restore, build, test
release.yml  al empujar un tag v*.*.*
```

`release.yml` **verifica que el tag coincida con `Directory.Build.props`** y falla si no, corre los
tests en Windows, publica el `.exe`, extrae las notas de esa versión del CHANGELOG y crea el
Release. Sube dos archivos: `EasyFix-vX.Y.Z.exe` —para distinguirlos en un USB con varias
descargas— y `EasyFix.exe`, que mantiene vivo el link
`/releases/latest/download/EasyFix.exe`.

---

## Lo que nunca se ejecutó en Windows

Con el mismo criterio que la app aplica a sus diagnósticos:

| Qué | Por qué |
|---|---|
| La interfaz renderizada | WPF compila fuera de Windows pero no corre; ningún build la dibuja |
| `SystemProbe` | A propósito concentrado: una corrida real lo valida entero |
| El catálogo de controladores | COM por late binding; nunca se consultó Windows Update real |
| Los ProgId de predeterminados | Salen de la documentación, no del registro de un equipo |
| Que los instaladores corran callados | Los argumentos salen del manifiesto de winget, pero acá los ejecuta EasyFix |
| `InstallerIcon` | P/Invoke a `shell32`; si falla se ve el monograma |
| Los códigos de salida de winget | De la documentación. Uno no reconocido cae en `Failed` con el valor crudo: el peor caso es un fallo honesto |

Qué probar y en qué orden: [`PRUEBAS.md`](PRUEBAS.md).
