# EasyFix — resumen para quien trabaje en la web

Documento autocontenido. No hace falta leer el código ni el resto del repo.

---

## Qué es

**EasyFix** es una herramienta de técnico para Windows 10 y 11. Un solo archivo `.exe` portable
(~69 MB) que se lleva en un pendrive: diagnostica el equipo del cliente, aplica las mejoras seguras,
repara errores del sistema e instala el software base.

Autor: **Andrés Hernández**. Versión actual: **0.17.0** — en pruebas en equipos reales.

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
| **Buscar controladores** | Actualiza drivers desde Windows Update, por clase de riesgo | Minutos |
| **Instalar programas** | Chrome, Acrobat Reader, 7-Zip, Visual C++ Redistributable, RustDesk y más | Variable |
| **Deshacer la última reparación** | Revierte los cambios, incluso semanas después | Segundos |

El análisis **arranca solo al abrir la aplicación**: no hay que apretar nada para ver el estado del
equipo.

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
- Instalar firmware o BIOS — un flasheo fallido es daño físico, no «reinstalar»
- Desinstalar programas por su cuenta — los reporta y el técnico decide

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

**Cada programa se baja de su origen oficial, directamente del fabricante.** Esto importa más de lo
que parece: las herramientas que usan un gestor de paquetes dependen de que ese gestor esté
instalado y funcionando, y cuando no lo está el técnico se queda sin nada. Acá los diez se instalan
igual.

Cada URL se verificó pidiéndola de verdad antes de configurarla. **Ninguna fuente se escribe de
memoria.**

Agregar un programa es una entrada en un archivo de configuración, sin recompilar.

### Mientras instala, cuenta una historia

Cada programa muestra un hecho real sobre su origen mientras se descarga, con su logo y una barra de
avance propia con los megabytes. Una instalación de varios minutos con una barra quieta se siente
eterna y deja al técnico sin nada que decirle al cliente.

### Programas predeterminados

Deja Chrome como navegador y Acrobat Reader para los PDF, por la vía oficial de Microsoft. **Alcance
real: los perfiles de usuario que se creen después** — para un técnico que prepara un equipo antes de
entregarlo, es justo lo que hace falta. Para el perfil que ya está abierto, Windows bloquea el cambio
automático desde 2024, y EasyFix no se pelea con esa protección.

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

**Es v0.17.0 y se está probando en equipos reales.** Se publica en GitHub Releases con cada
versión, pero **la web todavía no debería ofrecerlo como producto terminado**, por dos razones
concretas:

1. **El ejecutable no está firmado digitalmente.** Windows SmartScreen lo marca como desconocido y
   Defender puede clasificarlo como riesgoso, porque toca claves del registro y servicios. Firmarlo
   cuesta entre 100 y 300 dólares al año. **Es el único bloqueo que no se resuelve programando.**
2. **Cada ronda de pruebas en equipos reales sigue encontrando fallos** — las últimas tres versiones
   salieron de reportes de uso en sitio, no de una lista de funciones pendientes.

Lo que **sí** está probado en equipos reales: el diagnóstico completo, el punto de restauración, las
reparaciones de archivos de sistema, disco, Windows Update y red, y la instalación de programas.

Lo que **nunca se ejecutó en Windows**, dicho con el mismo criterio que la app aplica a sus
diagnósticos: cómo se ve la interfaz renderizada, el catálogo de controladores de Windows Update, y
que los instaladores corran en silencio con los argumentos configurados.

### Cómo se trabaja el proyecto

Dos reglas que explican por qué las versiones salen como salen, y que valen como argumento de
confianza:

- **Nunca se afirma algo que no se midió.** Es la regla de la que salieron cuatro versiones seguidas
  de correcciones: la herramienta reportaba diagnósticos que no había verificado.
- **Nunca se ajusta una prueba para que pase.** Si una prueba falla, se corrige el producto o se
  explica por qué la prueba estaba mal — y se para después de tres intentos en vez de insistir.

### Qué se puede afirmar y qué no

| Se puede decir | No se puede decir |
|---|---|
| «Diagnostica y dice qué necesita el equipo, con números medidos» | «Acelera tu PC un 200 %» |
| «Cada cambio es reversible, con punto de restauración verificado» | «Repara cualquier error de Windows» |
| «Se niega a trabajar si el disco está fallando» | «Arregla los pantallazos azules» (los **diagnostica**; solo los arregla si la causa es software) |
| «Dice qué no puede arreglar el software» | «Optimiza el registro» (no lo hace, a propósito) |
| «Un solo archivo, sin instalar nada» | «Compatible con todas las versiones de Windows» (es 10 y 11) |
| «Instala el software base desde el origen oficial de cada fabricante» | «Instala cualquier programa» (son diez, curados) |
| «Actualiza controladores seguros; el resto los nombra» | «Actualiza todos los controladores» (firmware y BIOS, nunca) |
| «En desarrollo, en pruebas» | Ofrecer descarga pública como producto terminado |

---

## Datos técnicos

| | |
|---|---|
| Plataforma | Windows 10 y 11, 64 bits |
| Tecnología | C# / .NET 8 con WPF |
| Distribución | Un `.exe` autocontenido de ~69 MB, sin instalador |
| Permisos | Requiere administrador (declarado en el manifiesto) |
| Interfaz | Ventana redimensionable y maximizable (mínimo 860 × 560), tema claro y oscuro automático, en español |
| Telemetría | **Ninguna.** No envía datos a ningún servidor |
| Registros | En el equipo del cliente, en `C:\ProgramData\EasyFix\` |
| Pruebas automatizadas | 619, corriendo en Windows en cada cambio |
| Repositorio | https://github.com/sephiroth30-dev/EasyFix |

### Identidad visual actual

Dos paletas —clara y oscura— que siguen el tema de Windows. En oscuro: fondo `#16181D`, superficie
`#1E2127`, texto `#F2F4F8`, acento `#4C9AFF`, advertencia `#E8B657`, crítico `#FF6B6B`, correcto
`#5EC98A`. Tipografía Segoe UI Variable. Cero gradientes, cero iconos decorativos, mucho espacio en
blanco.

**Minimalismo y esencialismo, en serio:** no hay barra lateral, no hay botones para cosas que la
aplicación debería hacer sola, y **no hay explicaciones ni descargos técnicos en pantalla**. El
resumen destaca únicamente lo que necesita atención; lo que está en orden se cuenta, no se enumera.
Cada programa muestra su logo real mientras se instala.

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
