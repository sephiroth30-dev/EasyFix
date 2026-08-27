# EasyFix — por Andrés Hernández

Herramienta de técnico para Windows 10 y 11. Un `.exe` portable que llevás en USB: diagnostica el
equipo, aplica las mejoras seguras con un click, instala el software base y repara los errores
comunes del sistema. Todo reversible.

**Estado: compila y los tests pasan.** `dotnet build` limpio en los tres proyectos —incluido el de
WPF— y **324 tests pasan, 2 se omiten** (los que dependen de Windows).

| Componente | Estado |
|---|---|
| Clasificador de 3 capas | ✅ 20 tests |
| Journal JSONL y motor de deshacer | ✅ 25 tests (incluye ida y vuelta a disco real) |
| Limpiador junction-safe de temporales | ✅ 15 tests, incluido uno con **un symlink real** |
| Motor de diagnóstico (paralelo, timeout por chequeo, rápido vs. profundo) | ✅ 13 tests |
| Motor de recomendación de hardware | ✅ 26 tests |
| Evaluador de condiciones de servicio (el caso `SysMain` en HDD) | ✅ 15 tests |
| Parser de resultados de winget | ✅ 13 tests |
| Runner de procesos endurecido, `PathGuard`, certificados, config | ✅ |
| Reglas de hallazgos de software (inicio, BitLocker, dominio) | ✅ lógica pura |
| UI en WPF: inicio, analizando, reporte | ✅ compila; **sin ejecutar** (necesita Windows) |
| `SystemProbe` — la capa de WMI, registro y Event Log | ⚠️ escrita, **sin ejecutar ni testear** |
| Spike de contratos de Windows (`tools/spike`) | escrito, **sin correr** |
| Módulo winget: instalar programas | ✅ 17 tests; el localizador de `winget.exe` sin ejecutar |
| Análisis de pantallazos azules: bugcheck, WHEA, cruce con actualizaciones | ✅ 47 tests |
| `FixRunner` con las cuatro compuertas de seguridad | ✅ 21 tests |
| Reparaciones: DISM/SFC, chkdsk, red, Windows Update, prueba de memoria, quitar actualización | ⚠️ escritas, **sin ejecutar** |
| `RestorePointService`, `BitLockerService` | ⚠️ escritos, **sin ejecutar** |
| Los handlers de deshacer, el wizard de perfil | **pendiente** |

`SystemProbe` es la única pieza no verificada, y es a propósito: concentra todas las llamadas a
Windows en un archivo, así que correr el `.exe` en un equipo real la valida entera de una vez. Lo que
*decide* —qué se puede tocar, qué recomendar, cómo deshacerlo y en qué orden— ya está escrito y
testeado.

### Compilar desde macOS o Linux

Contrario a lo que decía una versión anterior de este README: **WPF sí compila fuera de Windows.**
Basta `<EnableWindowsTargeting>true</EnableWindowsTargeting>` en el proyecto de la app — el SDK baja
el reference pack de `Microsoft.WindowsDesktop.App` y el XAML se compila y valida igual.

Lo que **no** se puede fuera de Windows:
- **Ejecutar** el `.exe`.
- Probar cualquier cosa que toque WMI, el registro, servicios o puntos de restauración.

O sea: la VM Windows sigue siendo imprescindible para *probar los fixes*, pero **no** para desarrollar
y verificar la lógica ni la UI. Casi todo `EasyFix.Core` es lógica pura y se testea en cualquier
sistema.

Desviaciones conscientes del plan: la configuración se lee con `System.Text.Json` en vez de
`Microsoft.Extensions.Configuration`, y el acceso a archivos usa un puerto propio de 9 métodos
(`IFileTree`) en vez de `System.IO.Abstractions`. Motivo: menos superficie de API, y `IFileTree` sin
borrado recursivo hace que el borrado peligroso sea imposible de invocar por descuido.

---

## Lo que esta app promete — y lo que no

No existe el "+200% de rendimiento" por software. Lo que sí se puede medir:

| Intervención | Ganancia real | Quién la hace |
|---|---|---|
| HDD → SSD | Boot 300–1000%. La mejora #1, sin competencia | Hardware (EasyFix lo **recomienda**) |
| RAM 4→8/16 GB en equipo que swapea | Elimina los congelamientos | Hardware (EasyFix lo **recomienda**) |
| Quitar antivirus duplicado / PUP / bloatware | 20–60% en un equipo lleno de basura | EasyFix |
| Desactivar startup pesado | 10–40% del tiempo de arranque | EasyFix |
| Temporales + caché de WU + WinSxS | Espacio en disco. Perf solo si `C:` estaba por debajo del 10% libre | EasyFix |
| Plan de energía, TRIM, defrag correcto | 0–15% | EasyFix |
| Equipo que ya está sano | **0–5%. Se dice y listo.** | — |

EasyFix reporta **milisegundos y GB medidos**, no porcentajes inventados. La métrica de arranque sale
del Event ID 100 de `Microsoft-Windows-Diagnostics-Performance/Operational`, campo `MainPathBootTime`,
comparando antes y después de un reinicio real.

## Lo que EasyFix se niega a hacer

Mitos del "optimizador de PC" que están **excluidos por diseño**, documentados acá para que no se
cuelen más adelante:

- Borrar Prefetch — **empeora** el arranque, Windows lo reconstruye.
- Limpiadores de registro — cero ganancia medible, riesgo real.
- "Optimizadores de RAM" — recortar working sets hace que todo se vuelva más lento.
- Desfragmentar un SSD — desgasta celdas sin ningún beneficio.
- Desactivar el archivo de paginación.
- `DISM /ResetBase` — impide desinstalar actualizaciones.
- Listas de servicios copiadas de foros — rompen Windows Update, Store, audio e impresión.
- Desactivar Defender.
- "Tweaks gamer" de resolución de timer, CPU unparking.
- Borrar `WinSxS` a mano.
- Instalar drivers automáticamente. Nunca. Un driver equivocado deja el equipo inservible.

---

## Cómo se decide qué se toca

El punto más delicado del proyecto. **No se usa match por nombre de archivo**: `*VPN*` protegería a
un malware llamado *MyVPN* y no protegería a *Pulse Secure*. La decisión es de tres capas, y la
primera que diga "no" gana:

1. **Bloqueo duro** — producto de seguridad registrado en `root\SecurityCenter2`, firmado por
   Microsoft Windows, con servicios dependientes en ejecución, o con un driver asociado. Intocable.
2. **Lista blanca positiva** — solo lo que está en `appsettings.json` como par
   *(publisher Authenticode, producto)* **con firma válida** se desactiva en automático.
3. **Zona gris** — todo lo demás. Checkbox, nunca automático. **Desconocido = pedir permiso.**

Los binarios sin firmar caen en Capa 3 y se marcan en rojo como posible malware.

## Reversibilidad

- Punto de restauración **verificado** antes del primer cambio. Si no se puede crear, se aborta.
- Journal por corrida en `%ProgramData%\EasyFix\runs\<runId>.json`, escrito **antes** de cada cambio.
  Si la app muere a mitad de camino, el undo sigue funcionando.
- Botón "Deshacer todo".
- El borrado de archivos se marca `reversible: false` y se dice en el reporte. No se miente sobre el undo.

### BitLocker

`chkdsk /f` y los resets de red pueden alterar la medición de integridad del TPM y provocar que
Windows pida la clave de recuperación de 48 dígitos al reiniciar. Si el cliente no la tiene, queda
fuera de su propio equipo. EasyFix detecta BitLocker, exige confirmación explícita de que la clave
está a mano, y suspende la protección antes de esos fixes. Sin confirmación, quedan bloqueados.

### Equipos de dominio

Con `PartOfDomain = true` la app entra en **modo restringido**: solo temporales, cachés y
diagnóstico. Las GPO revierten los cambios de servicios y startup, y desactivar agentes corporativos
rompe el equipo para el área de TI.

---

## Desarrollo

### Compilar y testear (macOS, Linux o Windows)

Solo hace falta el **.NET 8 SDK**. En macOS/Linux, sin `sudo`:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
```

```bash
git clone <repo> && cd EasyFix
dotnet build          # los tres proyectos, incluido el de WPF
dotnet test           # 324 pasan, 2 se omiten fuera de Windows
```

```bash
dotnet test --filter 'Category!=RequiresWindows'   # solo lo que corre en cualquier sistema
dotnet test --filter 'Category!=Integration'       # solo lógica pura, sin tocar el disco
```

### Generar el `.exe` portable

Se puede publicar desde macOS o Linux; ejecutarlo, solo en Windows.

```bash
dotnet publish src/EasyFix.App -c Release -o publish
```

Sale un único `publish/EasyFix.exe` — `PE32+ executable (GUI) x86-64`, **69 MiB**. Trae el runtime de
.NET adentro comprimido, así que no hay que instalar nada en el equipo del cliente. Se copia al
pendrive y listo; no hay instalador ni nada que desinstalar.

Al abrirlo, Windows pide permiso de administrador (va declarado en el manifiesto): sin elevación no
se puede leer el estado SMART del disco ni consultar los servicios. Y como el `.exe` no está firmado,
SmartScreen lo va a marcar como desconocido — «Más información» → «Ejecutar de todas formas».

### Qué hace la versión actual

**Diagnóstico** (solo lectura) e **instalación de programas**. Los botones que aplicarían cambios al
sistema avisan que no están conectados en lugar de simular trabajo.

- **Analizar el equipo** mide en serio —tipo y salud del disco, RAM y presión de memoria, espacio
  libre, programas de inicio con su retraso medido, tiempo de arranque, antivirus activos, BitLocker,
  dominio— y muestra el reporte separado en *lo que puedo arreglar* y *lo que necesita hardware*.
- **Instalar programas** instala con winget, en serie, en silencio.

### Todo se descarga en el momento

Nada viene empaquetado dentro del `.exe`: los 69 MiB son solo el runtime de .NET. winget baja cada
programa del repositorio oficial de Microsoft en el instante de instalar, así que **siempre entra la
última versión publicada** y no hay que regenerar el ejecutable cuando Chrome saque una versión nueva.
El equipo del cliente necesita internet.

Agregar un programa es una línea en `appsettings.json`; el ID se busca con `winget search <nombre>`.

| Programa | ID de winget | Por defecto |
|---|---|:-:|
| Google Chrome | `Google.Chrome` | ✓ |
| Adobe Acrobat Reader | `Adobe.Acrobat.Reader.64-bit` | ✓ |
| 7-Zip | `7zip.7zip` | ✓ |
| Visual C++ Redist x64 / x86 | `Microsoft.VCRedist.2015+.x64` / `.x86` | ✓ |
| **RustDesk** | `RustDesk.RustDesk` | ✓ |
| VLC · Notepad++ · Firefox · AnyDesk | — | |

**Sobre encontrar `winget.exe` en un proceso elevado:** winget se instala como paquete MSIX *por
usuario* y se invoca por un alias en `%LOCALAPPDATA%\Microsoft\WindowsApps`. Al elevar, esa variable
puede resolver al perfil del administrador, donde el alias no existe. Por eso se busca primero la
instalación real del paquete bajo `C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*`, que
es global, y el alias queda como respaldo. Nunca se resuelve por `PATH`.

```powershell
dotnet run --project src\EasyFix.App    # desde el repo, en Windows
```

### La VM Windows: para qué sigue siendo imprescindible

No para compilar. Para **probar los fixes**, que es lo que puede romper el equipo de un cliente:

1. **VMware Fusion Pro** (gratis; *Fusion Player* fue discontinuado por Broadcom en mayo 2024) o
   VirtualBox 7, con **Windows 11 x64**.
2. Una **segunda VM "conejillo"** con Windows sucio a propósito (muchos startup, temporales,
   bloatware) y snapshots. Es el único lugar seguro para probar los fixes destructivos y para medir
   el antes/después del arranque, que requiere reiniciar de verdad.

### Fase 1 — spike de contratos (hacer antes de escribir código de diagnóstico)

Todo el diseño se apoya en contratos de WMI, registro, Event Log y contadores de rendimiento. Este
script los consulta en el equipo real e imprime los valores verdaderos. Es **solo lectura**.

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\spike\Verify-WindowsApis.ps1
```

Correrlo **elevado**, en Windows 10 **y** en Windows 11, y diffear las dos salidas: las diferencias
son casos condicionales que el código va a tener que manejar.

Detecta además una trampa importante: los nombres de contador de rendimiento están **localizados**
(en Windows en español `\Memory\Committed Bytes` no existe), así que el código usa las clases
`Win32_PerfFormattedData_*`, cuyos nombres de propiedad son iguales en cualquier idioma.

---

## Arquitectura

```
src/EasyFix.App/     WPF. Ventana, XAML, ViewModels. Cero lógica de negocio.
src/EasyFix.Core/    Toda la lógica. Sin referencia a WPF (verificado por test de arquitectura).
  Diagnostics/       IDiagnosticCheck — read-only, siempre
  Fixes/             IFix — CanApply / Apply, con journal
  Rollback/          RunJournal, RestorePointService, UndoEngine
  Measurement/       BootTimeReader — el antes/después honesto
  Recommendations/   HardwareAdvisor
  Apps/              WingetService
  Abstractions/      IRegistry, IProcessRunner, IWmiQuery, IEventLog — para poder mockear
tools/spike/         Verify-WindowsApis.ps1
appsettings.json     Umbrales y listas curadas. Nada de esto va hardcodeado en C#.
```

**Procesos externos:** siempre `ProcessStartInfo.ArgumentList` (nunca strings concatenados),
`UseShellExecute = false`, ruta absoluta al ejecutable, y toda ruta canonicalizada y verificada bajo
su raíz esperada antes de borrar o copiar.

**Borrado de temporales:** enumeración manual comprobando `FileAttributes.ReparsePoint`. Un junction
dentro de `%TEMP%` apuntando a `Documents` convertiría la limpieza en borrado de datos del cliente.

## Documentación

| Documento | Para qué |
|---|---|
| [`docs/PRUEBAS.md`](docs/PRUEBAS.md) | Qué probar en Windows, en qué orden, y qué capturar cuando algo falla |
| [`docs/PLAN.md`](docs/PLAN.md) | Documento de trabajo: estado actual, decisiones, qué falta y en qué orden |
| [`CHANGELOG.md`](CHANGELOG.md) | Versiones, con el resultado de cada prueba real |

La versión vive en `Directory.Build.props` — un solo lugar. Se ve en la barra de título de la app y en
Propiedades → Detalles del `.exe`.

---

## Próximos pasos

1. Levantar las dos VMs (fase 0) — sin ellas nada de esto se puede verificar.
2. Correr el spike en Win10 y Win11, diffear (fase 1).
3. Scaffold de la UI y diagnóstico rápido read-only (fases 2–3).
