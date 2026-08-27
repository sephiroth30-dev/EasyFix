# EasyFix — resumen para quien trabaje en la web

Documento autocontenido. No hace falta leer el código ni el resto del repo.

---

## Qué es

**EasyFix** es una herramienta de técnico para Windows 10 y 11. Un solo archivo `.exe` portable
(~69 MB) que se lleva en un pendrive: diagnostica el equipo del cliente, aplica las mejoras seguras,
repara errores del sistema e instala el software base.

Autor: **Andrés Hernández**. Versión actual: **0.6.0** — pre-lanzamiento.

**No es un instalador.** No se instala nada en el equipo del cliente, no escribe en el registro al
copiarse, y no hay nada que desinstalar. Se copia, se ejecuta, se retira el pendrive.

## Para quién

Técnicos que reparan PCs **en sitio**, en equipos ajenos. Ese contexto define todo el diseño: el
trabajo se hace una vez, en la casa del cliente, sin poder volver fácil si algo sale mal.

---

## Qué hace — cinco acciones

| Acción | Qué hace | Cuánto tarda |
|---|---|---|
| **Analizar el equipo** | Mide disco, RAM, CPU, arranque, antivirus, pantallazos azules. **Solo lectura, no modifica nada.** | Menos de 15 s |
| **Mejorar rendimiento** | Limpia temporales, quita programas del inicio, ajusta el disco y la energía | Minutos |
| **Reparar errores** | DISM, `sfc`, `chkdsk`, reset de Windows Update y de la red | Hasta una hora |
| **Instalar programas** | Chrome, Acrobat Reader, 7-Zip, Visual C++ Redistributable, RustDesk y más | Variable |
| **Deshacer la última reparación** | Revierte los cambios, incluso semanas después | Segundos |

### El diagnóstico separa dos cosas

Es la decisión de producto más importante y conviene que la web la refleje:

- **Lo que se puede arreglar con software** — temporales, programas de inicio, archivos de sistema
  corruptos, antivirus duplicados.
- **Lo que necesita hardware** — disco mecánico, RAM insuficiente, disco muriendo, batería
  degradada, sobrecalentamiento.

Cada punto viene con **el número medido que lo respalda**: «el arranque tarda 94 s», «la memoria
comprometida está al 91 %», «11 programas arrancan con Windows». Nunca porcentajes inventados.

---

## Lo que EasyFix se niega a hacer

Esta lista es tan importante como la de funciones, y **es el diferenciador frente a la categoría de
«optimizadores de PC»**. Están excluidos por diseño, documentados en el repo para que no se cuelen:

- Borrar Prefetch — **empeora** el arranque, Windows lo reconstruye
- Limpiadores de registro — cero ganancia medible, riesgo real
- «Optimizadores de RAM» — recortar working sets hace que todo vaya más lento
- Desfragmentar un SSD — desgasta celdas sin ningún beneficio
- Desactivar el archivo de paginación
- Listas de servicios copiadas de foros — rompen Windows Update, Store, audio e impresión
- Desactivar Windows Defender
- «Tweaks gamer» de resolución de timer
- Instalar drivers automáticamente — un driver equivocado deja el equipo inservible

---

## El modelo honesto de rendimiento

**Crítico para cualquier texto de la web: no existe el «+200 % de rendimiento» por software.**
Prometerlo es exactamente lo que hace humo a esta categoría de herramientas. Lo que sí se puede
medir:

| Intervención | Ganancia real | Quién la hace |
|---|---|---|
| HDD → SSD | Arranque 300–1000 %. La mejora #1, sin competencia | Hardware. EasyFix lo **recomienda** |
| RAM 4→8/16 GB en equipo que usa disco como memoria | Elimina los congelamientos | Hardware. EasyFix lo **recomienda** |
| Quitar antivirus duplicado, PUP y bloatware | 20–60 % en un equipo lleno de basura | EasyFix |
| Desactivar programas de inicio pesados | 10–40 % del tiempo de arranque | EasyFix |
| Limpiar temporales | Espacio en disco. Rendimiento solo si `C:` estaba por debajo del 10 % libre | EasyFix |
| Plan de energía, TRIM, defrag correcto | 0–15 % | EasyFix |
| **Equipo que ya está sano** | **0–5 %. Se dice y listo.** | — |

Si el equipo está bien, el reporte aparece vacío. **Cuando no hay nada que vender, la respuesta
correcta es no vender nada.**

---

## Seguridad y reversibilidad — el argumento central

Todo esto se puede decir con confianza porque está implementado:

- **Punto de restauración verificado** antes del primer cambio. No alcanza con pedirlo: Windows
  aplica un límite de un punto por día y devuelve éxito sin crear nada, así que EasyFix **espera y
  confirma que existe**. Si no se puede crear, avisa y no toca nada salvo que el técnico lo autorice
  explícitamente.
- **Cada cambio se registra antes de aplicarse.** Si el programa se cierra a mitad, el deshacer sigue
  funcionando.
- **«Deshacer todo» funciona entre sesiones.** El registro vive en el disco del cliente, así que se
  puede volver al equipo semanas después y revertir.
- **Lo que no se puede recuperar se dice.** Los archivos temporales borrados no vuelven, y el reporte
  lo aclara en vez de prometer un deshacer completo que no existe.
- **Disco fallando → no toca nada.** Si SMART predice una falla, EasyFix se niega a trabajar:
  escribir acelera la pérdida de datos. Solo informa y recomienda respaldar.
- **BitLocker.** Reparar el disco puede hacer que Windows pida la clave de recuperación de 48 dígitos
  al reiniciar. EasyFix lo detecta, exige confirmación explícita de que la clave está a mano, y
  suspende la protección antes. Sin confirmación, esas reparaciones quedan bloqueadas.
- **Equipos de empresa.** En un equipo unido a un dominio entra en modo restringido: las políticas
  corporativas revertirían los cambios y desactivar agentes de gestión rompería el equipo para el
  área de sistemas.

### Cómo decide qué programas de inicio desactivar

Vale la pena contarlo porque es lo que lo separa de un limpiador cualquiera:

**No usa coincidencia de nombres.** Un patrón como `*VPN*` protegería a un malware llamado
`MyVPN.exe` y dejaría desprotegido a `PulseSecure.exe`. La decisión es por **certificado digital del
fabricante** en tres capas:

1. **Intocable** — producto de seguridad registrado en Windows, componente del sistema, con servicios
   dependientes o con un driver asociado.
2. **Lista blanca** — solo lo que está autorizado por par *(fabricante del certificado, producto)*
   **con firma válida** se desactiva sin preguntar.
3. **Todo lo demás** — casilla, nunca automático. **Desconocido = pedir permiso.**

---

## Instalación de programas

**Todo se descarga en el momento**, así que siempre entra la última versión publicada. Nada viene
empaquetado en el `.exe`: no hay instaladores que envejezcan. Requiere internet en el equipo.

Vienen preconfigurados: Google Chrome, Adobe Acrobat Reader, 7-Zip, Visual C++ Redistributable
(x64 y x86), **RustDesk**, VLC, Notepad++, Firefox, AnyDesk.

- **7-Zip en lugar de WinRAR** porque es libre, sin recordatorio de pago y comprime mejor.
- **RustDesk** para soporte remoto: es libre y se puede autohospedar, a diferencia de AnyDesk o
  TeamViewer.
- **Visual C++ Redistributable** merece estar en la lista: arregla la mayoría de los errores de
  «falta tal DLL» después de reinstalar Windows.

Agregar un programa es una línea en un archivo de configuración, sin recompilar.

---

## Diagnóstico de pantallazos azules

Función diferenciada que casi nadie tiene. EasyFix lee el código de parada del registro de eventos y
lo traduce a su **primer sospechoso**: memoria, disco, driver, driver de video, archivos de sistema o
hardware. Catálogo de 23 códigos.

Y hace un cruce que responde la pregunta habitual: **para cada pantallazo, qué actualizaciones de
Windows se instalaron en los 7 días anteriores.** Si ninguno cae en esa ventana, lo dice
explícitamente: la hipótesis de que fue una actualización no se sostiene. Descartar también es
información, y ahorra horas.

Si hay errores WHEA —el hardware informando fallas al sistema— EasyFix dice que es hardware y que
ninguna reparación de software lo resuelve.

---

## Estado real del proyecto — importante para la web

**Es pre-lanzamiento (v0.6.0). No está listo para distribuir.** La web **no debería ofrecer descarga
todavía**, por tres razones concretas:

1. **El ejecutable no está firmado digitalmente.** Windows SmartScreen lo marca como desconocido y
   Defender puede clasificarlo como riesgoso, porque toca claves del registro y servicios. Firmarlo
   cuesta entre 100 y 300 dólares al año.
2. **Varias funciones se escribieron pero nunca se ejecutaron en Windows.** Concretamente los cuatro
   fixes de rendimiento, el lector de programas de inicio, el botón de deshacer y la instalación de
   Chrome y RustDesk por descarga directa.
3. **Se está probando en equipos reales y todavía aparecen fallos** en cada ronda de pruebas.

Lo que **sí** está probado en equipos reales: el diagnóstico completo, el punto de restauración, y
las reparaciones de archivos de sistema, disco, Windows Update y red.

### Qué se puede afirmar y qué no

| Se puede decir | No se puede decir |
|---|---|
| «Diagnostica y dice qué necesita el equipo, con números medidos» | «Acelera tu PC un 200 %» |
| «Cada cambio es reversible, con punto de restauración verificado» | «Repara cualquier error de Windows» |
| «Se niega a trabajar si el disco está fallando» | «Arregla los pantallazos azules» (los **diagnostica**; solo los arregla si la causa es software) |
| «Dice qué no puede arreglar el software» | «Optimiza el registro» (no lo hace, a propósito) |
| «Un solo archivo, sin instalar nada» | «Compatible con todas las versiones de Windows» (es 10 y 11) |
| «En desarrollo, en pruebas» | Ofrecer descarga pública |

---

## Datos técnicos

| | |
|---|---|
| Plataforma | Windows 10 y 11, 64 bits |
| Tecnología | C# / .NET 8 con WPF |
| Distribución | Un `.exe` autocontenido de ~69 MB, sin instalador |
| Permisos | Requiere administrador (declarado en el manifiesto) |
| Interfaz | Ventana fija de 460 × 700, tema oscuro, un solo color de acento (`#4C9AFF`), en español |
| Telemetría | **Ninguna.** No envía datos a ningún servidor |
| Registros | En el equipo del cliente, en `C:\ProgramData\EasyFix\` |
| Repositorio | Privado |

### Identidad visual actual

Tema oscuro. Fondo `#16181D`, superficie `#1E2127`, texto `#F2F4F8`, acento `#4C9AFF`, advertencia
`#E8B657`, crítico `#FF6B6B`, correcto `#5EC98A`. Tipografía Segoe UI Variable. Cero gradientes, cero
iconos decorativos, mucho espacio en blanco.

El título de la ventana dice **«EasyFix — por Andrés Hernández»** con la versión al lado.

---

## Tono recomendado para la web

El proyecto entero está construido sobre un principio: **un diagnóstico que miente es peor que uno
que falta.** El texto de la web debería sostener eso.

Concreto:

- Hablar de **medir**, no de **acelerar**.
- Decir lo que la herramienta **no** hace. Es el diferenciador real frente a los limpiadores que
  prometen todo.
- Nada de porcentajes de mejora, barras de progreso decorativas ni «tu PC está en riesgo».
- El público es técnico. Trata bien la precisión.
