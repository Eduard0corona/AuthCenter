# Runbooks de soporte con la consola

Procedimientos para operadores y mesa de ayuda en la consola (`/admin-v2/`). Cada acción se vuelve
a autorizar en el servidor y queda en el System Log con su `traceId`. Los errores de la consola
terminan con **Referencia: `<traceId>`**: cópiala al ticket; con ella se encuentra la petición en
System Log y en Azure Monitor. Nunca pidas ni copies contraseñas, códigos, tokens ni enlaces de correo.

Permisos: cada sección indica el permiso `AUTHCENTER_*` que necesita. El rol **Admin** del seed cubre
directorio, políticas y gobierno; rotaciones, federación, SAML y hooks requieren los permisos de
escritura de su área, y varias operaciones piden reautenticación (step-up).

## 1. "No puedo iniciar sesión"

1. **System Log** (`AUDIT_LOGS_READ`): filtra por el correo del usuario (campo *Actor* o la ficha del
   usuario → *Ver historial*). Busca el último intento:
   - `LOGIN_FAILED`: contraseña incorrecta. `LOGIN_LOCKED_OUT`: bloqueo temporal por intentos; espera
     o usa *Forzar cambio de contraseña*.
   - `MFA_VERIFY_FAILED`: segundo factor incorrecto; si perdió el dispositivo, ver §2.
   - `ACCESS_POLICY_DENIED` / `OAUTH_ACCESS_DENIED` / `SAML_SSO_DENIED`: la política de la aplicación lo
     rechazó; ver paso 3.
   - `FEDERATION_LOGIN_FAILED` / `FEDERATION_ACCESS_DENIED`: ver §6.
2. **Usuarios → ficha** (`USERS_READ`): el usuario debe estar activo y tener acceso a la aplicación
   (directo o por grupo). Un acceso *Pendiente* espera una decisión (§3); uno *Revocado* se vuelve a
   otorgar, nunca se "aprueba".
3. **Políticas de acceso → aplicación → Simular decisión** (`ACCESS_POLICIES_READ`): simula con el
   usuario, la red y el nivel de autenticación para ver qué regla decide y por qué.
4. Si la aplicación exige MFA y el usuario no tiene factor, el login hospedado lo guía a inscribirlo;
   no hace falta intervenir.

## 2. Cuenta comprometida o dispositivo perdido

- **Restablecer MFA** (ficha del usuario, `USERS_WRITE`, step-up `admin.mfa.reset`): borra sus factores;
  en el siguiente inicio de sesión inscribe uno nuevo. Confirma la identidad por un canal fuera de banda antes.
- **Forzar cambio de contraseña**: revoca sus sesiones y exige una nueva al entrar.
- **Desactivar** la cuenta revoca todas sus sesiones al instante: la API de AuthCenter deja de
  aceptar sus tokens, pero una aplicación que valida el JWT por su cuenta lo acepta hasta que expira
  (`Jwt:AccessTokenMinutes`). Reactívala al resolver.
- Revisa en System Log los accesos posteriores al incidente (IP, aplicación, `traceId`) y abre
  incidente si hubo uso indebido (ver `RUNBOOKS.md`).

## 3. Accesos pendientes y solicitudes

- **Gobierno → Solicitudes de acceso** (`GOVERNANCE_READ`, decidir con `GOVERNANCE_WRITE`) reúne las
  solicitudes del portal, los registros con aprobación y las altas pendientes. Lo normal es que las
  decidan los **responsables** de cada aplicación desde su portal (*Aprobaciones*); asígnalos en
  **Aplicaciones → aplicación → Responsables y solicitudes de acceso**.
- Rechazar exige un motivo que la persona lee en su correo. Nadie decide su propia solicitud.
- `SOD_CONFLICT` al aprobar: la persona ya tiene un rol incompatible (§9). Resuélvelo antes o rechaza
  explicando el motivo.
- Una solicitud del portal expira a los 30 días (`Governance:AccessRequestLifetimeDays`); la persona
  puede pedirla otra vez.

## 4. Provisioning (SCIM) desde el proveedor de identidad

1. **Lifecycle → Provisioning tokens → token** (`PROVISIONING_READ`): el panel de diagnóstico cuenta
   las peticiones y los fallos de las últimas 24 h y 7 días, agrupa los fallos por estado y tipo SCIM
   (`invalidValue`, `uniqueness`, `mutability`, `invalidFilter`…) y lista cada petición con método,
   ruta, estado, detalle del error y `traceId`, nunca el cuerpo.
2. `401`: token revocado o vencido → rota (step-up `admin.provisioning-token.rotate`) y actualiza el
   proveedor el mismo día; el valor se muestra una sola vez.
3. `403` con `WWW-Authenticate: Bearer error="insufficient_scope"`: el token no tiene el alcance
   que la operación pide; emite uno con ese alcance.
4. `invalidValue` en atributos mapeados: revisa **Profile mappings** y el **Esquema de perfil**
   (tipo, valores permitidos).
5. Un usuario desactivado por SCIM queda con el acceso *revocado*. Reactívalo desde el proveedor, que
   es la fuente de verdad: un cambio hecho en la consola deja al proveedor desalineado.

## 5. Grupos y reglas dinámicas

- Un grupo con reglas (**Group rules**) no admite cambios manuales de miembros (`GROUP_MANAGED_BY_RULES`):
  cambia la regla o el perfil del usuario. La vista previa de la regla muestra quién entra.
- Cambiar aplicaciones o roles de un grupo revoca las sesiones de sus miembros para esa aplicación:
  avisa antes de hacerlo en horario laboral.

## 6. Federación empresarial

1. **Federación → proveedor → Probar conexión** (`FEDERATION_READ`): valida discovery, issuer, llaves,
   PKCE y callback (OIDC) o certificados, URL SSO y el certificado propio (SAML).
2. `FEDERATION_LOGIN_FAILED` con firma o emisor inválido: certificado o metadatos del IdP cambiados;
   actualiza el proveedor (step-up `admin.federation.change`).
3. `FEDERATION_ACCESS_DENIED`: el usuario tenía el acceso revocado o pendiente; la federación nunca lo
   devuelve por sí sola.
4. Un dominio que no enruta al IdP correcto: revisa las **reglas de routing** y su orden.

## 7. Aplicaciones SAML (AuthCenter como IdP)

1. El panel **Aplicaciones SAML** indica si AuthCenter puede firmar (`Saml:SigningCertificateBase64`)
   y la vigencia del certificado; renuévalo antes de que venza y avisa a cada aplicación.
2. `SAML_REQUEST_REJECTED` en System Log: la aplicación no está registrada (Issuer), la solicitud es
   vieja o repetida, el `Destination` o la ACS no coinciden, o la firma exigida no valida. El detalle
   de la entrada dice cuál.
3. La aplicación rechaza la aserción: compara su entity ID esperado con el de AuthCenter
   (`Saml:IdentityProviderEntityId`), el certificado registrado y la hora de ambos servidores.
4. `SAML_SSO_DENIED`: la política o el MFA de la aplicación de AuthCenter vinculada lo impidió (§1.3).

## 8. Clientes OAuth

- Un `redirect_uri` rechazado: las URIs registradas se comparan exactas (esquema, host, puerto y ruta).
- Rotar el secreto requiere step-up (`admin.oauth-client.rotate-secret`) y lo muestra una sola vez.
- Back-channel logout que no llega: revisa la URI registrada del cliente y que responda 2xx; los
  reintentos y sus errores quedan en los logs de la aplicación (Azure Monitor), no en System Log.

## 9. Gobierno: revisiones y segregación de funciones

- **Revisiones de acceso**: una campaña vencida se cierra sola (cada 15 minutos) aplicando lo que diga
  para lo no revisado. Los ítems *Por quitar de grupos* son accesos revocados que un grupo sigue
  dando: quita la membresía en **Grupos** o revisa el grupo completo.
- **Segregación de funciones**: la lista de violaciones muestra quién tiene los dos roles y cómo
  (directo o por qué grupo). Corrige quitando el rol directo o la membresía; si la combinación viene
  de SCIM, de una regla o de la federación, corrígela en su origen.
- `SOD_CONFLICT` al asignar un rol, agregar a un grupo o aprobar: la consola nombra al usuario, los
  roles y la regla. Queda registrado como `SOD_CONFLICT_BLOCKED`.

## 10. Event Hooks

- **Entregas → dead letter**: corrige el receptor y usa *Reintentar entrega* (idempotente con
  `Idempotency-Key`); el receptor debe deduplicar por `X-AuthCenter-Event-Id`.
- Un hook sin verificar no recibe eventos: el endpoint debe devolver el reto de verificación.
- Rotar el secreto (step-up `admin.event-hook.rotate-secret`) firma con ambos secretos durante 24 h.

## 11. Mensajes frecuentes de la consola

- *"Alguien más cambió este registro"* (409 `CONCURRENCY_CONFLICT`): carga la versión actual y vuelve a
  aplicar el cambio; nunca se sobrescribe en silencio.
- *"Confirma tu identidad"*: la operación requiere reautenticación (contraseña o passkey) de un solo uso.
- *"La consola se actualizó mientras estaba abierta"*: recarga la página.

## Escalamiento

Adjunta al ticket: referencia (`traceId`), hora UTC, usuario y aplicación afectados, pasos y
capturas **sin** datos sensibles. Los incidentes de seguridad siguen `RUNBOOKS.md`.
