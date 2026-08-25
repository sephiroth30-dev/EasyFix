# EasyFix

Herramienta de técnico para Windows 10 y 11. Un `.exe` portable que llevás en USB: diagnostica el
equipo, aplica las mejoras seguras con un click, instala el software base y repara los errores
comunes del sistema. Todo reversible.

**Estado: compila y los tests pasan.** `dotnet build` limpio en los tres proyectos —incluido el de
WPF— y **193 tests pasan, 2 se omiten** (los que dependen de Windows).

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
| Shell de la UI en WPF (ventana, tema, DI, logging) | ✅ compila; **sin ejecutar** (necesita Windows) |
| Spike de contratos de Windows (`tools/spike`) | escrito, **sin correr** |
| Los `IDiagnosticCheck` concretos (WMI), los fixes, el wizard de perfil | **pendiente: necesitan Windows** |

Lo que falta es, casi todo, código que *llama* a Windows. La lógica que *decide* ya está escrita y
verificada: qué se puede tocar, qué recomendar, cómo deshacerlo y en qué orden.

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
dotnet test           # 119 pasan, 2 se omiten fuera de Windows
```

```bash
dotnet test --filter 'Category!=RequiresWindows'   # solo lo que corre en cualquier sistema
dotnet test --filter 'Category!=Integration'       # solo lógica pura, sin tocar el disco
```

### Ejecutar: solo en Windows

```powershell
dotnet run --project src\EasyFix.App    # pide UAC al arrancar
```

El `.exe` portable para el USB (se puede *publicar* desde macOS, no ejecutar):

```bash
dotnet publish src/EasyFix.App -c Release
# salida: un solo EasyFix.exe (~70 MB), sin runtime a instalar en el equipo del cliente
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

El plan completo, con las 12 fases y sus tests, está en
`~/.claude/plans/ayudame-instalando-la-skill-snuggly-blanket.md`.

---

## Próximos pasos

1. Levantar las dos VMs (fase 0) — sin ellas nada de esto se puede verificar.
2. Correr el spike en Win10 y Win11, diffear (fase 1).
3. Scaffold de la UI y diagnóstico rápido read-only (fases 2–3).
