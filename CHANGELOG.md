# Changelog

Todas las versiones de EasyFix, de la más nueva a la más vieja.

**Convención de versionado** — SemVer adaptado a que esto no es una librería:

| Parte | Cuándo sube |
|---|---|
| MAJOR | Cambio que rompe `appsettings.json` o el formato del journal |
| MINOR | Capacidad nueva: un fix, una sonda, una pantalla |
| PATCH | Corrección de algo que estaba mal |

La versión vive en un solo lugar: `Directory.Build.props`. Se ve en la barra de título de la app y en
Propiedades → Detalles del `.exe`.

Cada versión probada en un equipo real lleva su resultado anotado. Lo que no se probó se dice.

---

## [0.3.0] — 2026-08-25

Primera versión que **corrige**, no solo diagnostica. Y la primera que se corrigió a partir de una
prueba en un equipo de verdad.

### Agregado

- **Análisis de pantallazos azules.** Catálogo de 23 códigos de parada con su primer sospechoso
  (RAM, disco, driver, video, archivos de sistema, hardware). Lee el bugcheck de los eventos 1001,
  los eventos WHEA, los apagones Kernel-Power 41, los errores de disco y los volcados.
- **Cruce con actualizaciones.** Para cada pantallazo, qué se instaló en los 7 días anteriores. Si
  ninguno cae en esa ventana, se dice que la hipótesis no se sostiene: descartar también es un dato.
- **Seis reparaciones:** DISM `/RestoreHealth` + `sfc`, `chkdsk`, reset de Windows Update, reset de
  la pila de red, prueba de memoria programada, y desinstalar la actualización correlacionada.
- **`FixRunner` con cuatro compuertas de seguridad**, todas en un solo lugar para que ningún fix las
  esquive: disco fallando aborta, sin punto de restauración aborta, BitLocker sin confirmar bloquea
  lo que toca arranque o disco, equipo en dominio entra en modo restringido.
- **Punto de restauración verificado.** Windows aplica un límite de 24 h: `CreateRestorePoint`
  devuelve éxito y no crea nada. Se compara la secuencia antes y después.
- **Instalación de programas con winget**, en serie, en silencio, siempre la última versión.
- **RustDesk**, y con él la descarga directa desde el origen oficial.
- **Marca del técnico**: barra de título, metadata del ejecutable y `appsettings.json`.
- **Versión visible** en la ventana.

### Corregido — de la prueba en un equipo real

- **El punto de restauración bloqueaba todo.** En muchos equipos Restaurar sistema viene
  deshabilitado de fábrica. Se agrega un override explícito, nunca por defecto: el journal sigue
  registrando cada cambio, así que «Deshacer todo» funciona igual; lo que se pierde es el respaldo
  del sistema completo.
- **«Falló» sin decir por qué.** El motivo estaba en el log y nadie abre un log en la casa de un
  cliente. Ahora el código y la explicación van en la fila, y hay un botón «Diagnosticar winget».
- **winget elevado no ve su propio catálogo.** `Microsoft.Winget.Source` se instala por usuario y la
  sesión de administrador no lo tiene → `0x8A15000F`. Se detecta y se auto-repara con
  `source reset --force`. Ver [winget-cli#698](https://github.com/microsoft/winget-cli/issues/698).
- **RustDesk fue removido de winget** en 2026 porque ESET lo marcó como `RemoteAdmin.RustDesk`, un
  falso positivo. Ver [winget-pkgs#368229](https://github.com/microsoft/winget-pkgs/issues/368229).
  Se baja de la API de releases de GitHub.

### Estado de las pruebas

| Componente | Estado |
|---|---|
| Diagnóstico (solo lectura) | ⚠️ probado una vez; el resumen del equipo hay que verificarlo |
| Instalación de programas | ❌ falló en la primera prueba; corregido, **sin reprobar** |
| Las seis reparaciones | ❌ **nunca ejecutadas** |
| `RestorePointService`, `BitLockerService` | ❌ **nunca ejecutados** |
| Análisis de pantallazos | ❌ **nunca ejecutado** contra un equipo con pantallazos |

---

## [0.2.0] — 2026-08-25

### Agregado

- **`SystemProbe`**: la única clase que habla con Windows. Lee WMI, registro y Event Log, y devuelve
  un `SystemSnapshot`. Todo el resto del diagnóstico queda como lógica pura, testeable en cualquier
  sistema.
- **Motor de recomendación de hardware.** Equipo sano devuelve lista vacía; un dato no medido nunca
  genera recomendación.
- **Motor de diagnóstico** en paralelo, con timeout por chequeo: un chequeo colgado no impide que
  salga el reporte.
- **Evaluador de condiciones de servicio.** Existe por `SysMain`, que en disco mecánico *mejora* el
  rendimiento.
- **Parser de resultados de winget.** «Ya estaba instalado» no es un fallo.
- **UI en WPF**: inicio, analizando, reporte, instalar programas.
- **Módulo winget** con 17 tests.

### Corregido

- `DiagnosticEngine` se comía la cancelación del usuario: el `catch` general atrapaba también
  `OperationCanceledException` y devolvía un reporte falso lleno de «no se pudo determinar».
- `FileJournalSink` usaba `Path.GetInvalidFileNameChars()`, que depende del sistema anfitrión. En
  Unix no incluye `:`, así que un `runId` con timestamp ISO generaba un nombre inválido en Windows.
- El tipo `Classification` chocaba con el namespace del mismo nombre. Renombrado a `StartupVerdict`.
- `InMemoryFileTree` pisaba el atributo `ReparsePoint` del directorio padre, y el test del junction
  estaba probando otra cosa mientras pasaba en verde.

---

## [0.1.0] — 2026-08-24

Núcleo de lógica pura, todo verificable sin Windows.

### Agregado

- **Clasificador de 3 capas** para decidir qué se puede desactivar: por certificado Authenticode, no
  por nombre de archivo. Un malware llamado `MyVPN` no queda protegido; `PulseSecure` sí.
- **Journal en formato JSON Lines** y motor de deshacer. Una escritura truncada por un crash pierde
  una línea, no el archivo.
- **Limpiador de temporales que no sigue junctions.** Verificado con un symlink real.
- **`SafeProcessRunner`**: `ArgumentList`, sin shell, ruta absoluta obligatoria.
- `PathGuard`, parser de certificados, validación de IDs de winget, loader de configuración.
- Spike de verificación de contratos de Windows (`tools/spike`), sin correr.

### Descubierto

- **WPF sí compila fuera de Windows** con `EnableWindowsTargeting=true`. La documentación previa
  afirmaba lo contrario y bloqueaba el desarrollo de la UI detrás de una VM innecesaria.
- Los contadores de rendimiento están **localizados**: `\Memory\Committed Bytes` no existe en un
  Windows en español. Se usan las clases `Win32_PerfFormattedData_*`.
