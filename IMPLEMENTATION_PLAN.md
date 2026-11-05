# Cold Ham implementation plan

## Goal

Build a simple Windows desktop application in .NET that lets a user maintain website and application block lists, activate them immediately or on a weekly schedule, and reverse every system change cleanly.

## Product scope

### MVP

1. A single WPF window with four practical areas: Overview, Websites, Applications, and Schedule.
2. Website rules entered as domains. Active rules are written to an app-owned section of the Windows hosts file and removed when blocking stops.
3. Application rules entered as executable names or selected `.exe` paths. While blocking is active, matching processes are detected and terminated after explicit user confirmation when enabling the block.
4. Manual Start/Stop controls plus an optional weekly time window.
5. Local JSON persistence, an activity log, validation, duplicate prevention, and clear administrator-rights errors.
6. A small test project covering domain normalization, hosts-file updates, schedule decisions, and configuration persistence.
7. An opt-in website allowlist mode for Chrome and Edge: block all website navigation except explicitly allowed domains during manual sessions and weekly schedules.

### Deliberately deferred

- Browser extensions, URL-path or keyword filtering, incognito statistics, cloud sync, remote administration, password locks, donation breaks, and anti-tamper kernel/service components.
- Blocking Microsoft Store apps by package identity.
- Cross-platform support.

## Architecture

- `src/ColdHam.App`: WPF presentation and application composition.
- `src/ColdHam.Core`: models, validation, scheduling, configuration storage, and enforcement abstractions.
- `src/ColdHam.Windows`: Windows hosts-file and process enforcement implementations.
- `tests/ColdHam.Core.Tests`: dependency-free executable test harness so the repository can build and test offline.

The app runs enforcement in-process on a short timer. This keeps the MVP transparent and easy to uninstall. A Windows service can be introduced later if enforcement must continue after the UI exits.

## Safety and reversibility

- Hosts-file changes are bounded by `# Cold Ham BEGIN` and `# Cold Ham END` markers.
- Applying a website block replaces only that marked section; stopping removes only that section.
- Domain input is normalized and restricted to valid host names. Wildcards and raw hosts-file lines are rejected.
- Executable rules retain full paths when supplied. Protected names, executables under Windows, unverifiable paths, wildcards and Cold Ham itself are excluded from termination.
- The app does not attempt anti-uninstall or privilege escalation.
- The allowlist mode owns only URL policy values it creates under the current user's Chrome/Edge registry policies. Existing user/machine URL policies cause an actionable conflict instead of being overwritten. A durable local recovery journal records ownership before writes; Stop and startup recovery remove only matching owned values.

## Interface direction

- Visual thesis: compact native Windows utility, neutral surfaces, one blue action color, and status-led hierarchy.
- Content plan: current status and master action; editable website list; editable application list; weekly schedule; recent activity.
- Interaction thesis: immediate state feedback when blocking changes, inline validation on rule entry, and unobtrusive status/log updates. No ornamental motion.

## Implementation sequence

1. Create solution/project structure and ignore rules.
2. Implement domain models, validation, schedule evaluation, and JSON storage.
3. Implement bounded hosts-file transformation and Windows process enforcement.
4. Build the WPF interface and wire commands, timers, persistence, and elevation-aware errors.
5. Add tests and run `dotnet build` plus the test harness.
6. Perform a smoke test that uses a temporary hosts file and a harmless process fixture; never modify the real hosts file during automated tests.

## Definition of done

- The solution builds without errors.
- Tests pass without administrator rights and without touching the machine hosts file.
- Rules survive restart.
- Starting and stopping a block are visibly reflected in the UI and activity log.
- Website blocking reports when elevation is required.
- Stopping a block removes all app-owned hosts entries and leaves unrelated content byte-for-byte equivalent, including boundary whitespace and encoding.

## First reliability delivery

- Web state is based on completed hosts operations, separately from application monitoring. Failures remain visible, including failure to remove rules, with an explicit retry action. “En hosts” describes file enforcement rather than claiming a browser connectivity test.
- A failed stop keeps recovery controls available and prevents silent exit with unresolved web rules. Stopping enforcement remains possible even if configuration saving fails.
- Application consent is a domain policy in `ApplicationBlockConsent`: confirmation covers the exact rule set for one active session, including future launches of matching applications. Startup, scheduled activation and rule changes use the same policy. Rejection suspends application enforcement and is not repeatedly prompted by the timer; retry requests a new decision.
- UI save and enforcement operations cannot overlap. Process sweeps run off the UI thread; errors or unverified process exits are reported.
- Hosts transformations require one exact marker pair and preserve all unrelated bytes. Ambiguous or incomplete markers fail without editing. UTF-8 BOM, UTF-16 and legacy byte encodings are preserved. An exclusive write handle prevents concurrent writers during mutation; an adjacent `.coldham-recovery` snapshot is written before mutation and write exceptions trigger rollback. A crash or disk failure may still require manual repair; the app never restores an entire old snapshot over unrelated edits. Correct ambiguous markers externally, then retry from the app.
- Configuration saves retain the previous version as `settings.json.bak` and serialize concurrent writes. Corrupt or structurally unsafe settings are copied to a unique `.corrupt-*` file. Recovery uses a valid backup when available and always disables manual and scheduled activation. Failed reads do not permit overwriting unknown settings.
- Automated reliability tests use temporary files only and suppress DNS flushing for injected hosts paths. Coverage includes exact byte preservation, invalid markers, file locks, permission failures, recovery, concurrent saves, consent transitions and schedule boundaries.

### Manual acceptance checks

1. Start without elevation with web and application rules: web failure stays visible; application consent is separate.
2. Reject consent at startup, during scheduled activation, and after adding an application: no unauthorized process is closed; retry asks again.
3. Deny access to hosts while stopping: state remains unconfirmed, retry remains available, and close does not silently exit.
4. Verify narrow-window layout and the scrollable recovery message area; confirm that editing and stopping work again after each completed operation.

## Segunda entrega: plan detallado de implementación

Estado: implementado el 8 de septiembre de 2026. La comprobación manual de bandeja, UAC y escalado permanece pendiente porque no se ejecutó una interfaz Windows durante la validación automatizada.

### Objetivo y alcance

Permitir sesiones manuales de concentración con duración definida, mantener la aplicación accesible desde la bandeja del sistema y terminar una sesión sin alterar el horario semanal. Conservar los estados reales, las confirmaciones y las garantías de recuperación de la primera entrega.

Incluido:

- Sesiones de 25 minutos, 50 minutos, duración personalizada y bloqueo manual sin límite como opción explícita.
- Cuenta atrás, hora prevista de finalización y restauración de sesiones todavía vigentes al volver a abrir Cold Ham.
- Icono y menú en la bandeja; cerrar la ventana la oculta mientras el proceso sigue funcionando.
- Acciones independientes para terminar la sesión, desactivar el horario y detener todo.
- Control del ciclo de vida, prevención de instancias simultáneas y coordinación del reinicio elevado.
- Pruebas del dominio y de la coordinación mediante archivos temporales, reloj controlado y adaptadores simulados.

Fuera de esta entrega: perfiles, varias franjas semanales, pausas/Pomodoro repetitivo, omitir una franja, inicio automático con Windows, servicio de Windows, extensión de navegador y componentes de resistencia a la desinstalación. La bandeja mantiene vivo el proceso; no garantiza ejecución después de terminarlo, cerrar sesión o apagar el equipo.

### Decisiones de comportamiento

| Situación o acción | Resultado esperado |
| --- | --- |
| Iniciar una sesión de 25/50 minutos | Se guarda una fecha límite absoluta y se solicita aplicar las reglas actuales. |
| Duración personalizada | Minutos enteros entre 1 y 1440; validación visible antes de empezar. Es una decisión de producto modificable, no un límite técnico. |
| Iniciar sin límite | Se conserva el comportamiento manual actual mediante una opción explícita. |
| Ya existe una sesión manual | No se inicia otra ni se sustituye silenciosamente; primero debe terminarse la actual. |
| Termina o expira la sesión; no hay horario activo | Se detiene la vigilancia y se intenta retirar la sección propia de hosts. |
| Termina o expira la sesión; el horario está activo | Continúa el bloqueo por horario y se explica el motivo; no se retiran reglas intermedias. |
| Desactivar el horario durante una sesión | La sesión manual continúa hasta su fecha límite o cancelación. |
| Detener todo | Tras confirmar, termina la sesión y desactiva el horario; se intenta retirar las reglas aunque falle el guardado. |
| Pulsar X o Alt+F4 | Se oculta la ventana si la bandeja está disponible, sin alterar sesión, horario ni consentimiento. |
| Minimizar | Se mantiene el comportamiento normal de minimizar; no se introduce otra preferencia en esta entrega. |
| Salir desde la bandeja | Si hay sesión o incidencias de retirada, se explica y confirma su finalización; se retiran las reglas, se detiene el proceso y se conserva el horario configurado para la próxima apertura. |
| Salir con horario habilitado | Se avisa de que el horario no funcionará mientras Cold Ham esté cerrado; no se deshabilita la programación guardada. |
| Falla la retirada al salir | No hay salida silenciosa: se muestra la ventana con el error y los controles de recuperación. |
| Falla la creación del icono | La ventana permanece accesible; cerrar ofrece una salida explícita en lugar de ocultar el único punto de control. |

La cuenta atrás representa la duración solicitada, no el tiempo de bloqueo exitoso. Si falla hosts o se rechaza el cierre de aplicaciones, el plazo sigue avanzando y la interfaz muestra la incidencia. No se amplía automáticamente la sesión.

### Modelo de datos y migración

1. Añadir una versión explícita al documento de configuración. Tratar los JSON existentes sin versión como versión 1.
2. Sustituir el booleano como fuente de verdad por `ManualSession`, opcional, con `Id`, `StartedAtUtc` y `EndsAtUtc`. Una fecha final nula indica sesión indefinida; ausencia de sesión indica bloqueo manual desactivado.
3. Conservar `ManualBlockEnabled` únicamente en la lectura de la versión anterior. Migrar `false` a ausencia de sesión y `true` a sesión indefinida, para no inventar una fecha de caducidad para usuarios existentes.
4. Validar identificador, fechas y coherencia temporal. Ante datos inválidos, usar el flujo de recuperación actual sin activar sesiones ni horarios recuperados.
5. Guardar la configuración migrada con copia previa y mediante el mecanismo de sustitución existente. Si falla la migración persistida, mostrar la incidencia sin sobrescribir el original ni activar cambios no guardados.
6. Rechazar una versión futura desconocida con un mensaje claro; no degradarla automáticamente a una configuración vacía que después pueda sobrescribirse.
7. No persistir consentimiento de cierre de procesos. Al arrancar un nuevo proceso Cold Ham, la sesión restaurada requiere de nuevo autorización para cerrar aplicaciones.
8. No guardar cada segundo. Persistir al iniciar, terminar, expirar o cambiar configuración. Las escrituras repetidas por un error de expiración tendrán reintentos controlados y aviso, sin volver a considerar activa una sesión caducada.

### Tiempo, suspensión y recuperación

- Usar `DateTimeOffset` en UTC para las fechas límite manuales e inyectar `TimeProvider` para poder probarlas sin esperas reales.
- Calcular el tiempo restante como `max(0, EndsAtUtc - ahoraUtc)`. Nunca reconstruir la sesión sumando la duración original al iniciar la aplicación.
- Mantener el horario semanal en hora local. Tras reanudar el equipo o detectar un cambio de hora, reevaluar conjuntamente sesión y horario antes del siguiente cierre de procesos.
- Una sesión se considera expirada cuando `ahoraUtc >= EndsAtUtc`, aunque todavía no se haya conseguido guardar su eliminación del JSON.
- Si la aplicación vuelve a abrirse después del vencimiento, no reanudar la vigilancia por esa sesión: intentar limpiar reglas residuales y mostrar cualquier fallo de permisos.
- Cambiar de zona horaria no cambia el instante final de la sesión manual; sí puede cambiar si el horario local está activo. Un cambio manual del reloj del sistema cambia el cálculo de tiempo restante: documentar este límite, sin implementar protección contra manipulación del reloj.
- Durante suspensión o con el proceso cerrado, no se ejecutan acciones puntualmente. La reconciliación ocurre al reanudar o volver a abrir Cold Ham; las entradas de hosts pueden permanecer hasta entonces.

### Arquitectura propuesta

| Componente | Ubicación | Responsabilidad |
| --- | --- | --- |
| `ManualSession` | ColdHam.Core | Estado persistible de una sesión manual. |
| `BlockStateEvaluator` | ColdHam.Core | Calcular fuentes activas, tiempo restante y bloqueo deseado a partir de configuración y reloj. |
| `ScheduleEvaluator` | ColdHam.Core | Conservar evaluación semanal y trasladar aquí el cálculo de próxima transición que hoy vive en la ventana. |
| `ApplicationBlockConsent` | ColdHam.Core | Mantener consentimiento por conjunto de reglas y periodo continuo de bloqueo. |
| `BlockController` | ColdHam.Core | Coordinar comandos, persistencia, expiración, consentimiento y aplicación mediante interfaces; emitir un estado común para ventana y bandeja. |
| Adaptadores de configuración, hosts y procesos | ColdHam.Core / ColdHam.Windows | Interfaces mínimas en Core e implementaciones actuales en sus proyectos; permitir simular errores en pruebas. |
| `TrayIconService` | ColdHam.App | Crear icono y menú, publicar avisos puntuales y liberar recursos. |
| `ApplicationLifetime` | ColdHam.App | Arranque, ventana visible/oculta, salida, instancia única y traspaso al proceso elevado. |

El controlador expondrá una instantánea con fuentes activas (manual, horario o ambas), estado web confirmado, vigilancia de aplicaciones, consentimiento pendiente, tiempo restante, próxima transición y operaciones en curso. La ventana y la bandeja consumirán esa misma instantánea; ninguna duplicará las reglas de negocio.

Usar una cola o exclusión asíncrona para serializar comandos y reconciliaciones. Mantener la notificación y los controles WPF en su hilo de interfaz. Las operaciones de procesos y archivos no deben bloquear la cuenta atrás. Separar el refresco visual de un segundo del barrido de procesos actual de dos segundos: refrescar la pantalla no debe escribir hosts ni guardar JSON.

El consentimiento se pide mediante una solicitud explícita a la interfaz, fuera de una sección crítica que pudiera impedir detener el bloqueo. Antes de ejecutar el cierre autorizado se vuelve a comprobar vigencia, reglas y solicitud actual. Cancelar, detener o expirar invalida solicitudes pendientes. No iniciar cierres nuevos durante la salida; un cierre ya enviado al sistema no puede deshacerse.

### Secuencia de trabajo y criterios por fase

#### Fase 0. Validar la primera entrega

- Ejecutar compilación completa y las 20 pruebas actuales como referencia.
- Revisar manualmente el tamaño mínimo de la ventana, escalado, navegación con teclado y panel de incidencias.
- Comprobar inicio sin elevación, cancelación de UAC, rechazo de consentimiento y errores de retirada. Para integración automatizada, usar exclusivamente hosts temporales y procesos fixture propios.
- Registrar defectos y resolver los que afecten al estado real, al consentimiento o a la reversibilidad antes de cambiar el ciclo de vida.

Salida: referencia validada y lista explícita de comprobaciones manuales realizadas o pendientes.

#### Fase 1. Sesiones y reglas de evaluación

- Crear el modelo de sesión, el evaluador y la migración versionada.
- Implementar comandos de iniciar sesión, terminar sesión y detener todo como operaciones diferenciadas.
- Definir que el bloqueo deseado es la unión de sesión manual vigente y horario activo.
- Trasladar el cálculo de próxima transición fuera de `MainWindow`; corregir la coherencia entre la evaluación de día completo y la hora mostrada cuando inicio y fin coinciden.
- Añadir pruebas de migración, límites temporales, sesiones indefinidas, sesiones caducadas y solapamientos.

Salida: dominio y persistencia probados sin depender de WPF ni de permisos administrativos.

#### Fase 2. Controlador compartido y expiración

- Extraer progresivamente `ApplyDesiredStateAsync`, coordinación del temporizador y manejo de errores de la ventana.
- Introducir interfaces pequeñas para reloj, persistencia, hosts y procesos; reutilizar las implementaciones existentes.
- Mantener la diferenciación entre estado deseado y operaciones confirmadas. Una expiración no equivale a retirada web exitosa.
- No aceptar un inicio como completado si no se pudo guardar. Una orden de detener debe intentar deshacer el bloqueo aunque no pueda persistirse.
- Evitar que la salida con horario activo vuelva a aplicar reglas: entrar en estado de cierre antes de retirarlas, sin modificar el horario guardado.
- En la transición manual → horario sin interrupción ni cambio de reglas, conservar el consentimiento. Al detenerse realmente el bloqueo, invalidarlo.
- Reevaluar antes de cerrar procesos si la sesión venció mientras el usuario respondía a una confirmación.
- Probar comandos simultáneos, errores y solicitudes pendientes con adaptadores simulados.

Salida: controlador capaz de operar sin ventana visible y con pruebas de estado real y retirada fallida.

#### Fase 3. Interfaz de sesiones

- Añadir controles compactos para 25 min, 50 min, duración personalizada y sin límite.
- Mostrar tiempo restante y hora final; indicar por separado si el horario seguirá bloqueando al terminar la sesión.
- Incorporar «Terminar sesión» sin cambiar el horario y conservar «Detener todo» con su confirmación.
- Confirmar terminación anticipada cuando vaya a liberar reglas; explicar expresamente los solapamientos con el horario.
- Deshabilitar acciones incompatibles durante un comando, sin bloquear la lectura del estado ni dejar una confirmación obsoleta ejecutable.
- Mantener errores visibles y evitar un estado global verde si hay incidencias o autorizaciones pendientes.

Salida: sesiones utilizables desde la ventana y verificación visual a tamaño mínimo y con escalado.

#### Fase 4. Bandeja y ciclo de vida

- Implementar `TrayIconService` utilizando el componente de bandeja de Windows Forms del framework de escritorio, sin paquetes externos. Validar en un pequeño cambio compilable su convivencia con WPF y resolver los nombres de tipos ambiguos mediante alias explícitos.
- Añadir un icono local incorporado al ejecutable y un menú con Abrir Cold Ham, estado/tiempo restante, Terminar sesión, Detener todo y Salir.
- Enrutar las acciones del menú a los mismos comandos del controlador; no duplicar lógica ni confirmaciones.
- Hacer explícita la propiedad del ciclo de vida en `App`; ocultar la ventana no debe terminar el proceso. Liberar icono, eventos, temporizadores y recursos al salir de verdad.
- Mostrar una explicación la primera vez que X oculta la ventana. Emitir avisos solo en finalización, error o consentimiento pendiente, sin avisos por cada tick. Los avisos no serán el único modo de acceder a una acción.
- Si se necesita consentimiento mientras la ventana está oculta, mostrar un aviso y una acción accesible desde el menú para abrirla; no bloquear el controlador en un diálogo invisible.
- Validar recuperación del icono tras reiniciar Explorer. Si el mecanismo del framework no basta, incorporar el manejo mínimo del evento de recreación de la barra.
- Añadir instancia única por usuario/sesión de Windows. Una segunda apertura debe activar la ventana existente mediante comunicación local restringida al usuario; nunca iniciar un segundo motor de bloqueo.
- Integrar la elevación con esa exclusión: al cancelar UAC sigue funcionando el proceso original; cuando el nuevo proceso esté preparado, el original deja de ejecutar enforcement antes de transferirle el control. Si el traspaso falla, mostrar el error y mantener una instancia accesible, sin reiniciar la cuenta atrás. No implementar un auxiliar elevado permanente en esta entrega.

Salida: cierre a bandeja, reapertura, instancia única y reinicio elevado verificados sin motores simultáneos.

#### Fase 5. Verificación final y documentación

- Ejecutar la matriz de pruebas siguiente y compilar toda la solución con cero advertencias como objetivo.
- Probar desde una configuración antigua y desde una configuración recuperada de copia.
- Actualizar Ayuda con las diferencias entre ocultar, terminar sesión, detener todo y salir; explicar los límites durante suspensión y con el proceso cerrado.
- Actualizar este plan con decisiones finales, comprobaciones realizadas y limitaciones que permanezcan.

Salida: funciones completas, pruebas aprobadas y evidencia de las comprobaciones manuales; no considerar el éxito del build equivalente a validar bandeja o UAC.

### Matriz mínima de pruebas

| Área | Casos y resultado exigido |
| --- | --- |
| Duración | 1, 25, 50 y 1440 minutos aceptados; cero, negativos, decimales, texto y valores superiores rechazados. |
| Fecha límite | Antes del límite activa; exactamente en el límite expirada; cuenta atrás nunca negativa. |
| Restauración | Conservar fecha original, descartar sesión vencida y requerir nuevo consentimiento en un proceso nuevo. |
| Migración | Booleanos antiguos true/false, JSON sin versión, sesión inválida, copia recuperada, versión futura y fallo al guardar migración. |
| Horario | Límites exactos, medianoche, día completo, horario solapado y cambios de hora local. Próxima transición coherente con la evaluación. |
| Fuentes de bloqueo | Manual sola, horario solo, ambos y ninguno; terminar una fuente no elimina la otra. |
| Consentimiento | Rechazo, reintento, reglas cambiadas, caducidad durante diálogo y cancelación mientras se esperaba autorización. |
| Fallos | Guardado fallido al iniciar/detener/expirar; apply/remove fallidos; no afirmar retirada exitosa ni reactivar sesión caducada. |
| Concurrencia | Doble inicio, tick durante guardado, detener durante confirmación y salir durante un barrido; una secuencia de enforcement, sin reactivación por tareas antiguas. |
| Hosts | Mantener todas las pruebas de bytes, codificaciones, marcadores ambiguos, archivo bloqueado y retirada posterior. |
| Bandeja | X, Alt+F4, doble clic, menú, icono no disponible, Explorer reiniciado y liberación de recursos al salir. |
| Instancias/UAC | Segunda apertura, UAC cancelado, traspaso exitoso y fallido, ausencia de dos motores activos y fecha de sesión sin cambios. |
| Ciclo de vida | Suspender/reanudar antes y después del límite; salir con horario activo; reabrir tras una salida abrupta. |
| Accesibilidad | Teclado, foco al reabrir, escala 100/150/200 %, textos largos y ventana mínima sin ocultar controles esenciales. |

Las pruebas de tiempo usarán un reloj controlado sin esperas reales. Los tests automáticos no alterarán el hosts del sistema ni cerrarán procesos ajenos. Las pruebas manuales que activen bloqueo real utilizarán reglas y aplicaciones de prueba identificadas de antemano y terminarán comprobando su retirada.

### Orden de integración y definición de terminado

Integrar en unidades revisables: (1) modelo y migración, (2) controlador y pruebas, (3) interfaz de sesiones, (4) bandeja y ciclo de vida, (5) instancia única/elevación y validación final. Las fases 1–3 entregan valor antes de introducir la bandeja; la fase 4 depende de que el motor ya no pertenezca a la ventana.

La entrega se considera terminada cuando se puede iniciar una sesión, ocultar y reabrir Cold Ham, ver la misma fecha límite, terminarla sin perder el horario y salir con retirada confirmada. Deben mantenerse las garantías de la primera entrega, aprobarse las pruebas de dominio/integración y registrarse la validación manual de bandeja, UAC y recuperación. Cualquier comprobación pendiente se identificará expresamente en la entrega.

### Implementación realizada

- Se añadieron `ManualSession` y `BlockStateEvaluator`; las sesiones de 25, 50, duración personalizada (1–1440 min) y sin límite usan una fecha límite UTC persistida. Al vencer, una sesión no vuelve a activar el bloqueo aunque falle su guardado.
- La configuración ahora es versión 2. Los ajustes anteriores con `ManualBlockEnabled: true` se convierten en una sesión ilimitada en la lectura y se guardan después sin ese campo. Una versión futura se rechaza sin recuperar, borrar o sobrescribir el archivo.
- El horario y la sesión son fuentes separadas. Terminar una sesión no cambia el horario y una transición entre ambas mantiene el bloqueo si sigue habiendo una fuente activa. La próxima transición de un día completo se calcula a medianoche.
- La ventana incorpora las acciones de sesión y la cuenta atrás. El icono de bandeja ofrece abrir, terminar sesión, detener todo y salir. Cerrar la ventana la oculta; salir termina la sesión, intenta retirar reglas, conserva el horario y libera el icono.
- `App` usa un mutex local por SID de Windows y una tubería local con un nombre ligado a ese SID: una segunda apertura muestra la ventana existente. El reinicio elevado libera la instancia antes de crear el proceso elevado y recupera la instancia original si se cancela UAC.
- La validación automática cubre 24 casos. La automatización visual nativa no estuvo disponible en el entorno de desarrollo, por lo que bandeja, el traspaso UAC, una segunda instancia, el escalado 100/150/200 % y la recuperación tras reiniciar Explorer requieren comprobación manual antes de distribuir.

## Distribución portable Windows x64

- Perfil reproducible: `src/ColdHam.App/Properties/PublishProfiles/Portable-win-x64.pubxml`.
- Generación: `dotnet publish src/ColdHam.App/ColdHam.App.csproj -c Release -p:PublishProfile=Portable-win-x64 -m:1 -p:UseSharedCompilation=false`.
- Salida: `bin/portable/win-x64/ColdHam.App.exe`, un ejecutable autocontenido que incorpora .NET y WPF, comprime el paquete y extrae las bibliotecas nativas cuando es necesario. No requiere instalación ni un runtime preinstalado. No se usa trimming con WPF.
- Los ajustes siguen en `%LOCALAPPDATA%/ColdHam`; portable significa ejecución sin instalación, no trasladar automáticamente los ajustes junto al EXE.
- Entrega adicional: `bin/portable/ColdHam-portable-win-x64.zip`, con ejecutable, instrucciones y SHA-256. Todos los artefactos generados permanecen en `bin/`, ignorado por Git.
- Verificación: publicación y compilación Release correctas, cabecera PE x64 verificada y 38/38 pruebas aprobadas. NuGet emitió NU1900 al no poder consultar su servicio de vulnerabilidades desde el entorno; se utilizaron los paquetes de runtime 10.0.2 disponibles en caché. No se activó bloqueo real para verificar el paquete.

## Solo sitios permitidos — noviembre de 2026

### Alcance y comportamiento

- La pestaña **Permitidos** permite añadir dominios, eliminar entradas con confirmación y seleccionar **Bloquear toda la web salvo estos sitios (Chrome y Edge)**. El modo requiere activación explícita; las configuraciones antiguas conservan el bloqueo por lista.
- Durante una sesión o franja activa, el modo escribe `URLBlocklist = *` y excepciones HTTP/HTTPS en `URLAllowlist` para Chrome y Edge. Una lista vacía bloquea todos los sitios web. La lista de sitios bloqueados se conserva, pero no se aplica en este modo; se retiran las entradas propias de hosts al cambiar de modo.
- Se normalizan URLs, mayúsculas y dominios internacionales. Cada entrada permite el dominio exacto y su variante `www`; otros subdominios se añaden expresamente. Las páginas internas del navegador permanecen disponibles. Se verifica el límite de 1000 patrones después de expandir los dominios (hasta 249 entradas con `www`).
- Terminar la última fuente de bloqueo, detener todo o salir retira las políticas propias. Al cambiar de modo se reconcilian hosts y políticas; se intentan ambas operaciones incluso si una falla. No se declara éxito si alguna retirada o escritura falla.
- Chrome/Edge pueden tardar en recargar las políticas: la interfaz indica **políticas escritas**, pide reiniciar el navegador y remite a `chrome://policy` / `edge://policy`. No equivale a una comprobación de navegación real. Las páginas ya cargadas pueden seguir mostrando contenido.
- Esta implementación cubre los navegadores y perfiles que admiten estas políticas, no otros navegadores o aplicaciones. Edge no aplica estas políticas a perfiles con cuenta Microsoft personal. Las políticas de organización/nube pueden tener precedencia; deben verificarse en el navegador. Un filtro de red universal queda fuera de esta entrega.

### Arquitectura y persistencia

- Configuración versión 3: `AllowedWebsites` y `WebsiteMode` (`BlockListedSites` / `AllowListedSites`). Las versiones anteriores migran con lista permitida vacía y el modo tradicional. La carga y el guardado validan reglas, modo y límites; la recuperación de datos dañados mantiene desactivadas sesiones y horarios.
- `WebsiteRules` centraliza normalización, expansión y cálculo de dominios. `BrowserPolicyPlan` produce los patrones HTTP/HTTPS exactos y valida el límite de políticas.
- `BrowserPolicyManager` y su adaptador de registro Windows operan bajo HKCU. Rechazan políticas URL ajenas y las de HKLM, no reescriben listas existentes. `browser-policy-journal.json`, bajo LocalApplicationData/ColdHam, se guarda y vacía a disco antes de escribir las políticas. Las retiradas comparan los valores con el registro de propiedad, preservan cambios ajenos y mantienen el diario si no pueden completar la limpieza. Pueden permanecer claves de registro vacías, sin reglas activas.
- No se añaden dependencias externas. El bloqueo por lista sigue utilizando exclusivamente la sección propia del archivo hosts.

### Validación

- Compilación completa con `dotnet build ColdHam.slnx --no-restore -m:1 -p:UseSharedCompilation=false`: cero errores y advertencias.
- 38 pruebas automáticas: normalización, prioridad/transformaciones de hosts, persistencia y migración, horarios/sesiones, generación de patrones, lista vacía, límites, políticas previas de usuario/equipo, cambio de permitidos, retirada tras reiniciar, fallos parciales, reintentos, cambios externos y diario corrupto.
- Los tests usan hosts temporales y un adaptador de registro simulado; no activan bloqueos reales ni cambian políticas del usuario. Para evitar las restricciones de movimiento de archivos del directorio temporal del entorno, se ejecutan con TEMP/TMP en `TestResults/Temp` (ignorado por Git).
- Pendiente antes de distribuir: comprobación real de navegación en Chrome y Edge con perfiles compatibles, recarga de políticas y retirada tras reiniciar el navegador, políticas de organización, ventana mínima/escalado y flujo visual de altas/bajas. El intento de apertura con el controlador de Windows no expuso una ventana utilizable; no se afirma validación visual ni del bloqueo real.

Referencias oficiales: [Chrome: permitir o bloquear URLs](https://support.google.com/chrome/a/answer/7532419?hl=en), [formato de filtros de Chromium](https://www.chromium.org/administrators/url-blocklist-filter-format/), [Edge URLAllowlist](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/urlallowlist), [Edge URLBlocklist](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/urlblocklist).
