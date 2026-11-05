# Reservas de sala con Microsoft 365 (tablet en la puerta)

ASP.NET Core 8 MVC + Microsoft Graph (Client Credentials) + IIS. La tablet muestra el estado de la sala `sala.reuniones` y permite reservar; los eventos se crean en su calendario de Exchange Online, por lo que se ven en Outlook y Teams.

## Estructura

```
SalaReservas/
├─ SalaReservas.csproj
├─ Program.cs                      # arranque, HTTPS/HSTS, clave de tablet, antiforgery
├─ appsettings.json                # configuración (sin secretos)
├─ web.config                      # hosting IIS in-process
├─ Models/Models.cs
├─ Services/GraphRoomService.cs    # consulta, disponibilidad y creación de eventos
├─ Controllers/Controllers.cs      # HomeController + API (/api/status, /api/book)
├─ Views/ (_ViewStart, _ViewImports, Shared/_Layout, Home/Index)
└─ wwwroot/ (css/site.css, js/room.js)
```

## 1. Microsoft 365 / Exchange

1. Confirma que `sala.reuniones@tudominio.com` existe (buzón de sala o usuario con buzón). Para Teams en las reservas, el buzón debe poder alojar reuniones de Teams (normalmente un usuario con licencia); si es buzón de sala puro, deja `CreateTeamsMeeting: false`.
2. Comprueba en el centro de administración de Exchange que el buzón es visible y tiene calendario.

## 2. Registro de la aplicación en Microsoft Entra ID

1. https://entra.microsoft.com → **Identidad > Aplicaciones > Registros de aplicaciones > Nuevo registro**.
2. Nombre: `SalaReservas`; tipos de cuenta: *Solo este directorio*; sin URI de redirección. Registrar.
3. Copia **Id. de aplicación (cliente)** e **Id. de directorio (inquilino)**.
4. **Certificados y secretos > Nuevo secreto de cliente**: copia el *Valor* (solo se ve una vez). Pon caducidad corta y anota la fecha de renovación. (Mejor práctica: usar un certificado; `ClientSecretCredential` se cambia por `ClientCertificateCredential`.)
5. **Permisos de API > Agregar > Microsoft Graph > Permisos de la aplicación > `Calendars.ReadWrite`**. Pulsa **Conceder consentimiento de administrador**.

## 3. Permisos mínimos: limitar la app a UN solo buzón

`Calendars.ReadWrite` de aplicación da acceso a todos los calendarios del inquilino. Hay que restringirlo:

**Opción A – Application Access Policy (clásica):**

```powershell
Connect-ExchangeOnline
New-DistributionGroup -Name "SalaReservas-Buzones" -Type Security -Members "sala.reuniones@tudominio.com"
New-ApplicationAccessPolicy -AppId <CLIENT_ID> -PolicyScopeGroupId SalaReservas-Buzones@tudominio.com -AccessRight RestrictAccess -Description "SalaReservas solo sala.reuniones"
Test-ApplicationAccessPolicy -Identity sala.reuniones@tudominio.com -AppId <CLIENT_ID>   # debe dar Granted
```

**Opción B – RBAC for Applications (recomendada por Microsoft para nuevos despliegues):** crea un *service principal* en Exchange y asigna el rol `Application Calendars.ReadWrite` con ámbito de gestión limitado al buzón de la sala. Consulta la documentación actual de Microsoft ("Role Based Access Control for Applications in Exchange Online"). Si usas B, no necesitas la política de A.

Los cambios pueden tardar hasta 30 min en propagarse.

## 4. Ejecutar en local

```powershell
cd SalaReservas
dotnet user-secrets init
dotnet user-secrets set "Graph:TenantId" "<TENANT_ID>"
dotnet user-secrets set "Graph:ClientId" "<CLIENT_ID>"
dotnet user-secrets set "Graph:ClientSecret" "<SECRETO>"
dotnet user-secrets set "Room:Mailbox" "sala.reuniones@tudominio.com"
dotnet run
```
Abre `https://localhost:5001` (o el puerto que indique la consola).

## 5. Publicar en IIS (Windows Server)

1. Instala el **.NET 8 Hosting Bundle** (https://dotnet.microsoft.com/download/dotnet/8.0) y ejecuta `iisreset`. IIS debe tener el rol Web Server.
2. Publica: `dotnet publish -c Release -o C:\inetpub\SalaReservas`
3. IIS Manager → **Application Pools > Add**: nombre `SalaReservas`, *No Managed Code*. En *Advanced Settings*: **Maximum Worker Processes = 1** (necesario para el bloqueo anti-duplicados), Start Mode = AlwaysRunning, Idle Time-out = 0.
4. **Sites > Add Website**: ruta `C:\inetpub\SalaReservas`, pool `SalaReservas`, binding **https** (puerto 443, nombre `salas.tudominio.com`) con un certificado válido (de tu CA interna o público). Quita el binding http o deja la redirección del propio app.
5. Da permiso de lectura a `IIS AppPool\SalaReservas` sobre la carpeta y escritura sobre `logs\` (crea la carpeta).
6. **Secretos**: no los dejes en `appsettings.json`. Define variables de entorno del sistema (y reinicia IIS) o edita `web.config` con `<environmentVariables>`:
   ```powershell
   [Environment]::SetEnvironmentVariable("Graph__TenantId","<TENANT>","Machine")
   [Environment]::SetEnvironmentVariable("Graph__ClientId","<CLIENT>","Machine")
   [Environment]::SetEnvironmentVariable("Graph__ClientSecret","<SECRETO>","Machine")
   [Environment]::SetEnvironmentVariable("Room__Mailbox","sala.reuniones@tudominio.com","Machine")
   [Environment]::SetEnvironmentVariable("Kiosk__AccessKey","<CLAVE-LARGA-ALEATORIA>","Machine")
   iisreset
   ```
7. DNS interno: crea el registro `salas.tudominio.com` → IP del servidor. Abre el 443 en el firewall solo para la red de las tablets.

## 6. Conectar la tablet

1. Importa en la tablet el certificado de tu CA si es interno (para que el HTTPS sea de confianza).
2. Abre una sola vez `https://salas.tudominio.com/?key=<CLAVE-LARGA-ALEATORIA>`: se guarda una cookie segura de un año y desde entonces basta la URL normal.
3. Modo kiosco:
   - **Android**: Chrome → *Añadir a pantalla de inicio* y usar *Fully Kiosk Browser* (recomendado: pantalla siempre encendida, pantalla completa, recarga automática) o fijar pantalla.
   - **iPad**: Acceso guiado (Ajustes > Accesibilidad) con Safari y "Añadir a inicio".
   - **Windows**: `msedge --kiosk https://salas.tudominio.com --edge-kiosk-type=fullscreen`.
4. Desactiva el reposo de pantalla y conecta la tablet a corriente.

## Seguridad y comportamiento

- Client Credentials: el secreto vive solo en el servidor; la tablet nunca lo ve.
- Antiforgery en las llamadas POST; HTTPS + HSTS; cabeceras de seguridad en `web.config`.
- Anti-duplicados: un `SemaphoreSlim` serializa las reservas y la disponibilidad se vuelve a comprobar contra Exchange justo antes de crear el evento (por eso un único worker). Si escalas a varios servidores, sustituye el bloqueo por uno distribuido.
- Validación de duración, horario laborable y hora pasada en el servidor.
- La pantalla se refresca cada 30 s (`REFRESH_MS` en `wwwroot/js/room.js`); el reloj va cada segundo.
- Las reservas aparecen en Outlook y Teams (calendario de la sala); los cambios hechos en Outlook se reflejan en la tablet en el siguiente refresco.

## Problemas típicos

| Síntoma | Causa |
|---|---|
| 403 / `ErrorAccessDenied` | Falta consentimiento de administrador o la política de acceso no incluye el buzón (espera hasta 30 min) |
| 404 `MailboxNotFound` | `Room:Mailbox` mal escrito |
| `Teams` no se crea | El buzón no admite reuniones online; pon `CreateTeamsMeeting` en `false` |
| HTTP 500.19 en IIS | Falta el Hosting Bundle |
| La hora sale desplazada | Revisa `Room:TimeZone` (`Romance Standard Time` = Madrid) |
