# Guía de pruebas en Windows

Qué probar, en qué orden, y qué capturar. El orden importa: va de lo que no puede romper nada a lo
que sí.

**Antes de empezar:** anotá la versión que aparece en la barra de título (hoy `v0.5.0`). Un reporte sin
versión no se puede atar a ningún build.

---

## Dónde queda todo

| Qué | Dónde |
|---|---|
| Logs | `C:\ProgramData\EasyFix\logs\easyfix-<fecha>.log` |
| Registro de cada reparación | `C:\ProgramData\EasyFix\runs\<runId>.jsonl` |
| Configuración editable | `appsettings.json` junto al `.exe` (si no está, usa el embebido) |

El log tiene el detalle de cada sonda, cada llamada a winget con su código de salida, y cada fix.
**Es lo primero que hay que mandar cuando algo falla.**

---

## Nivel 0 — Que arranque

- [ ] Doble click → aparece el aviso de administrador (UAC) → aceptar.
- [ ] SmartScreen dice que es desconocido → «Más información» → «Ejecutar de todas formas».
- [ ] La ventana abre y dice `EasyFix · por Andrés Hernández · v0.8.0`.
- [ ] Click derecho en el `.exe` → Propiedades → Detalles: aparece la empresa y el copyright, y la
      **versión del archivo dice 0.8.0.0**. Hasta v0.7.0 decía 0.6.0.0 mientras la ventana decía otra
      cosa, así que era imposible saber qué binario estaba corriendo.

**Si no arranca:** el log puede no haberse creado todavía. Capturá el mensaje de error textual.

---

## Nivel 1 — Diagnóstico · SEGURO, no modifica nada

Se puede correr en cualquier equipo, incluso de un cliente, sin riesgo. Es todo solo lectura.

- [ ] «Analizar el equipo» termina en menos de 15 segundos.
- [ ] **El resumen del equipo es correcto:** sistema operativo, modelo, procesador, RAM, tipo de
      disco y espacio libre. **Este es el chequeo más importante de todos**: valida los contratos de
      WMI, que nunca se probaron.
- [ ] ¿Aparece la sección **«NO SE PUDO DETERMINAR»**? Cada línea ahí es una sonda con el contrato
      equivocado.

### Qué capturar

1. Captura de pantalla del reporte completo.
2. El log de `C:\ProgramData\EasyFix\logs\`.
3. Si algo del resumen está mal, **qué debería decir**. Ejemplo: «dice Disco no identificado y es un
   SSD Kingston de 480 GB».

### Comparación manual, si algo no cuadra

```powershell
Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer, Model
Get-CimInstance Win32_Processor      | Select-Object Name, NumberOfCores
Get-PhysicalDisk                     | Select-Object MediaType, HealthStatus, Size
Get-CimInstance Win32_PhysicalMemory | Select-Object Capacity, Speed, SMBIOSMemoryType
```

### Un favor aparte, que cierra tres pendientes

Si alguno de los equipos tiene **winget 1.6 o superior** (`winget --version`), correr esto y
mandarme el archivo:

```powershell
winget error --output "$env:USERPROFILE\Desktop\winget-errores.txt"
```

Es la tabla completa de códigos de error de winget. Con eso se verifican de una las tres constantes
que hoy están transcritas de memoria y marcadas `SIN VERIFICAR` en el código. En winget 1.24 y
anteriores el comando no existe y devuelve `INVALID_CL_ARGUMENTS` — es lo que pasó en el equipo del
2026-08-29, y por eso las constantes fueron la única fuente ahí.

---

## Nivel 1.2 — Lo nuevo de v0.9.0 · SEGURO, no modifica nada

Mismo «Analizar el equipo» del Nivel 1; esto agrega qué mirar de los módulos nuevos.

- [ ] **Sección «ESTADO DEL EQUIPO»** en el reporte: 12 condiciones con ✓ / ✗ / —. Ninguna
      condición puede estar en ✓ si su dato salió en «NO SE PUDO DETERMINAR». Las dos de escaneo
      profundo (DISM/SFC y chkdsk) salen «—» siempre: es lo esperado.
- [ ] **Programas con mala reputación**: si el equipo tiene algún PUP conocido (Driver Booster,
      Web Companion, etc.), ¿aparece con publisher, categoría y tamaño? Y al revés, **lo legítimo
      no puede salir condenado** — TeamViewer/AnyDesk/antivirus de pago jamás deben aparecer.
      Si hay un falso positivo, anotar el `DisplayName` y `Publisher` exactos del registro.
- [ ] **Antivirus por nombre**: con dos antivirus activos, el hallazgo debe nombrarlos y
      recomendar dejar solo Defender si no hay licencia paga.
- [ ] **Batería** (en laptop): `bateria.presente = True` y un desgaste plausible en el log.
      En desktop: `False` y sin desgaste. Contratos de `root\wmi` nunca probados — si salen «no
      medido» en una laptop, es un contrato equivocado y hay que anotar qué devuelve
      `Get-CimInstance -Namespace root/wmi -ClassName BatteryFullChargedCapacity` en PowerShell.
- [ ] **En el log** (`easyfix-<fecha>.log`): el volcado del snapshot ahora distingue `no medido` de
      `False`/`0` en bitlocker, dominio, impresoras, bluetooth, antivirus, smart y batería.

Después de «Mejorar rendimiento» (Nivel 2.5), además:

- [ ] **«ESTADO DEL EQUIPO — ANTES Y DESPUÉS»** aparece en el resultado, con `antes ✗ → después ✓`
      en lo que se arregló. La re-medición corre sola al terminar; tarda ~10-15 s más.
- [ ] Lo que dependa de reiniciar (arranque, inicio liviano) debe salir IGUAL en las dos columnas —
      si «mejora» sin reiniciar, es un dato inventado y hay que reportarlo como bug.

## Nivel 1.5 — Sin internet · SEGURO, no modifica nada

**La prueba más importante de v0.8.0**, y la única que reproduce a propósito el fallo del
2026-08-29. Es solo lectura y se hace en cualquier equipo: se desconecta la red y se vuelve a
conectar.

### Preparación

Desenchufar el cable de red **y** apagar el wifi. En una laptop alcanza con activar el modo avión.

### Con la red desconectada

- [ ] «Analizar el equipo» termina normal. **El análisis no debe tardar más que con red**: la
      comprobación de internet corre en paralelo con las sondas, no después.
- [ ] En el reporte aparece un aviso naranja: **«El equipo no está conectado a ninguna red»**.
- [ ] Ese aviso dice que «Instalar programas» no va a funcionar, **y también** que analizar, mejorar
      el rendimiento y reparar errores sí funcionan sin internet.
- [ ] En el log, la última línea del resumen dice `internet = NoAdapter`.
- [ ] Marcar los programas → «Instalar»: **los cuatro dicen «sin internet»**, no «falló» ni «sin
      catálogo de winget».
- [ ] El resumen de abajo dice **«No se instaló nada: el equipo no tiene internet»**. No debe decir
      «4 con error».
- [ ] **Lo más importante, en el log:** NO puede aparecer ninguna línea de `winget.exe`, ni
      «SourceUnavailable», ni «sesión elevada», ni «Fuentes de winget reparadas». Sin conexión no se
      lanza ningún proceso. Si aparece cualquiera de esas líneas, la compuerta no funcionó.
- [ ] Toda la operación de instalar debe terminar en **menos de 10 segundos**: es una comprobación de
      8 s como techo y nada más.

### Volviendo a conectar la red

- [ ] Analizar de nuevo: el aviso naranja **desaparece** y el log dice `internet = Online`.
- [ ] Instalar: funciona normal.

### Caso opcional, si se puede

Conectarse a un wifi con portal cautivo (hotel, cafetería, aeropuerto) **sin** aceptar su página.
Esperado: el aviso dice **«La red exige iniciar sesión en el navegador»**, no «no hay internet». El
log dice `internet = CaptivePortal`.

### Qué capturar

1. Captura del reporte con el aviso naranja.
2. El log completo de la corrida sin red — sirve sobre todo para confirmar que **no** hay líneas de
   winget.

---

## Nivel 1.8 — Bootstrap de winget · MODIFICA el equipo (instala un paquete Appx)

Solo aplica a un equipo donde «Diagnosticar winget» diga que no lo encuentra. Si no tenés ninguno a
mano, probar en una VM Windows 11 recién instalada SIN haber abierto nunca la Microsoft Store (así
nunca se aprovisiona el paquete por usuario) — es el escenario real que reportó el primer uso.

- [ ] Con winget ausente, apretar «Instalar» en la pantalla de Apps (con al menos un programa
      marcado). **No debería bloquearse ni pedir nada**: tiene que descargar e instalar winget
      solo, y seguir con la instalación del programa en la misma corrida.
- [ ] Mirar el log: debe aparecer `Bootstrap de winget: True. winget se instaló...` y, antes de
      declarar éxito, tiene que haber vuelto a buscar `winget.exe` — no alcanza con que
      `Add-AppxPackage` no tirara error.
- [ ] Si falla (p. ej. falta Microsoft.UI.Xaml), el resultado del paquete tiene que decir
      explícitamente que se intentó instalar winget y por qué no se pudo — nunca «WingetMissing»
      a secas como si no se hubiera intentado nada.
- [ ] Repetir «Instalar» con un SEGUNDO programa en la misma corrida (o reabrir la app si la
      primera corrida ya terminó): si el bootstrap anterior funcionó, winget tiene que resolverse
      normal sin volver a intentar el bootstrap.
- [ ] Verificar con `winget --version` desde una consola aparte que efectivamente quedó instalado
      para ESTE usuario (no solo para la sesión elevada de EasyFix).

### Qué capturar
Log completo + salida de `winget --version` manual + captura de la pantalla de Apps al terminar.

---

## Nivel 2 — Instalar programas · MODIFICA el equipo

Instala software. En un equipo propio primero.

- [ ] «Instalar programas» muestra los 10 programas, 6 marcados.
- [ ] **«Diagnosticar winget» primero.** Muestra dónde está winget, su versión y si lee su catálogo.
      Es lo que dice si el problema de la prueba anterior quedó resuelto.
- [ ] Instalar solo **7-Zip** para empezar: es chico, rápido e inofensivo.
- [ ] Después **RustDesk** y **Chrome**, que van por descarga directa y no por winget:
      RustDesk desde GitHub (prefiere el MSI), Chrome desde el MSI empresarial de Google.
- [ ] Al final el resto.

### Lo que hay que mirar

| Estado en la fila | Qué significa |
|---|---|
| `Instalado` | Lo instaló ahora |
| `Ya estaba` | Estaba desde antes. **No es un fallo** |
| `winget sin catálogo` | El problema de la sesión elevada. Debería auto-repararse y reintentar |
| `No está en el catálogo` | El ID cambió. Hay que corregirlo en `appsettings.json` |
| `Falló` | Trae el código de error en rojo debajo del nombre |

Los dos que vienen de descarga directa tienen **timeout propio** —8 min RustDesk, 10 min Chrome— en
vez de los 30 min generales. Si uno se cuelga, ya no bloquea toda la tanda.

### Qué capturar

1. La salida completa de «Diagnosticar winget».
2. Captura de la lista con los estados.
3. El log — tiene el código de salida exacto de cada paquete.

---

## Nivel 2.5 — Mejorar rendimiento · MODIFICA el equipo

> **Advertencia.** Borra archivos temporales de forma **no reversible** y desactiva programas de
> arranque. Lo segundo sí se puede deshacer; lo primero no.

Es más rápido que reparar —minutos, no una hora— y es lo que más se nota en un equipo lento.

1. [ ] Analizar el equipo primero.
2. [ ] **«Mejorar rendimiento»**. Debería tardar unos minutos, no cuarenta.
3. [ ] Mirar el resultado, fix por fix:

| Fix | Qué esperar |
|---|---|
| Limpiar archivos temporales | MB liberados. Si encontró enlaces, lo dice: **es la protección funcionando** |
| Quitar programas del inicio | Los que desactivó, y cuántos quedaron esperando tu confirmación |
| Ajustar el disco a su tipo | TRIM en SSD, desfragmentación en mecánico. **Nunca desfragmentar un SSD** |
| Plan de energía | Requiere aprobación; en portátil afecta la batería |

4. [ ] **Verificar en el Administrador de tareas → Inicio** que los programas que dice haber quitado
       aparezcan como «Deshabilitado». Eso confirma que escribió el flag correcto.
5. [ ] Probar el **deshacer** y verificar que esos programas vuelven a «Habilitado».

### Lo que hay que mirar con cuidado

- ¿Desactivó algo que **no** debía? Antivirus, driver de audio, VPN, panel de la placa de video. Si
  pasa, es un fallo grave del clasificador y hay que reportarlo con el nombre exacto del programa.
- ¿Dejó en «necesita confirmación» algo que sí es seguro? Menos grave, pero indica que falta en la
  lista blanca de `appsettings.json`.

---

## Nivel 3 — Reparar · MODIFICA el sistema

> **Advertencia.** Este nivel corre DISM, `sfc` y `chkdsk`, puede desinstalar una actualización de
> Windows y puede programar tareas para el próximo arranque. Nunca lo pruebes por primera vez en el
> equipo de un cliente.
>
> Antes de empezar, verificá que podés entrar en Modo seguro: **Shift + Reiniciar** → Solucionar
> problemas → Opciones avanzadas → Configuración de inicio.

Pasos, en orden:

1. [ ] Analizar el equipo primero. «Reparar» necesita el diagnóstico.
2. [ ] **Si el equipo tiene BitLocker:** aparece un aviso rojo. Antes de marcar la casilla, conseguí
       la clave de recuperación de 48 dígitos y guardala en otro lado. Si no la tenés, dejá la
       casilla sin marcar: las reparaciones de disco se saltean y el resto corre igual.
3. [ ] Si no puede crear el punto de restauración, aparece la casilla para continuar sin él. Probá
       **primero sin marcarla**, para confirmar que aborta como debe.
4. [ ] Después marcala y confirmá que aplica los cambios igual.
5. [ ] Reiniciar si lo pide, y volver a analizar: el tiempo de arranque se compara contra el anterior.
6. [ ] Volver al inicio: aparece **«Deshacer la última reparación»** con la fecha y la cantidad de
       cambios reversibles.
7. [ ] Abrirlo: lista qué se revierte y qué no se puede recuperar, **sin aplicar nada todavía**.
8. [ ] **«Deshacer todo»** y verificar que el estado vuelve.
9. [ ] Cerrar la app, volver a abrirla: el botón de deshacer **ya no debe aparecer** para esa corrida.

### Lo que hay que mirar

- ¿Se creó el punto de restauración? Verificalo en `rstrui.exe`.
- ¿Cada reparación dice `Hecho`, `No hacía falta` o `Falló` con su motivo?
- ¿Se creó el archivo del journal en `C:\ProgramData\EasyFix\runs\`?

### Qué capturar

1. Captura de la pantalla de resultado.
2. El archivo `.jsonl` del journal completo.
3. El log.

---

## Nivel 4 — El equipo con pantallazos azules

Solo diagnóstico primero. Es seguro.

- [ ] «Analizar el equipo» y buscar la sección de pantallazos en el reporte.
- [ ] Verificar que el **código de parada** que muestra coincide con lo que dice el Visor de eventos.
- [ ] Verificar la **conclusión sobre las actualizaciones**: si dice que coinciden o que no.

### Qué capturar

1. Captura de la sección de pantallazos.
2. La salida de `tools\Diagnose-BlueScreen.ps1`, que hace el mismo análisis desde PowerShell y sirve
   para contrastar si la app leyó bien los eventos.

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Diagnose-BlueScreen.ps1
```

**No repares ese equipo todavía.** Primero hay que confirmar que el diagnóstico es correcto: si el
código apunta a RAM o disco, reparar no sirve y el reporte lo tiene que decir.

---

## Plantilla para reportar

```
Versión:        v0.5.0
Equipo:         (marca, modelo, Windows 10/11, build)
Nivel probado:  0 / 1 / 2 / 3 / 4

Qué hice:
Qué esperaba:
Qué pasó:

Adjunto: captura + C:\ProgramData\EasyFix\logs\easyfix-<fecha>.log
```

Lo que más valor tiene en un reporte: **qué debería decir en lugar de lo que dijo.** Un «está mal» sin
el valor correcto no permite arreglar el contrato de WMI.
