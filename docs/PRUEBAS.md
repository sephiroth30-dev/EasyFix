# Guía de pruebas en Windows

Qué probar, en qué orden, y qué capturar. El orden importa: va de lo que no puede romper nada a lo
que sí.

**Antes de empezar:** anotá la versión que aparece en la barra de título (hoy `v0.17.0`). Un reporte
sin versión no se puede atar a ningún build.

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
- [ ] La ventana abre y dice `EasyFix · por Andrés Hernández · v0.17.0`.
- [ ] Click derecho en el `.exe` → Propiedades → Detalles: aparece la empresa y el copyright, y la
      **versión del archivo dice 0.17.0.0**. Hasta v0.7.0 decía 0.6.0.0 mientras la ventana decía
      otra cosa, así que era imposible saber qué binario estaba corriendo.
- [ ] **El análisis arranca solo**, sin apretar nada. Antes había que pulsar «Analizar» y hasta
      entonces el resto de las acciones estaban bloqueadas, lo que se sentía como «solo funciona
      un botón».

**Si no arranca:** el log puede no haberse creado todavía. Capturá el mensaje de error textual.

---

## Nivel 0.5 — La interfaz · SEGURO, no modifica nada

**Nada de esto se vio nunca renderizado**: WPF compila fuera de Windows pero no corre, así que
ningún build dibuja la pantalla. Es lo primero a mirar, porque un problema acá afecta a todas las
pantallas siguientes.

- [ ] La ventana abre y **no hay barra lateral**. Se quitó entera: el espacio de la izquierda
      ahora lo usa el resumen.
- [ ] **Maximizar** con el botón ▢ y con doble clic en la barra de título. La ventana **no** debe
      tapar la barra de tareas. Restaurar y confirmar que vuelve al tamaño anterior.
- [ ] **Maximizada, el espacio extra se usa**: las columnas se ensanchan con la ventana en vez de
      quedar fijas con un hueco vacío al costado. Las líneas de texto no se vuelven
      incómodamente largas.
- [ ] **Redimensionar** arrastrando los bordes; no debería poder achicarse más allá de 860×560, y
      al achicar, las columnas no deberían aplastarse ni superponerse.
- [ ] **Tema claro**: poner Windows en «Modo de aplicación: Claro» y abrir EasyFix. Tiene que
      verse claro y **legible**. Volver a «Oscuro» y confirmar que vuelve al tema de siempre.
- [ ] **La barra de desplazamiento** es fina y moderna, estilo Chrome — no la barra gris con
      flechitas de Windows clásico.
- [ ] **El resumen central destaca solo lo que necesita atención.** Lo que está bien se cuenta
      («N revisados, M en orden»), no se enumera uno por uno.
- [ ] **No hay explicaciones ni descargos técnicos en pantalla** en ningún lado.

### Qué capturar
Captura de pantalla de cada una de las 7 pantallas (Inicio, Analizando, Reporte, Instalar,
Reparando, Resultado, Deshacer), **en los dos temas**. Es lo único que permite ajustar
anchos/márgenes y contraste sin adivinar.

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

Solo aplica a un equipo sin winget. Si no tenés ninguno a mano, probar en una VM Windows 11 recién
instalada SIN haber abierto nunca la Microsoft Store (así nunca se aprovisiona el paquete por
usuario) — es el escenario real que reportó el primer uso.

> **Desde v0.17.0 este nivel dejó de ser crítico.** Ninguno de los diez programas necesita winget;
> el bootstrap existe para «Actualizar lo instalado». Si falla, la instalación de los diez tiene
> que seguir funcionando igual — eso es parte de lo que se prueba acá.

- [ ] Con winget ausente, apretar «Instalar» en la pantalla de Apps (con al menos un programa
      marcado). **No debería bloquearse ni pedir nada**: tiene que descargar e instalar winget
      solo, y seguir con la instalación del programa en la misma corrida.
- [ ] **Si el bootstrap falla, los diez programas se instalan igual.** Un fallo de winget ya no
      puede dejar al técnico sin nada.
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

## Nivel 1.9 — Controladores · MODIFICA el equipo (instala drivers reales)

**El más riesgoso de todos los niveles.** El catálogo de drivers usa COM tardío: ningún build
valida que la API exista, así que esta es literalmente la primera vez que el código corre. Probar
primero en una VM con snapshot, nunca directo en el equipo de un cliente.

- [ ] Correr «Reparar errores» con internet. En el log tiene que aparecer
      `Catálogo de drivers: N actualización(es) disponibles` — si dice «No se pudo consultar»,
      copiar el error textual: ahí está el nombre del miembro COM que no existe.
- [ ] **Lo que se instaló solo tiene que ser únicamente de clase segura** (red, video, audio, USB,
      Bluetooth, impresora, entrada). Si se instaló un driver de almacenamiento, es un bug grave
      del clasificador — reportarlo con el título exacto que mostró.
- [ ] **Si había firmware/BIOS en el catálogo, NO puede haberse instalado.** Tiene que aparecer
      solo nombrado, con el aviso de ir al fabricante.
- [ ] Los de almacenamiento aparecen listados con su nombre y sin instalar.
- [ ] Sin internet, «Reparar errores» tiene que saltear este paso con el motivo visible («necesita
      internet»), no fallar.
- [ ] Comparar los títulos que muestra EasyFix contra lo que ofrece
      `Configuración → Windows Update → Opciones avanzadas → Actualizaciones opcionales`:
      deberían ser los mismos drivers.

### Qué capturar
El log completo (trae los títulos exactos y la clasificación de cada uno) + captura de
«Actualizaciones opcionales» de Windows para comparar. **Si algo se clasificó mal, el título
textual es el dato que permite corregir el clasificador.**

---

## Nivel 2 — Instalar programas · MODIFICA el equipo

Instala software. En un equipo propio primero.

**Lo que esta versión cambia:** ninguno de los diez pasa por winget. Si winget falta o falla, los
diez tienen que instalarse igual. Esa es la prueba central del nivel.

- [ ] «Instalar programas» muestra los **10 programas, 6 marcados**.
- [ ] **Los que ya están instalados aparecen en gris y desmarcados**, con «Ya instalado». No hay
      que apretar Instalar para enterarse.
- [ ] Empezar por **7-Zip**: chico, rápido e inofensivo.
- [ ] Después **Chrome** y **RustDesk**, que van por descarga directa.
- [ ] Después **Adobe Reader** y **VLC**, que son los nuevos de v0.17.0 — resuelven su versión
      consultando el origen en el momento.
- [ ] Al final el resto.

> **Adobe Reader pesa unos 800 MB.** Es el instalador oficial x64, el mismo que baja winget; no
> existe una versión liviana. En una conexión lenta son 10–15 minutos solo para ese programa.

### Lo que hay que mirar

| Estado en la fila | Qué significa |
|---|---|
| `Instalado` | Lo instaló ahora |
| `Ya estaba` / `Ya instalado` | Estaba desde antes. **No es un fallo** |
| `No se encontró un instalador` | El origen cambió de formato. Hay que revisar `matchPattern` en `appsettings.json` |
| `No está en el catálogo` | El ID cambió. Corregirlo en `appsettings.json` |
| `Falló` | Trae el código de error en rojo debajo del nombre |

- [ ] **Una barra de avance por programa**, con los MB que lleva (`12 MB de 28 MB`). No una sola
      barra global.
- [ ] **El logo real** del programa mientras se instala, no una letra. Los que van por winget y
      los instaladores sin icono muestran el monograma, y eso es lo esperado.
- [ ] **La historia se alcanza a leer.** 7-Zip instala en segundos; su historia igual tiene que
      quedar en pantalla el tiempo suficiente.
- [ ] **Todo en silencio.** Ninguna ventana de instalador, ningún «Windows Installer», ningún
      diálogo esperando un Enter. Si aparece alguna, capturarla: es un argumento silencioso
      equivocado.
- [ ] **Cancelar cancela.** Empezar la descarga de VLC o Adobe, apretar otro programa o Cancelar, y
      confirmar que la descarga anterior **se corta** en vez de seguir en segundo plano.
- [ ] **Marcar durante la instalación**: con la tanda corriendo, marcar un programa que estaba
      desmarcado. Tiene que instalarse en la misma corrida, sin apretar «Instalar» de nuevo.
      Desmarcar uno que todavía no arrancó: no debería instalarse.

### La prueba que importa — sin winget

En un equipo que no tenga winget (Windows 10 anterior a 1809, o una instalación recién hecha):

- [ ] Instalar los 10. **Los 10 tienen que entrar igual.** Si alguno dice «winget no está
      disponible», eso es el fallo que estas dos versiones vinieron a cerrar: capturar cuál.

### Qué capturar

1. Captura de la lista con los estados finales.
2. El log — tiene la URL resuelta de cada programa y su código de salida exacto.
3. Si algo abrió una ventana, la captura de esa ventana con su texto.

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
Versión:        v0.17.0
Equipo:         (marca, modelo, Windows 10/11, build)
Nivel probado:  0 / 1 / 2 / 3 / 4

Qué hice:
Qué esperaba:
Qué pasó:

Adjunto: captura + C:\ProgramData\EasyFix\logs\easyfix-<fecha>.log
```

Lo que más valor tiene en un reporte: **qué debería decir en lugar de lo que dijo.** Un «está mal» sin
el valor correcto no permite arreglar el contrato de WMI.
