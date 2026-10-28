# Emby Backup

Plugin para Emby Server que hace copias de seguridad completas del **Server Data Folder** y de sus
bases de datos, **sin metadatos de películas**: ni NFO, ni pósteres, ni fondos, ni caché, ni ficheros de
las bibliotecas multimedia.

Pensado para Emby Server **4.9 o superior** (probado con 4.9.1.90) en Linux, Docker, Windows y NAS.

## Descarga

La versión lista para instalar está en [Releases](https://github.com/ratopro/emby-backup-plugin/releases/latest): descarga el ZIP, extrae
`EmbyBackup.dll` y cópialo en la carpeta de plugins (ver [Instalación](#instalación)).

## Versiones

- **1.4.1**: endurecimiento de seguridad: límites para metadatos ZIP y restauraciones, validación
  adicional de cabeceras/manifiestos, comprobación de espacio de staging y aislamiento del bypass de
  certificados FTPS auto-firmados.
- **1.4.0**: protección opcional de las copias con contraseña (**AES-256** al estilo WinZip) y
  **restauración desde la página**, con verificación previa, doble confirmación y reinicio al terminar.
- **1.3.0**: programación configurable de las copias automáticas (**cada día**, **cada dos días**,
  **semanal** o **mensual**) y hora de inicio, con el origen (*Manual* / *Automatic*) en el historial.
- **1.2.0**: destino seleccionable **Local**, **red SMB** o **FTP/FTPS**, con *Test destination* y
  credenciales guardadas en la configuración.
- **1.1.1**: comprueba la escritura del destino antes de empezar y explica cómo dar permisos a la cuenta
  de Emby cuando la carpeta no es accesible.
- **1.1.0**: explorador de carpetas del servidor junto a *Backup destination*; `Esc` cierra los
  diálogos sin salir de la página.
- **1.0.0**: primera versión con copias consistentes de SQLite, manifiesto, verificación y retención.

## Qué incluye la copia

- Bases de datos SQLite (`library.db`, `users.db`, `authentication.db`, `activitylog.db` y cualquier otra
  base del Data Folder). Se copian con la **SQLite Online Backup API**, no con una copia directa del
  fichero, para que el resultado sea consistente aunque Emby esté escribiendo con WAL activo.
- Configuración del servidor (`config/system.xml`) y políticas de usuario (`config/users/**`).
- Play-states, favoritos, colecciones, playlists, dispositivos, Live TV (temporizadores y grabaciones
  programadas), configuración de plugins (`plugins/configurations/*.xml`).

## Qué nunca incluye

- `metadata/` completo (NFO, imágenes, pósteres, fondos, subtítulos descargados).
- `cache/`, `logs/`, `transcoding-temp/`.
- Bibliotecas multimedia, grabaciones de Live TV (`data/livetv/recordings`) y uploads de cámara.
- Binarios y símbolos: `.dll`, `.exe`, `.pdb`, `.so`, `.dylib`.
- Enlaces simbólicos y junctions, incluso si apuntan fuera del Data Folder.
- El propio historial del plugin (`plugins/EmbyBackup/`) y sus ficheros temporales.

Las exclusiones quedan documentadas dentro del propio ZIP, en `manifest.json`.

## Instalación

1. Copia `EmbyBackup.dll` **directamente** en la carpeta de plugins del servidor, sin subcarpeta:

   | Plataforma | Ruta |
   |---|---|
   | Linux (paquete) | `/var/lib/emby/plugins/` |
   | Linux (manual) | `<programdata>/plugins/` |
   | Docker (oficial) | `/config/plugins/` dentro del contenedor |
   | Windows | `%APPDATA%\Emby-Server\programdata\plugins\` |
   | NAS (Synology/QNAP) | `<volumen>/EmbyServer/programdata/plugins/` |

   Emby 4.9 no descubre el plugin si el DLL está en una subcarpeta.

2. Reinicia Emby. El plugin aparece como **Emby Backup** en *Panel → Plugins*.
3. En *Panel → Programadas* se registra la tarea **Emby Backup**, cuya programación sigue los ajustes
   del plugin.

No hace falta copiar dependencias: el plugin usa las que Emby ya carga (`MediaBrowser.*`, SQLite del
sistema). En Linux necesita `libsqlite3.so` (Emby ya la usa); en Windows usa el `sqlite3.dll` que Emby
incluye en `system/`.

## Configuración

*Panel → Plugins → Emby Backup*.

| Ajuste | Descripción |
|---|---|
| **Destination type** | **Local folder**, **Network share (SMB)** o **FTP / FTPS server**. La página muestra solo los campos del tipo elegido. |
| **Backup folder** (Local) | Ruta absoluta, fuera del Data Folder. |
| **Share path** (Network) | UNC `\\servidor\recurso\carpeta` en Windows; punto de montaje en Linux/Docker/NAS. |
| **Server / Port / Remote folder** (FTP) | Host, puerto (21 por defecto) y carpeta remota (`/` es la carpeta del usuario). Se crea si no existe. |
| **Backups to keep** | Número de ZIP que se conservan (por defecto 7). |
| **Compression** | `Fastest`, `Optimal` o `NoCompression`. |
| **Database busy timeout** | Segundos de espera si Emby mantiene bloqueada una base. |
| **Automatic backups** | Periodicidad: **Every day**, **Every other day**, **Once a week** o **Once a month**. |
| **Day of the week** | Solo para **Once a week**: día en que se ejecuta la copia. |
| **Start time** | Hora local del servidor (`HH:mm`) a la que empieza la copia automática. |
| **Run automatic backups** | Activa o desactiva la tarea programada; las copias manuales siguen disponibles. |
| **Verify the archive after creating it** | Relee el ZIP y compara todos los SHA-256. |
| **Protect new backups with a password (AES-256)** | Cifra las copias nuevas (manuales y programadas) con la contraseña indicada, usando AES-256 (WinZip AE-2). Las copias existentes conservan su contraseña. |
| **Backup password** | Contraseña que se aplica a las copias nuevas. Sin ella, una copia protegida no se puede verificar ni restaurar. |
| **Test destination** | Guarda los ajustes y comprueba que el destino existe y admite escritura, sin lanzar una copia. |

Ejemplos de destino:

- Linux: `/mnt/backups/emby`
- Windows: `D:\Backups\Emby`
- NAS por SMB: `\\nas\backups\emby` (Windows) o `/mnt/nas/backups` si ya está montado
- Docker: monta un volumen, por ejemplo `-v /host/backups:/backups`, y usa `/backups/emby`
- FTP: `ftp.example.com`, puerto `21`, carpeta `/emby`

El destino no puede estar dentro del Data Folder; el plugin lo rechaza con un mensaje claro. Cada
ejecución manual guarda antes los ajustes.

### Programación de las copias

En *Panel → Plugins → Emby Backup* elige la periodicidad, el día de la semana cuando corresponda y la
hora de inicio. La hora y el día usan la **zona horaria local del servidor**, igual que el resto de
tareas de Emby.

| Periodicidad | Comportamiento |
|---|---|
| **Every day** | Una copia automática al día a la hora elegida. |
| **Every other day** | Una copia cada dos días, contados desde la última copia **automática** correcta. |
| **Once a week** | Una copia el día de la semana y a la hora elegidos. |
| **Once a month** | Una copia el **primer día de cada mes** a la hora elegida. |

- Emby solo sabe expresar «cada día» y «cada semana», así que el plugin usa un trigger diario y decide
  en cada ejecución si el periodo ya toca. La decisión se basa en la última copia automática correcta
  del historial: una copia manual no retrasa la siguiente automática.
- Los días en que no toca, la tarea termina de inmediato sin crear nada; aparece como completada en
  *Programadas*.
- Si el servidor está apagado a la hora programada:
  - **Every day**: esa copia se pierde; la siguiente es al día siguiente a la misma hora.
  - **Every other day** y **Once a month**: la copia se hace en la siguiente ejecución diaria, es
    decir, al día siguiente a la misma hora.
  - **Once a week**: se pierde la de esa semana, porque Emby no recupera triggers semanales perdidos.
- El trigger se aplica al guardar los ajustes y al arrancar Emby, sin reiniciar el servidor. Si nunca
  has guardado los ajustes, se usa la programación diaria a las 03:00.
- El historial indica **Automatic** o **Manual** en la columna *Started by*.

### Destino de red (SMB)

- **Windows**: escribe la ruta UNC, por ejemplo `\\nas\backups\emby`. Si el servidor necesita
  credenciales, rellena usuario, contraseña y dominio; el plugin abre la sesión SMB con
  `WNetAddConnection2` antes de escribir. Si la sesión de Windows que ejecuta Emby ya tiene acceso al
  recurso, deja las credenciales vacías.
- **Linux, Docker y NAS**: el plugin **no monta** recursos. Monta el recurso SMB con el sistema
  operativo (por ejemplo `cifs` en `/etc/fstab`) y escribe el punto de montaje. Las credenciales SMB se
  ignoran en estas plataformas y el registro muestra un aviso.
- En Windows el explorador de carpetas se oculta para este tipo, porque la API de Emby no navega por
  rutas UNC; escribe la ruta a mano.

### Destino FTP / FTPS

- **Use FTPS (explicit TLS)** activa `AUTH TLS`; sin marcar, la conexión y las credenciales viajan sin
  cifrar. Marca **Accept a self-signed certificate for this server** solo si usas un certificado
  propio; sin esa opción el certificado debe ser válido para el host.
- El ZIP se sube primero como `.partial`, se renombra al nombre final y después se sube su `.sha256`.
  Si el servidor no permite renombrar, el plugin vuelve a subir el archivo con el nombre definitivo.
- La retención en FTP se basa en el `.sha256` válido como prueba de propiedad; si alguien borra el
  sidecar, el ZIP se conserva.
- **Verificar** un archivo FTP lo descarga a una carpeta temporal del servidor, lo comprueba y lo borra.

### Credenciales

Las contraseñas de red, FTP y la contraseña opcional de las copias se guardan **en texto plano** en
`plugins/configurations/EmbyBackup.xml`, igual que el resto de ajustes del plugin. Cualquiera con acceso
de lectura a ese fichero o a la copia de seguridad puede leerlas. Usa una cuenta dedicada con permiso
solo sobre la carpeta de copias y no reutilices la contraseña de un administrador.

La contraseña de la copia **no se guarda dentro del ZIP**: protege el archivo con AES-256, pero si la
olvidas no hay forma de recuperar el contenido. Anótala en un lugar seguro. Para crear copias sin
contraseña, desmarca la protección; las copias que ya estaban cifradas conservan su contraseña.

### Protección con contraseña (AES-256)

Al marcar **Protect new backups with a password** y escribir una contraseña, las copias nuevas se cifran
con **AES-256** (formato WinZip AE-2, compatible con 7-Zip, WinRAR y WinZip). El contenido del ZIP —
incluido `manifest.json`— queda cifrado y autenticado con HMAC-SHA1, así que no se puede leer ni
modificar sin la contraseña.

- **Verificar** una copia protegida pide la contraseña; con una contraseña incorrecta devuelve un error
  claro en lugar de un fallo genérico.
- **Restaurar** una copia protegida pide la contraseña antes de continuar.
- La retención sigue reconociendo las copias cifradas como propias mediante el `.sha256` (el manifiesto
  no se puede leer sin la contraseña).
- Puedes abrir la copia a mano con `7z x archivo.zip -p<contraseña>`.

### Explorador de carpetas

El icono de carpeta a la derecha de **Backup folder** (o **Share path** en Linux/Docker/NAS) abre un explorador que recorre las carpetas
del servidor usando la propia API de Emby. Muestra únicamente carpetas (no archivos) y permite subir de
nivel con **Up**, entrar en una carpeta con un clic y confirmar con **Use this folder**. Si el campo ya
tiene una ruta, el explorador se abre en esa carpeta; si la ruta no existe, empieza en la raíz (o en la
lista de unidades en Windows). **Cancel** y `Esc` cierran sin cambiar el valor.

El explorador ve exactamente lo que ve Emby, que es lo que importa para que la copia funcione:

- **Docker**: rutas del contenedor (`/backups`), no las del host. Hay que navegar al volumen montado.
- **Linux/NAS**: empieza en `/` y muestra los puntos de montaje accesibles para el usuario de Emby.
- **Windows**: empieza en la lista de unidades y permite entrar en rutas `D:\...` y en recursos UNC
  (`\\nas\share`) siempre que el servidor tenga acceso.
- Solo se pueden elegir carpetas existentes y legibles por el proceso de Emby. Para crear una carpeta
  nueva, créala antes en el sistema (o escribe la ruta a mano) y vuelve a explorar.

### Permisos del destino en Linux

Emby corre como servicio con su propia cuenta (`emby`, `embyServer`, el usuario del contenedor…), no con
tu cuenta de escritorio. Una carpeta dentro de `/home/tu-usuario` suele ser privada (`0700`/`0750`), así
que el proceso no puede entrar y la copia falla con `Access to the path '...zip.partial' is denied`.

Primero averigua la cuenta que ejecuta Emby:

```bash
ps -o user= -C EmbyServer        # servidor nativo
systemctl show emby -p User      # si usas la unidad systemd emby.service
```

Opción recomendada, una carpeta dedicada:

```bash
sudo mkdir -p /srv/emby-backup
sudo chown emby:emby /srv/emby-backup
sudo chmod 750 /srv/emby-backup
```

y usa `/srv/emby-backup` como destino.

Si prefieres conservar tu carpeta actual, da acceso solo a esa cuenta con ACL (paquete `acl`). Hay que
conceder `x` en todos los directorios padre y `rwx` en el destino:

```bash
sudo setfacl -m u:emby:x  /home/usuario
sudo setfacl -m u:emby:x  /home/usuario/Documentos
sudo setfacl -m u:emby:rwx /home/usuario/Documentos/Backup
```

Evita `chmod 777`: las ACL permiten dar acceso únicamente a Emby. En Docker, el destino debe ser un
volumen montado y la ruta que se escribe es la del contenedor, no la del host.

Desde 1.1.1 el plugin crea un fichero de prueba antes de empezar; si no puede, falla de inmediato con
la ruta, la cuenta que ejecuta Emby y una sugerencia concreta.

### Copia manual

El botón **Run backup now** lanza la copia en segundo plano. La página muestra progreso, mensaje,
historial y los archivos del destino, y permite **Cancel**. Solo puede haber una copia a la vez (manual,
programada o restauración).

### API

Todas las rutas requieren un token de administrador:

| Método | Ruta |
|---|---|
| `GET` | `/EmbyBackup/Status` |
| `GET` | `/EmbyBackup/History` |
| `GET` | `/EmbyBackup/Files` |
| `POST` | `/EmbyBackup/Run` |
| `POST` | `/EmbyBackup/Cancel` |
| `POST` | `/EmbyBackup/TestDestination` |
| `POST` | `/EmbyBackup/Verify` (`{"FileName":"...", "Password":"..."}`) |
| `POST` | `/EmbyBackup/RestoreCheck` (`{"FileName":"...", "Password":"..."}`) |
| `POST` | `/EmbyBackup/Restore` (`{"FileName":"...", "Password":"...", "Confirm":true}`) |
| `GET` | `/EmbyBackup/RestoreStatus` |

Cada entrada de `/EmbyBackup/History` incluye `Origin` (`manual` o `scheduled`), que es lo que muestra
la columna *Started by*, y `Encrypted` (copia protegida con contraseña). `/EmbyBackup/Files` incluye
`Encrypted` y `EncryptedKnown` para cada archivo. `/EmbyBackup/Status` incluye el estado de la
restauración en `Restore`.

## Qué se crea en el destino

```
emby-backup-20261028-112343Z.zip
emby-backup-20261028-112343Z.zip.sha256
```

- El ZIP se escribe primero como `.partial` en el destino y se renombra de forma atómica al terminar.
  En Local y red el `.partial` está en la propia carpeta; en FTP se construye en una carpeta temporal
  del servidor, se sube como `.partial` y se renombra en el servidor.
- `manifest.json` contiene versión del formato, versión del plugin y de Emby, sistema operativo, fecha,
  ruta de origen, compresión, lista de bases con `quick_check`, inventario de ficheros con SHA-256 y
  tamaños, exclusiones y advertencias.
- Si la protección está activa, el ZIP se cifra con AES-256 y `manifest.json` queda cifrado también; en
  ese caso el inventario no se puede leer sin la contraseña.
- El `.sha256` es el hash del ZIP completo, en el formato habitual de `sha256sum`.
- El ZIP no crea una carpeta raíz: su contenido se corresponde con la raíz del Data Folder.

La retención solo borra ZIP reconocidos por su manifiesto (`format: emby-server-backup`) y sus
`.sha256`. En FTP, donde leer el manifiesto obligaría a descargar cada ZIP, se acepta un `.sha256`
válido como prueba de propiedad. Las copias cifradas se reconocen por el `.sha256` válido más el
contenedor AES. Nunca toca otros ficheros del destino. Los `.partial` con más de 24 horas también se
eliminan.

## Verificación

La verificación está disponible tras cada copia (opcional) y desde la tabla de historial o de archivos
con el botón **Verify**. Comprueba:

- El ZIP se puede abrir y contiene un manifiesto válido.
- Cada entrada del inventario existe y su SHA-256 coincide.
- No hay entradas fuera del inventario (defensa contra archivos añadidos).
- Las rutas no escapan del directorio de restauración.
- El `.sha256` coincide con el ZIP.

En FTP la verificación descarga primero el ZIP a una carpeta temporal y la borra al terminar. Si la copia
está protegida, la verificación pide la contraseña; con una contraseña incorrecta lo indica claramente.

También puedes verificarlo a mano:

```bash
sha256sum -c emby-backup-20261028-112343Z.zip.sha256
unzip -p emby-backup-20261028-112343Z.zip manifest.json | head
```

Si la copia está cifrada, `unzip` te pedirá la contraseña (o usa `7z x -p`).

## Restauración

### Desde la página

En *Panel → Plugins → Emby Backup* hay una tarjeta **Restore from a backup**. Elige el archivo de la
lista (leída del destino configurado) y pulsa **Restore selected backup...**:

1. Si la copia está protegida, se pide la contraseña; con una contraseña incorrecta se vuelve a pedir.
2. Se muestra un resumen del archivo (fecha, versión de Emby, contenido) y se pide **dos confirmaciones**
   antes de tocar nada.
3. El plugin descarga el ZIP, lo extrae y **verifica todos los SHA-256** en una carpeta temporal antes de
   escribir en el Data Folder; si algo falla, no se modifica nada.
4. Aplica los ficheros y las bases de datos con la SQLite Online Backup API (Emby sigue corriendo), y al
   terminar pide **restart Emby** para cargar los datos restaurados.

Detalles importantes:

- La restauración comparte el bloqueo con las copias: solo puede haber una operación (copia o
  restauración) a la vez.
- Se conserva la configuración actual del plugin (`plugins/configurations/EmbyBackup.xml`): el destino y
  las credenciales no se pisan, para no perder el acceso a las copias.
- No se borran los ficheros creados **después** de la copia; solo se reemplazan los que estaban en ella.
- Emby **debe reiniciarse** al terminar para recargar configuración y cachés.
- La restauración funciona en una instalación nueva (Emby reinstalado + plugin instalado + destino
  configurado): recupera toda la información contenida en el archivo.

### Manual (sin el plugin o como último recurso)

Si prefieres restaurar a mano, detén Emby y extrae el ZIP directamente:

1. **Detén Emby**.
2. Haz una copia del Data Folder actual por si acaso.
3. Verifica el ZIP (`Verify` o `sha256sum -c`).
4. Extrae el contenido **directamente en la raíz del Data Folder**, sin crear una carpeta intermedia:

   ```bash
   cd /var/lib/emby
   unzip /mnt/backups/emby/emby-backup-20261028-112343Z.zip
   ```

   Si la copia está cifrada, `unzip` pide la contraseña (o usa `7z x -p`). En Windows, con el Data
   Folder en `%APPDATA%\Emby-Server\programdata`, extrae ahí el contenido del ZIP (por ejemplo con el
   Explorador: *Extraer todo* → esa carpeta).
5. Ajusta propietario y permisos al usuario de Emby:

   ```bash
   sudo chown -R emby:emby /var/lib/emby
   ```

6. Arranca Emby y revisa usuarios, bibliotecas (las rutas de medios deben existir en el servidor) y
   tareas programadas.

Notas:

- El ZIP no incluye binarios ni DLL de plugins: las versiones instaladas se conservan.
- Si restauras en otro servidor, las rutas de las bibliotecas y las credenciales de plugins deben seguir
  siendo válidas.
- Los ficheros `-wal` y `-shm` no se incluyen; las bases restauradas ya están consolidadas.

## Compilación y pruebas

Requiere el SDK de .NET (probado con .NET 8) y acceso a NuGet para `MediaBrowser.Server.Core 4.9.1.90`.

```bash
dotnet build -c Release src/EmbyBackup/EmbyBackup.csproj
dotnet run -c Release --project tests/EmbyBackup.Tests
```

El resultado es `src/EmbyBackup/bin/Release/netstandard2.0/EmbyBackup.dll`.

Las 157 pruebas del motor cubren: WAL real con la backup API, snapshots, ZIP, manifiesto, sidecar,
verificación, retención segura local y remota, archivos ajenos, cancelación, path traversal, destino
dentro del Data Folder, archivos corruptos, exclusión de binarios y de metadatos, validación de los
tres tipos de destino, análisis de listados FTP (Unix e IIS), las reglas de programación (periodos,
horas, días de la semana y decisión de vencimiento), el cifrado AES-256 (ida y vuelta, contraseña
ausente o incorrecta, manipulación del contenido e interoperabilidad con 7-Zip en ambos sentidos) y la
restauración completa (extracción y verificación en staging, bases de datos restauradas con la API
online, conservación de los ajustes actuales, ficheros posteriores y rechazo de path traversal).

## Estructura

```
src/EmbyBackup/
  Plugin.cs                 Plugin, páginas, miniatura y aplicación del trigger
  BackupTask.cs             Tarea programada (periodo, guarda y descripción)
  BackupSchedule.cs         Construcción y aplicación del trigger de Emby
  BackupApi.cs              Rutas REST administrativas
  BackupRuntime.cs          Estado compartido y ejecución en segundo plano
  RestoreRuntime.cs         Estado y ejecución de la restauración en segundo plano
  PluginConfiguration.cs    Ajustes persistidos en plugins/configurations/EmbyBackup.xml
  Core/                     Motor independiente de Emby (probado aparte)
  Core/BackupDestinations.cs    Destinos Local, red y FTP/FTPS
  Core/BackupScheduleRules.cs   Reglas puras de periodo, hora y día
  Core/ZipEncryption.cs         Escritor ZIP AES-256 (WinZip AE-2) y primitivas
  Core/ZipReader.cs             Lector ZIP, descifrado y detección de cifrado
  Core/RestoreService.cs        Comprobación y ejecución de la restauración
  Web/                      Página de configuración (HTML + JS)
  Images/thumb.png          Miniatura
tests/EmbyBackup.Tests/     Pruebas del motor
```

GUID del plugin: `5a3c44fa-8c66-4f07-b75e-4a8c6123852f`.

## Limitaciones conocidas

- Solo se ejecuta una operación a la vez (copia manual, copia programada o restauración).
- Emby no recupera una tarea diaria o semanal perdida mientras el servidor estaba apagado. Las
  periodicidades «cada dos días» y «mensual» sí se recuperan al día siguiente porque se comprueban en
  cada ejecución diaria; la semanal no.
- Si una base no puede leerse tras el tiempo de espera, la copia falla con un mensaje claro en lugar de
  guardar un estado inconsistente.
- Los enlaces simbólicos se omiten y quedan reflejados como advertencia en el manifiesto.
- El historial se guarda en el Data Folder del plugin (máx. 100 entradas) y no forma parte del ZIP.
- En Linux/Docker/NAS el plugin no monta recursos SMB: el administrador debe montarlos. Las
  credenciales SMB solo se usan en Windows.
- FTP es síncrono y sin reanudación: si se corta la conexión, la copia falla y el `.partial` se limpia
  en la siguiente ejecución. Para archivos muy grandes, valora un destino local o una unidad montada.
- La contraseña de las copias se guarda en texto plano en la configuración del plugin. No se puede
  recuperar el contenido de una copia cifrada si se olvida la contraseña.
- La restauración no deshace cambios: solo reemplaza los ficheros presentes en la copia y conserva los
  creados después. Requiere reiniciar Emby al terminar.

## Licencia

MIT. Consulta [LICENSE](LICENSE).
