# Integración Rindegastos → ERP ICS

Programa que trae de Rindegastos lo aprobado y lo registra en el ERP **exactamente como
si alguien lo hubiera digitado a mano**, en los mismos módulos y con los mismos stored
procedures.

## Los cuatro flujos, de un vistazo

Cada 15 minutos el worker ejecuta los cuatro, en este orden. El orden importa: el
comprobante del informe cancela los documentos que compras acaba de registrar.

| # | Qué trae de Rindegastos | Cuándo entra | Módulo del ERP | Qué deja |
|---|---|---|---|---|
| 1 | **Gastos** con documento: factura, boleta, RxH y no domiciliada, en soles y dólares, totales y parciales | Apenas el gasto está **aprobado** | Compra de Servicio sin Prorrateo (`wctrFacturaCompra2.ascx`) | La factura y su comprobante (tipo 5, o 10 en RxH). El proveedor queda por pagar |
| 2 | **Informes** de gastos (rendiciones) | Cuando el informe se **cierra** | Ingreso de Comprobante (`wctrComprobanteContable.ascx`) | Un comprobante por informe: cancela los documentos del flujo 1 y registra las planillas de movilidad, contra la cuenta de quien rinde |
| 3 | **Fondos** (cajas chicas) | En cada **depósito** | Ingreso de Comprobante | Transferencia (tipo 19): del banco a la cuenta del fondo |
| 4 | **Solicitudes de fondo** (viáticos y entregas a rendir pedidos por adelantado) | Cuando se **aprueban** | Ingreso de Comprobante | Transferencia (tipo 19): del banco a entregas a rendir |

Cada flujo tiene su propia tabla de staging (`rg_gasto`, `rg_informe`, `rg_fondo`,
`rg_solicitud_fondo`) con los mismos cinco estados, y se puede apagar por separado desde
la configuración. Más abajo hay una sección por flujo con el detalle y los comprobantes
manuales contra los que se verificó cada uno.

**Para monitorear** (`sql\03_consultas_monitoreo.sql`): la consulta **A3** muestra los
cuatro flujos juntos, y cada uno tiene su bloque de consultas:

| Flujo | Tabla | Día a día | Errores | Detalle contable |
|---|---|---|---|---|
| Gastos | `rg_gasto` | A0 | B1 | E1, E2 |
| Informes | `rg_informe` | L0 | L2 y L2b (en espera) | L5, L6 |
| Fondos | `rg_fondo` | M0 | M1 | M2 |
| Solicitudes | `rg_solicitud_fondo` | S0 | S1 | S2 |

---

## Qué es un "worker"

Un programa **sin pantalla** que se queda corriendo en un servidor y repite una tarea
cada cierto tiempo. No tiene ventanas ni botones: hace su trabajo y deja constancia
en la base de datos y en archivos de log.

En este caso, cada 15 minutos hace esto:

```
Rindegastos                    Worker                    ERP (bd_epysa_peru)
─────────────                  ──────                    ───────────────────
getExpenses          ──────►   rg_gasto (staging)
Status=1                       estado 0 DESCARGADO
IntegrationStatus=0
                                     │
                                     ▼  traduce códigos
                               estado 1 HOMOLOGADO
                                     │
                                     ▼  transacción      ──► mae_factura_boleta
                               estado 2 CONTABILIZADO    ──► mae_comprobante_contable
                                     │                   ──► mae_detalle_comprobante_contable
                                     ▼
setExpenseIntegrationBulk  ◄──  estado 3 CONFIRMADO
IntegrationCode = mcm_cod_comprobante_contable
```

El worker **no inventa SQL**: llama a los mismos stored procedures que usa la pantalla
del ERP (`sp_insert_mae_factura_boleta_1v2`, `sp_insert_mae_comprobante_contable_1`,
`sp_insert_mae_detalle_comprobante_contable_1v2`), en el mismo orden y dentro de una
única transacción. El resultado en la base de datos es idéntico a un ingreso manual.

> **Cómo se marca en Rindegastos.** Los cuatro métodos `set...IntegrationBulk` reciben
> un **objeto** con la lista adentro, no una lista suelta:
> `{"Expenses":[...]}`, `{"ExpenseReports":[...]}`, `{"Funds":[...]}`,
> `{"FundsRequest":[...]}`. Con la lista suelta la API contesta **HTTP 200 con el error
> adentro** (`"property 0 should not exist"`, `"statusCode":400`). Hasta el 17/09/2026 el
> worker lo tomaba como éxito, así que ninguna marca se había aplicado; ahora ese caso se
> trata como error, y `sql\09_reenviar_marcas_integracion.sql` devolvió a estado 2 lo que
> había quedado como confirmado para que se vuelva a mandar.

---

## Estructura de carpetas

```
RindegastosIntegracion\
│
├── RindegastosIntegracion.sln    la solución: se abre con Visual Studio 2022
├── global.json                   qué versión de .NET se necesita
├── compilar.cmd                  compila sin abrir Visual Studio
├── ejecutar-una-vez.cmd          corre un ciclo y termina
│
├── sql\                          scripts que TÚ ejecutas en SQL Server
│   ├── 01_tablas_rg.sql          crea 3 tablas nuevas
│   ├── 02_homologaciones.sql     carga las equivalencias
│   ├── 03_consultas_monitoreo.sql  catálogo de consultas para revisar
│   └── 04_usuario_integracion.sql  crea el usuario del worker
│
└── src\                          "source": el código fuente
    └── Rindegastos.Worker\
        ├── appsettings.json      configuración
        ├── Program.cs            arranque
        ├── Worker.cs             el reloj: repite el ciclo cada N minutos
        ├── Api\                  habla con Rindegastos
        ├── Datos\                habla con la base de datos
        ├── Servicios\            traduce datos y arma el asiento contable
        ├── Modelo\               constantes del ERP (cuentas, estados)
        └── Configuracion\        lectura del appsettings
```

---

## Requisitos

> **Este proyecto se abre con Visual Studio 2022, no con VS 2019.**
>
> El ERP (`AppWebPeru`) está en .NET Framework 4.0 y se sigue trabajando en VS 2019.
> Este worker es una solución aparte en .NET 8 y necesita MSBuild 17.x.
> El MSBuild de VS 2019 llega hasta 16.11 y no puede compilar .NET 6 ni superior:
> por eso aparece el error *"requiere como mínimo la versión 17.12.0 de MSBuild"*.

| | Versión |
|---|---|
| SDK de .NET | 8.0 o superior (fijado en `global.json`) |
| Visual Studio | 2022, versión 17.12 o superior |
| Alternativa | VS Code, o directamente `compilar.cmd` |

---

## Entornos: a qué base de datos apunta

**El worker apunta a donde diga `BaseDatos:CadenaConexion` en `appsettings.json`. Nada más.**
No detecta el entorno solo: si la cadena dice producción, escribe en producción.

Los servidores que aparecen en el `Web.config` del ERP:

| Servidor | Base | Entorno |
|---|---|---|
| `172.16.21.27` | bd_epysa_peru | **QA** — es donde está configurado el worker hoy |
| `192.168.15.12` | bd_epysa_peru | Confirmar con infraestructura |

### Antes de pasar a producción

1. Ejecutar **los 4 scripts de `sql\`** en el servidor de producción. Las tablas `rg_*`,
   las homologaciones y el usuario `rindegastos` existen solo en QA.
2. El `mus_cod_usuario` que genere producción **será distinto** al de QA: hay que
   actualizar `CodUsuarioErp`.
3. Cambiar `BaseDatos:CadenaConexion`, preferentemente por variable de entorno:
   ```bash
   setx /M BaseDatos__CadenaConexion "Data Source=<servidor-prod>;Initial Catalog=bd_epysa_peru;..."
   ```
4. Volver a `SoloLectura: true` durante los primeros días en producción.

> Mientras la cadena apunte a QA, activar `SoloLectura: false` es seguro: lo peor que
> puede pasar es que quede un comprobante de prueba, y se borra.

---

# PASO A PASO

## Paso 1 — Crear las tablas

Abre **SQL Server Management Studio**, conéctate a `172.16.21.27`, base `bd_epysa_peru`,
y ejecuta en este orden:

| Script | Qué hace |
|---|---|
| `sql\01_tablas_rg.sql` | Crea `rg_gasto`, `rg_homologacion` y `rg_log_api` |
| `sql\02_homologaciones.sql` | Carga las equivalencias de tipo de documento y moneda |
| `sql\06_tabla_rg_informe.sql` | Crea `rg_informe`, del flujo de informes |
| `sql\07_tabla_rg_fondo.sql` | Crea `rg_fondo`, del flujo de entrega de fondos |
| `sql\08_tabla_rg_solicitud_fondo.sql` | Crea `rg_solicitud_fondo`, del flujo de solicitudes |

Los tres últimos tienen un índice filtrado: si los corres con `sqlcmd` en vez de SSMS,
agrega el modificador **`-I`**.

**No modifican ninguna tabla existente del ERP.** Solo crean tablas nuevas con prefijo `rg_`.
Y son idempotentes: si los corres dos veces, la segunda no hace nada.

Verificación:

```sql
SELECT name FROM sys.tables WHERE name LIKE 'rg_%';   -- debe devolver 6 filas
```

Para deshacer todo, si hiciera falta:

```sql
DROP TABLE dbo.rg_gasto; DROP TABLE dbo.rg_homologacion; DROP TABLE dbo.rg_log_api;
DROP TABLE dbo.rg_informe; DROP TABLE dbo.rg_fondo; DROP TABLE dbo.rg_solicitud_fondo;
```

---

## Paso 2 — Crear el usuario de la integración

Ejecuta `sql\04_usuario_integracion.sql`.

Crea un usuario llamado `rindegastos` en `mae_usuario` con perfil **173 (ASISTENTE
CONTABLE)** y **sin contraseña**, así que no puede iniciar sesión en el ERP. Solo existe
para que su código quede grabado en `mfb_cod_usuario` de cada factura.

Sin esto, los asientos automáticos quedarían a nombre de una persona real y en una
auditoría no se podría distinguir lo que hizo ella de lo que hizo el programa.

El script imprime al final el código generado:

```
mus_cod_usuario = 1234

Copia ese numero en appsettings.json:
    "Integracion": { "CodUsuarioErp": 1234 }
```

Anótalo: lo necesitas en el paso 4.

---

## Paso 3 — Guardar el token de Rindegastos

El token se genera en **Rindegastos → Administración → Integración**. Es una cadena
larga de texto que da acceso completo a la información de la empresa.

**No lo pongas en `appsettings.json`.** Ese archivo se copia, se comparte y puede
terminar en un repositorio. Se guarda aparte:

```bash
dotnet user-secrets set "Rindegastos:Token" "el-token-real" --project src\Rindegastos.Worker
```

### Dónde queda guardado

```
C:\Users\<tu-usuario>\AppData\Roaming\Microsoft\UserSecrets\rindegastos-erp-ics\secrets.json
```

Ese archivo vive **fuera del proyecto**. No se copia al compilar, no se sube a git y
solo lo puede leer tu usuario de Windows. El `rindegastos-erp-ics` sale del
`<UserSecretsId>` que está en el `.csproj`.

### De dónde lee la configuración el programa

Gana el último de la lista:

| Orden | Fuente | Para qué |
|---|---|---|
| 1 | `appsettings.json` | Valores que no son secretos |
| 2 | user-secrets | Tu equipo, mientras desarrollas |
| 3 | Variables de entorno | El servidor de producción |

Como los user-secrets son **por usuario de Windows**, en el servidor no sirven. Ahí se
usa una variable de entorno. El doble guion bajo `__` representa el `:`:

```bash
setx /M Rindegastos__Token "el-token-real"
```

### Ver o cambiar el token

```bash
dotnet user-secrets list --project src\Rindegastos.Worker
```

```bash
dotnet user-secrets remove "Rindegastos:Token" --project src\Rindegastos.Worker
```

> **Pendiente:** la contraseña de SQL sigue en texto plano dentro de `appsettings.json`.
> Conviene moverla igual:
> ```bash
> dotnet user-secrets set "BaseDatos:CadenaConexion" "Data Source=...;Password=..." --project src\Rindegastos.Worker
> ```

---

## Paso 4 — Configurar

Abre `src\Rindegastos.Worker\appsettings.json`:

```json
"Integracion": {
    "SoloLectura": true,
    "IntervaloMinutos": 15,
    "CodUsuarioErp": 0,
    "FechaDesde": "2026-01-01",
    "StatusGasto": 1,
    "MaxIntentos": 3
}
```

| Opción | Qué hace |
|---|---|
| `SoloLectura` | **`true` = modo seguro.** Descarga y traduce, pero no escribe nada en contabilidad |
| `IntervaloMinutos` | Cada cuánto se repite el ciclo |
| `CodUsuarioErp` | El código del paso 2 |
| `FechaDesde` | Solo se traen gastos emitidos desde esta fecha |
| `StatusGasto` | 1 = solo gastos aprobados |
| `MaxIntentos` | Después de tantos fallos, el gasto queda en estado ERROR |

**Deja `SoloLectura` en `true` por ahora.** Se cambia recién en el paso 7.

---

## Paso 5 — Compilar

Doble clic en **`compilar.cmd`**. Debe terminar con *"Compilación correcta"*.

Solo hace falta cuando cambia el código. Si únicamente tocaste `appsettings.json`, no.

---

## Paso 6 — Primera corrida

Doble clic en **`ejecutar-una-vez.cmd`**. Corre un ciclo y termina.

```
[11:26:32 INF] Configuracion validada. Modo solo lectura: True
[11:26:33 INF] getExpenses pagina 1/1: 8 gastos
[11:26:33 INF] Descargados 8 gastos: 8 nuevos, 0 ya conocidos
[11:26:33 INF] Homologando 8 gastos
[11:26:34 WRN] Gasto 72622902 no homologado: El proveedor con RUC 20536401254 no existe...
[11:26:34 INF] Homologacion terminada: 0 de 8 correctos
[11:26:34 WRN] MODO SOLO LECTURA: no se contabiliza nada.
```

Los logs también quedan en `src\Rindegastos.Worker\logs\rindegastos-AAAA-MM-DD.log`.

### Las dos formas de ejecutarlo

| Forma | Qué hace | Termina solo | Para qué |
|---|---|---|---|
| `ejecutar-una-vez.cmd` | Un ciclo y sale | Sí | **Pruebas.** Tú decides cuándo corre |
| Servicio de Windows | Repite cada N minutos, sin ventana | No | **Producción** (paso 9) |

Las dos hacen exactamente lo mismo en cada ciclo. Solo cambia quién decide cuándo se repite.

Si alguna vez necesitas ver el modo continuo en consola (por ejemplo, para diagnosticar
en el servidor cuando el servicio no arranca), ejecuta el programa **sin** el parámetro:

```bash
dotnet run --project src\Rindegastos.Worker
```

Se queda corriendo y repite hasta que lo cortes con Ctrl+C. No hace falta un `.cmd` para eso.

---

## Paso 7 — Revisar y corregir

Abre `sql\03_consultas_monitoreo.sql`. **No se ejecuta completo**: seleccionas el bloque
que necesites y presionas F5.

Si solo vas a usar una consulta, usa **A0**: lista todos los gastos, uno por fila, con
el estado en texto y el motivo si falló.

```
id_rindegastos | estado                       | proveedor      | documento     | total | motivo_si_fallo
72622902       | 9 - ERROR                    | LA CAPSULA SAC | BJL1-00066559 | 17.00 | El proveedor con RUC ... no existe
```

### Los 5 estados

| Estado | Significa | Qué hacer |
|---|---|---|
| **0** | Descargado: llegó de Rindegastos, aún sin traducir | Esperar al próximo ciclo |
| **1** | Homologado: listo para contabilizar | Esperar al próximo ciclo |
| **2** | Contabilizado en el ERP, falta avisar a Rindegastos | Se reintenta solo |
| **3** | Terminado | Nada |
| **9** | **Error**: agotó los reintentos | Revisar `rgg_ultimo_error` y corregir |

> **Cuidado al leer A1.** Devuelve una fila por estado, y la primera columna es el
> **código** del estado, no la cantidad:
> ```
> estado | descripcion    | cantidad
>    0   | 0 - Descargado |    8      ← son 8 gastos, no cero
> ```

Los bloques que más vas a usar:

| Bloque | Para qué |
|---|---|
| **B2** | Agrupa los errores por tipo: te dice qué atacar primero |
| **C1** | Proveedores que hay que crear en el ERP |
| **C3** | Categorías cuya cuenta contable no existe |
| **D1** | Documentos cargados varias veces en Rindegastos |
| **J1** | Reintentar un gasto después de corregir el dato |

Después de crear un proveedor que faltaba, el gasto se reintenta así:

```sql
UPDATE rg_gasto SET rgg_estado = 0, rgg_intentos = 0, rgg_ultimo_error = NULL
WHERE rgg_id = 72622902;
```

**Repite los pasos 6 y 7 hasta que la mayoría quede en estado 1.** Ese trabajo de
limpieza es el objetivo del modo solo lectura: descubres todo lo que falta sin haber
tocado un solo asiento contable.

---

## Paso 8 — Activar la escritura

> ⚠️ **A partir de aquí se generan asientos contables reales en producción.**
> No lo hagas hasta que Contabilidad haya revisado lo que quedó en `rg_gasto`.

```json
"Integracion": {
    "SoloLectura": false,
    "CodUsuarioErp": 1234
}
```

Vuelve a correr `ejecutar-una-vez.cmd` y verifica:

```sql
-- E1: qué comprobantes se generaron
SELECT rgg_id, rgg_supplier, rgg_total, rgg_cod_factura_boleta, rgg_cod_comprobante
FROM rg_gasto WHERE rgg_estado >= 2;
```

```sql
-- E3: comprobantes que no cuadran. Debe devolver 0 filas SIEMPRE.
```

---

## Paso 9 — Pasar a producción

> Los pasos 1 a 8 se hicieron contra **QA** (`172.16.21.27`). Producción arranca de cero:
> las tablas `rg_*`, las homologaciones y el usuario `rindegastos` **no existen allá**.

### Qué hace el servicio una vez instalado

Cada N minutos, **sin que nadie intervenga**:

1. Descarga de Rindegastos **todos** los gastos aprobados y no integrados. No hay tope:
   pagina de 100 en 100 hasta traerlos todos.
2. Homologa y **contabiliza todos los que resuelvan correctamente**, uno por uno,
   cada uno en su propia transacción.
3. Devuelve a Rindegastos el número de comprobante de cada uno.

Los que no resuelven quedan en estado 9 y **el servicio no los vuelve a tocar**.
Nadie se entera hasta que alguien mire. Por eso, mientras no exista la pantalla de
monitoreo ni las alertas por correo, **hay que revisar la consulta A0 todos los días**.

### 9.1 Preparar la base de datos de producción

Ejecutar en el servidor de producción, en este orden:

| Script | Qué crea |
|---|---|
| `sql\01_tablas_rg.sql` | `rg_gasto`, `rg_homologacion`, `rg_log_api` |
| `sql\02_homologaciones.sql` | Equivalencias de tipo de documento y moneda |
| `sql\04_usuario_integracion.sql` | El usuario `rindegastos` |

Anota el `mus_cod_usuario` que devuelve el último: **será distinto al de QA**.

Antes de continuar, verifica que los catálogos de producción tengan lo que la
integración necesita — pueden diferir de QA:

```sql
-- Los centros de costo que manda Rindegastos, existen?
SELECT rcc_cod_centro_costo, rcc_nombre_centro_costo FROM ref_centro_costo
WHERE rcc_cod_centro_costo IN (3,25,29,49,87,229,288);
```

```sql
-- Las cuentas de las categorias, existen y estan vigentes?
SELECT mpc_codigo_cuenta, mpc_nombre, mpc_cuenta_vigente FROM mae_plan_cuenta
WHERE mpc_codigo_cuenta IN ('6251010','6251110','6311015','6311030','6311135',
                            '6353010','6354010','6354020','6365010','6399010',
                            '6561070','6595010','4212030','4212040','4240010','4011020');
```

### 9.2 Publicar

En tu equipo:

```bash
dotnet publish src\Rindegastos.Worker -c Release -o C:\Publicar\Rindegastos
```

Copia esa carpeta al servidor, por ejemplo a `C:\Servicios\RindegastosIntegracion`.

> **No copies `appsettings.Local.json`.** Tiene el token de tu equipo y la conexión a QA.
> En el servidor la configuración va por variables de entorno (9.3).

### 9.3 Configurar el servidor

Variables de entorno a nivel máquina, para que el servicio las vea aunque corra con
otra cuenta de Windows. El doble guion bajo `__` representa el `:` de la configuración:

```bash
setx /M Rindegastos__Token "el-token-de-produccion"
```

```bash
setx /M BaseDatos__CadenaConexion "Data Source=<servidor-prod>;Initial Catalog=bd_epysa_peru;User ID=...;Password=...;TrustServerCertificate=True"
```

```bash
setx /M Integracion__CodUsuarioErp "<el codigo del paso 9.1>"
```

Y **arranca en modo seguro** los primeros días:

```bash
setx /M Integracion__SoloLectura "true"
```

### 9.4 Probar antes de instalar el servicio

Desde el servidor, en la carpeta donde copiaste el programa:

```bash
Rindegastos.Worker.exe --una-vez
```

Debe conectar, descargar y **no contabilizar nada** (`SoloLectura = true`).
Revisa la consulta A0 en la base de producción. Aquí es donde aparecen los proveedores
y categorías que existen en QA pero no en producción.

**No sigas hasta que esto salga limpio.**

### 9.5 Instalar el servicio

```bash
sc.exe create RindegastosIntegracion binPath= "C:\Servicios\RindegastosIntegracion\Rindegastos.Worker.exe" start= auto DisplayName= "Integracion Rindegastos - ERP ICS"
```

```bash
sc.exe description RindegastosIntegracion "Trae los gastos aprobados de Rindegastos y los contabiliza en el ERP."
```

```bash
sc.exe start RindegastosIntegracion
```

Comandos útiles:

```bash
sc.exe query RindegastosIntegracion
```

```bash
sc.exe stop RindegastosIntegracion
```

```bash
sc.exe delete RindegastosIntegracion
```

Los logs quedan en `C:\Servicios\RindegastosIntegracion\logs\rindegastos-AAAA-MM-DD.log`
(30 días) y en la tabla `rg_log_api`.

> La cuenta con la que corre el servicio necesita **permiso de escritura** en esa
> carpeta de logs.

### 9.6 Activar la escritura

Cuando la consulta A0 de producción esté limpia:

```bash
setx /M Integracion__SoloLectura "false"
```

```bash
sc.exe stop RindegastosIntegracion && sc.exe start RindegastosIntegracion
```

El servicio no relee las variables de entorno en caliente: hay que reiniciarlo.

### Checklist de puesta en marcha

- [ ] Los 3 scripts SQL ejecutados en producción
- [ ] `mus_cod_usuario` de producción anotado y configurado
- [ ] Centros de costo y cuentas verificados en producción
- [ ] Programa publicado y copiado, **sin** `appsettings.Local.json`
- [ ] Variables de entorno configuradas
- [ ] El servidor tiene salida a `api.rindegastos.com`
- [ ] Prueba `--una-vez` en modo solo lectura, limpia
- [ ] Servicio instalado y arrancando solo tras reiniciar el servidor
- [ ] Permisos de escritura en la carpeta `logs`
- [ ] Contabilidad sabe que debe revisar A0 a diario
- [ ] Definido quién resuelve los gastos en estado 9

---

# Reglas de negocio implementadas

Todas verificadas contra comprobantes reales de `bd_epysa_peru`.

### Cómo se reparte el total

| Tipo de documento | `mfb_monto_neto` | `mfb_monto_impuesto` | `mfb_monto_exento` |
|---|---|---|---|
| Factura (01) | `total / (1 + tasa)` | `neto × tasa` | 0 |
| Boleta (03) | 0 | 0 | `total` |
| Recibo por Honorarios (R1) | 0 | 0 | `total` |

**La tasa de IGV es la que eligió quien rindió el gasto en Rindegastos**
(`Taxes.taxPercentage`), no la del ERP. No todas las facturas son al 18%: la factura
`FAA1-32729996` del gasto 79222741 lleva **10.5%** ("IGV 10.5%"), y con 18% quedaba mal
(neto 457.63 en vez de 488.69). Solo aplica a facturas; boletas y recibos van a exento.

Si el gasto no trae tasa, se usa la del ERP (`ref_parametro_general_calculo`,
parámetro 1 = 18.00) y queda un aviso en el log.

El neto y el IGV calculados se comparan con los montos que manda Rindegastos, y si no
coinciden el gasto se detiene con error. Esa comparación se salta en dos casos, porque
ahí los montos de Rindegastos no son comparables:

- **Gastos parciales**: `Net` y `taxAmount` son del documento completo, mientras que el
  ERP desagrega solo la parte que asume la empresa.
- **Montos desactualizados**: cuando `Net + taxAmount` no suma el monto del gasto, señal
  de que el importe se corrigió después en Rindegastos (el gasto 78988720 manda
  57.04 + 10.26 para un gasto de 20.00). Ahí se desagrega con la tasa y se avisa en el log.

### Líneas del comprobante

**Factura** — 3 líneas

| Cuenta | Análisis | Tipo doc | N° doc | Centro costo | Cargo | Abono |
|---|---|---|---|---|---|---|
| 4212030 / 4212040 | proveedor | del encabezado | folio | — | | **total** |
| 4011020 | 1 | del encabezado | folio | — | **IGV** | |
| CategoryCode | según la cuenta | según la cuenta | según la cuenta | sí | **neto** | |

**Boleta y Recibo por Honorarios** — 2 líneas

| Cuenta | Análisis | Tipo doc | N° doc | Centro costo | Cargo | Abono |
|---|---|---|---|---|---|---|
| 4212030 (boleta) / 4240010 (RxH) | proveedor | del encabezado | folio | — | | **total** |
| CategoryCode | según la cuenta | según la cuenta | según la cuenta | sí | **total** | |

En la línea de gasto, el análisis, el tipo de documento y el número **no son fijos**:
se llenan solo si la cuenta lo pide (`mpc_requiere_analisis`, `mpc_requiere_tipo_documento`,
`mpc_requiere_numero_documento`). Es lo mismo que hace `CargaCuenta()` en el ERP: si la
bandera está en 0, el campo queda deshabilitado en la pantalla y se graba el valor por
defecto (análisis 1, tipo de documento 27, número 0).

### Gastos parciales

Un gasto parcial es un documento del que la empresa asume solo una parte y el resto
se le cobra al trabajador. Rindegastos lo marca con dos campos extra:

| Campo | Ejemplo | Qué es |
|---|---|---|
| `¿Es un gasto parcial?` | `Si` | activa el reparto |
| `OriginalAmount` | 30.00 | **lo que asume la empresa** |
| `Monto total` | 36.00 | **el total real del documento** |
| la diferencia | 6.00 | **lo que paga el trabajador** |

Ojo con `OriginalAmount`: en un gasto normal es el total del documento, pero en uno
parcial pasa a ser solo la parte de la empresa.

**Cómo se reparte.** El neto y el IGV se calculan únicamente sobre lo que asume la
empresa: sobre la parte del trabajador no se toma crédito fiscal, va a exento. En boleta
y recibo por honorarios no hay crédito fiscal en absoluto, así que el exento es el total
completo.

**El asiento lleva una línea más**, en `1419010` (Otras cuentas por cobrar al personal),
con el análisis del proveedor y el tipo y número de documento del encabezado. No lleva
centro de costo, y esa cuenta no tiene versión en moneda extranjera: se usa la misma en
soles y en dólares. El ERP no la genera solo — el contador la agrega a mano desde la
pantalla, igual que la línea del gasto.

Los cuatro casos verificados contra los registros que ingresó Contabilidad:

```
FACTURA PARCIAL  E001-3527  (comprobante 2606127)   empresa 30.00 de un total de 36.00
  cabecera: neto 25.42 + IGV 4.58 + exento 6.00 = 36.00
  4212030  analisis=proveedor  td=1   numdoc=folio   abono 36.00   ← total del documento
  4011020  analisis=1          td=1   numdoc=folio   cargo  4.58   ← IGV solo sobre los 30
  6251010  analisis=1  cc=288  td=27  numdoc=0       cargo 25.42   ← el gasto
  1419010  analisis=proveedor  td=1   numdoc=folio   cargo  6.00   ← al trabajador

RECIBO POR HONORARIOS PARCIAL  E001-67  (comprobante 2606165, tipo 10 Honorarios)
                                empresa 100.00 de un total de 180.00
  cabecera: exento 180.00 = 180.00   (el RH no da credito fiscal)
  4240010  analisis=proveedor  td=15  numdoc=folio   abono 180.00
  6399010  analisis=1  cc=288  td=27  numdoc=0       cargo 100.00  ← solo lo que asume la empresa
  1419010  analisis=proveedor  td=15  numdoc=folio   cargo  80.00  ← al trabajador

BOLETA PARCIAL   0002-242   (comprobante 2606129)   empresa 200.00 de un total de 324.00
  cabecera: exento 324.00 = 324.00   (la boleta no da credito fiscal)
  4212030  analisis=proveedor  td=14  numdoc=folio   abono 324.00
  6251110  analisis=1  cc=288  td=27  numdoc=0       cargo 200.00  ← solo lo que asume la empresa
  1419010  analisis=proveedor  td=14  numdoc=folio   cargo 124.00  ← al trabajador
```

Dos detalles más que salen de comparar con los registros manuales:

- **La línea `1419010` apunta a su factura** (`mdco_cod_documento_origen`), igual que la
  del proveedor. Es lo que hace `btnGrabaDetalle` (líneas 2013-2018) con cualquier cuenta
  que pida tipo de documento. Verificado: 427 de 427 líneas de `1419010` en compras de
  2026 apuntan a su propia factura.
- **La glosa del gasto lleva la nota completa**, incluida la aclaración del reparto
  (`"... / SOLO SE CONSIDERA S/30.00 DEL TOTAL DE S/36.00"`). Así lo pidió Contabilidad,
  para que el comprobante explique solo por qué el gasto es menor que el documento. Ojo
  al comparar con los registros manuales antiguos: ahí el contador la cortaba antes del
  `" / "`. `mdco_glosa` admite 150 caracteres; lo que pase de ahí se recorta al grabar.

**Si el gasto dice `Si` pero no trae `Monto total`, se detiene con un mensaje** en vez de
registrarlo por el monto equivocado. Hoy hay tres gastos así en Rindegastos
(`F001-3030`, `B001-2020`, `F001-00000065`): son anteriores a que se agregara ese campo
a la política.

### Alta automática de proveedores

Si el RUC del gasto no existe en `mae_proveedor`, el worker da de alta el proveedor antes de
registrar el gasto, igual que `Abastecimiento > Mantenedor de Proveedores`
(`wctrMantenedorProveedores2.ascx`, `btnGrabar_Click`).

**Solo lo crea si el contribuyente está `ACTIVO` y `HABIDO`.** Cada dato se toma de dos
lugares de `SunatInfo`, en este orden:

| Dato | Primero | Si viene vacío |
|---|---|---|
| Estado | `DocTaxpayerStatus` (validación del comprobante) | `taxPayerStatus` de `ExtractedData` (padrón de contribuyentes) |
| Condición | `DocTaxpayerAddressCondition` | `condition` de `ExtractedData` |

Los campos `Doc…` vienen vacíos cuando falla la validación del comprobante (por ejemplo
*"NO EXISTE - Comprobante no informado"*), pero el padrón sigue informando si el contribuyente
está activo y habido. Si el campo `Doc…` trae un valor, ese manda: un `NO HABIDO` ahí bloquea
aunque el padrón diga `HABIDO`. Cuando el alta se decide con el padrón, queda un aviso en el log
con el resultado de la validación del comprobante.

En cualquier otro caso no lo crea y el gasto queda detenido con el motivo, para que
Contabilidad lo revise. La consulta **C1** los lista.

| Campo de la pantalla | Valor |
|---|---|
| RUC (o ID) y NIF | `SunatInfo.Ruc` |
| Nombre y razón social | `SunatInfo.BusinessName` (la razón social en mayúsculas, como la pantalla) |
| Nacional / Vigente | Sí / Sí |
| Moneda / País | Soles / Perú |
| Forma de pago | Efectivo |
| Tipo de proveedor | Artículos **y** Servicios |
| Todo lo demás | vacío (NULL) |

Lo que hace, en una sola transacción, con los mismos procedimientos que la pantalla:
`sp_insert_mae_proveedor_1` (que además llena `mae_anexo_concar` para CONCAR), un
`sp_insert_nub_tipo_proveedor_1` por cada tipo, `usp_abs_actualiza_cod_centralizacion` al marcar
Artículos, y `sp_insert_tran_analisis_1` con el análisis contable `PR`. Sin ese análisis la
factura no se puede grabar. No graba lead time ni direcciones: la pantalla tampoco lo hace al
crear un proveedor nuevo. Verificado contra el alta manual del proveedor 17024.

No se crea si:

- no está `ACTIVO` y `HABIDO`, o ni la validación ni el padrón informan su estado.
- el gasto no trae `SunatInfo` (por ejemplo, un DNI en vez de RUC, o un no domiciliado).
- el RUC que devuelve SUNAT no coincide con el del gasto.

En modo solo lectura no crea nada: solo avisa en el log qué proveedor crearía.

### Gastos corregidos en Rindegastos

En cada ciclo, los gastos que todavía no se contabilizaron (estados 0, 1 y 9) se actualizan
con lo que devuelve la API. **Si el gasto cambió en Rindegastos, vuelve a pendiente** con 0
intentos y se procesa de nuevo. Antes el worker se quedaba con la primera versión del gasto y
las correcciones hechas en la plataforma no llegaban. Los estados 2 y 3 no se tocan nunca.

### Comprobante no domiciliado (factura del exterior)

Llega con `Tipo de Documento` Code **`91`** y el campo extra `¿Es factura no
domiciliada?` = `Sí`. En el ERP es el tipo **37** (COMPROBANTE NO DOMICILIADO).

> El código SUNAT `91` lo comparten tres documentos del ERP: 25 Factura de exportación,
> 32 Factura de importación y 37 Comprobante no domiciliado. Sin configuración, la
> búsqueda directa devolvía el **25**, que es incorrecto. Por eso hay una fila
> `91 → 37` en `rg_homologacion` (`sql\02_homologaciones.sql`).

Verificado contra la factura `3347246` que ingresó Contabilidad (262038 / 2606151):

- **Montos**: no hay crédito fiscal, todo va a exento (neto 0, IGV 0, exento = total).
- **Tipo de cambio**: el vigente a la fecha de emisión (21/06/2026 → 3.3860), como en
  cualquier compra.
- **Línea de IGV en cero**: el ERP la crea igual, con 0.00. La pantalla genera sola la línea
  de `4011020` para los tipos 1, 52, 51, 6, 13, 34, 50, 41 y 37, aunque el IGV sea cero
  (`btnGrabar`, líneas 1600-1608). Boleta y recibo por honorarios no están en esa lista.
- **Bloque de no domiciliados** (desde "Periodo Dua" hasta "Modalidad del Serv."): Rindegastos
  no manda estos datos. Se graban siempre con los valores del registro manual:

  | Campo | Valor |
  |---|---|
  | Tipo Doc. Dua / N° Serie / Doc. asoc. | 37 / `0` / `0` |
  | Tipo Vinculación (A1. T17) | 1 — Sin vinculación |
  | Convenio Doble Trib. (A1. T18) | 1 — Ninguno |
  | Exoneraciones (A1. T21) | 1 |
  | Tipo Renta (A1. T19) | 4 — Rentas de bienes o derechos utilizados en el país |
  | Modalidad del Serv. (A1. T20) | 3 — Servicio prestado exclusivamente en el extranjero |

  La pantalla solo habilita este bloque para el tipo 37; en los demás documentos queda en
  sus valores vacíos. Los valores están en la clase `DatosNoDomiciliado`.

- **Periodo Dua**: se graba la fecha de contabilización, no el 13/08/2026 del registro
  manual. El Libro de Compras No Domiciliados solo lee el **año** de ese campo
  (`usp_ctb_neo_libro_compra_le_82v2`, `Campo12 = LEFT(FechaDocAsoc82, 4)`): una fecha fija
  haría que desde 2027 todo saliera como 2026.

**Lo que el worker no hace:** en el registro manual el gasto está repartido entre
`6365010` (jul-dic 2026) y `1890010` Otros Gastos Pagados por Anticipado (ene-jul 2027). Ese
reparto es una decisión contable que no viene en los datos de Rindegastos, así que el worker
carga el total a la cuenta del gasto.

### Cabecera

| Campo | Valor |
|---|---|
| `mfb_folio` | ExtraField "Nro Documento" |
| `mfb_cod_proveedor` | por ExtraField "Ruc Proveedor" (respaldo: `SunatInfo.Ruc`) |
| `mfb_fecha_emision` / `mfb_fecha_vencimiento` | `IssueDate` |
| `mfb_fecha_creacion` | fecha actual del sistema |
| `mfb_cod_moneda` | `Currency` vía `ref_moneda.rmo_iso` |
| `mfb_tipo_cambio` | 1 si es PEN; si no, `tran_tipo_cambio` a la fecha de emisión |
| `mfb_cod_tipo_forma_pago` | 1 (Efectivo) |
| `mfb_cod_estado_global` | 43 (Factura Compra Cerrada) |
| `mcm_cod_tipo_comprobante` | 10 si es RxH, 5 en los demás casos |
| `mcm_folio` | `MAX(mcm_folio) + 1` del año, mes y tipo |
| `mcm_glosa` | `"RAZÓN SOCIAL Nºfolio"` |

### Equivalencia de tipo de documento

| `Code` de Rindegastos | `rtdc_cod_sunat` | `rtdc_cod_tipo_documento_contable` |
|---|---|---|
| `01` Factura | 01 | **1** |
| `03` Boleta de Venta | 03 | **14** |
| `R1` Recibo por Honorarios | 02 | **15** |

---

# Protección contra duplicados

Cuatro barreras, en este orden:

1. **`rgg_id` es clave primaria** de `rg_gasto`. Un gasto ya descargado se ignora.
2. **Se confirma antes de descargar.** El primer paso del ciclo reintenta avisar a
   Rindegastos de lo ya contabilizado. Si en el ciclo anterior se grabó el comprobante
   pero falló la llamada a la API, el gasto queda en estado 2 y ese estado nunca se
   vuelve a contabilizar.
3. **Folio + proveedor + tipo de documento.** Antes de grabar se verifica que la
   factura no exista ya en `mae_factura_boleta`. Esto cubre el caso de una boleta
   cargada varias veces en Rindegastos: cada copia tiene un `Id` distinto, así que
   la barrera 1 no la detecta.
4. **Índice único** sobre `rgg_cod_comprobante`: un comprobante del ERP solo puede
   provenir de un gasto.

Además, todo el paso de contabilización ocurre en **una sola transacción**. Si falla al
grabar la tercera línea, se deshacen también la factura y la cabecera del comprobante.
Y antes del commit se valida que **cargo = abono**: si no cuadra, hace rollback.

---

# Segundo flujo: informes (rendiciones)

Además de los gastos con documento, el worker integra los **informes** de gastos, que en
el ERP entran por `Contabilidad > Comprobantes > Ingreso de Comprobante`
(`wctrComprobanteContable.ascx`).

La diferencia de fondo con el otro flujo: aquí la unidad de trabajo es el **informe**,
no el gasto. Un informe con cinco gastos produce **un solo comprobante** con una línea
por gasto más la contrapartida.

**Solo se integran informes cerrados** (`Status = 1`, aprobados por todo el flujo). Un
informe con `Status = 0` (le falta un aprobador) queda **en espera**. Si se descargó
abierto, en cada ciclo se vuelve a leer de la API y entra cuando llega cerrado.

### Cómo se sabe qué gastos entran en cada informe

Cada gasto trae `ReportId`, que es el `Id` del informe. Además `getExpenses` acepta
`ReportId` como filtro, y así es como el worker los pide. Los gastos **rechazados**
(`Status = 2`) no entran.

### Qué pasa con cada tipo de gasto

El campo extra **`Tipo de Documento`** del gasto:

| Code | Qué es | Compras (flujo 1) | Informe (flujo 2, al cerrarse) |
|---|---|---|---|
| `PL` | Planilla de Movilidad | — | línea a la cuenta de gasto (`6311015`) |
| `01`, `03`, `R1`, `91` | Factura, Boleta, RxH, No domiciliado (totales o parciales) | se registra el documento | se **cancela** contra la cuenta del proveedor |

Un informe puede traer las dos cosas mezcladas: todo va al mismo comprobante.

### Informes con facturas, boletas y RxH

El documento entra primero por compras apenas se aprueba el gasto (flujo 1). Cuando el
informe se cierra, el comprobante del informe lo **cancela**: cargo a la cuenta del
proveedor por el total del documento y abono a la persona que rinde. Si el gasto es
parcial, además lleva un abono a `1419010` por lo que paga el trabajador. Verificado
contra lo que Contabilidad registró a mano:

```
DOCUMENTOS TOTALES — 2606175, entrega a rendir
  4212030  analisis=proveedor  td=1   E001-2151  cargo  413.33  glosa = nota del gasto
  4212030  analisis=proveedor  td=14  0003-362   cargo   55.00
  4240010  analisis=proveedor  td=15  E001-16    cargo  750.00
  1413110  analisis=empleado   td=26  15092026   abono 1218.33  glosa = título del informe

FACTURA PARCIAL — 2606128 (empresa 30.00, trabajador 6.00)
  4212030  analisis=proveedor  td=1   E001-3527  cargo   36.00
  1419010  analisis=proveedor  td=1   E001-3527  abono    6.00  glosa = título del informe
  1413110  analisis=empleado   td=26             abono   30.00
```

- **Cuenta, análisis, tipo y número de documento** se copian de la línea del proveedor
  en el comprobante de compra, no se recalculan: así se cancela exactamente lo que se
  registró, aunque se haya registrado a mano. Por eso en dólares sale solo `4212040` /
  `4240020`.
- En esas líneas el **centro de costo va vacío**, **sin documento de origen**, y el
  **vencimiento es la fecha del comprobante**.
- La **contrapartida no lleva centro de costo** si el informe solo tiene documentos
  (2606128, 2606130, 2606152, 2606162, 2606166, 2606175). Si tiene planillas de movilidad
  lleva el del informe, como en 2606122–2606125.
- **El documento se busca** primero en `rg_gasto` (lo registró el worker); si no está, por
  RUC + tipo + folio (lo registró Contabilidad a mano).
- **Si el documento todavía no está en compras**, el informe queda en espera. Normalmente
  entra en el mismo ciclo, porque el flujo de compras corre antes. Si ese gasto está en
  error en compras, el motivo de la espera lo dice.
- **Se detiene con error** si el monto que asume la empresa en compras no coincide con
  el del gasto en Rindegastos, si la moneda no coincide, o si el documento **ya se canceló**
  en otro comprobante (por ejemplo, rendido a mano o pagado al proveedor).
- En Rindegastos solo se marcan como integradas las **planillas** del informe. Las
  facturas, boletas y RxH ya las marcó el flujo de compras con el código de su propio
  comprobante.

### Quién rinde: siempre el usuario que envía el informe

La contrapartida va **siempre** a nombre del usuario que envió el informe, traiga o no
planillas de movilidad. El DNI sale de `Employee.Identification`:

```jsonc
"Employee": {
    "Id": 148283,
    "Name": "Contabilidad Implementos",
    "Identification": "73026091",   // <-- este
    ...
}
```

Con ese documento se busca el empleado (`mae_empleado.men_rut`) y su análisis contable.
Si el perfil no tiene documento, el informe se detiene con un mensaje pidiendo
completarlo en Rindegastos.

**El campo extra `Ruc Proveedor` del gasto no sirve para esto:** en una factura es el RUC
del comercio, y en una planilla de movilidad es el DNI de quien se movilizó, que puede
ser **otra persona**. Verificado en el comprobante 2606225: la planilla es de Karen
Pereda (DNI 73833161) y la contrapartida quedó igual a nombre de Hiroshi (73026091), que
fue quien envió el informe.

**No duplicar lo que se registró a mano.** Además del candado por documento, antes de
grabar se busca un comprobante con la misma glosa, tipo, cuenta de contrapartida y monto.

**No duplicar lo que se registró a mano.** Además del candado por documento, antes de
grabar se busca un comprobante con la misma glosa, tipo, cuenta de contrapartida y monto.

### Las cuatro rendiciones

El campo extra `Tipo de rendición` del **informe** decide todo:

| Tipo de rendición | Tipo de comprobante | Contrapartida en soles | Contrapartida en dólares | Número de documento |
|---|---|---|---|---|
| Entrega a rendir | 17 Diario | `1413110` Entregas a Rendir Cta M.N. | `1413010` Entregas a Rendir Cuenta ME ⚠️ | `ddMMyyyy` del día de registro |
| Viáticos | 17 Diario | `1413110` Entregas a Rendir Cta M.N. | `1413010` Entregas a Rendir Cuenta ME ⚠️ | `ddMMyyyy` del día de registro |
| Reembolso | 17 Diario | `4699210` Otras Ctas por Pagar Diversas MN | `4699220` Otras Ctas por Pagar Diversas ME | `ddMMyyyy` del viernes + correlativo |
| Caja Chica | 2 Caja Egreso | `4690210` Ctas por Pagar Cajas Chicas MN | `4690215` Ctas por Pagar Cajas Chicas ME | `aaaa-nnnn` |

**Moneda.** Sale de la moneda *original* de los gastos (`OriginalCurrency`), no de la del
informe, que siempre trae la de la política (PEN). Si los gastos de un informe vienen en
monedas distintas, se detiene: un comprobante va en una sola moneda. El tipo de cambio es
el vigente en `tran_tipo_cambio` **a la fecha del comprobante**, igual que la pantalla
(`wctrComprobanteContable.ascx.vb`, línea 1239). Verificado en el comprobante 2598232 del
15/06/2026: tipo de cambio 3.389, el vigente ese día. En soles, cada línea es tipo de
cambio × monto redondeado a 2 decimales; la contrapartida es la **suma** de esas líneas,
para que cuadre al céntimo (así está en el comprobante real 2598271: 151.01 + 236.99 =
388.00, cuando 114.49 × 3.389 daría 388.01).

**Los correlativos van por cuenta**: la de soles y la de dólares llevan cada una su
numeración. Verificado con el reembolso en dólares que Contabilidad ingresó el 10/09
(comprobante 2606152, `4699220`, número `1809202601`).

> ⚠️ **`1413010` no la indicó Contabilidad**: se tomó de la BD, donde en 2026 aparece 27
> veces al abono de comprobantes Diario en dólares, con la misma forma que la de soles
> (comprobante 2598232). Hay que confirmarla. Y `4690215` (caja chica en dólares) existe
> y está vigente, pero no se ha usado ni en 2025 ni en 2026, así que no hay un registro
> real con qué compararla.

Rindegastos manda ese campo con `Code` vacío, así que se reconoce por el texto. Se
compara sin tildes, espacios ni mayúsculas, porque llega sucio (`"Viáticos "` viene con
un espacio al final).

### El número de documento de la contrapartida

Ninguno viene de la API: los arma Contabilidad. Está todo en una sola clase,
`CalculadorDocumentoRendicion`, para poder leerlo y corregirlo en un sitio.

**Reembolso.** El vencimiento es el viernes de la semana en que se registra, más 7 días
(es decir, el segundo viernes contando desde el registro). Sacado del histórico de 2026,
que es consistente en los ocho casos revisados: miércoles 03/06 → 12/06, jueves 18/06 →
26/06, viernes 04/09 → 11/09.

> **Regla confirmada por Contabilidad:** un reembolso registrado viernes, sábado o
> domingo salta un viernes más (viernes 11/09 → 25/09). Ninguno de los tres viernes del
> histórico se comportaba así, pero es la regla indicada: `SaltoViernesFinDeSemana = 14`.

**Los correlativos van por persona, no por empresa.** Se calculan con el máximo ya usado
más uno, no contando filas, porque cada liquidación deja dos líneas con el mismo número
y hay huecos por errores de digitación:

- Reembolso: cuántos lleva esa persona para ese mismo viernes. Carlos Cisneros tiene
  `1206202601`, `1206202602` y `1206202603`; otras personas del mismo viernes arrancan
  de nuevo en `01`.
- Caja chica: la secuencia anual de esa persona. En 2026 el número `2026-0001` aparece
  34 veces, una por cada persona que hizo su primera liquidación del año.

**La persona es siempre quien envía el informe** (`Employee.Identification`), la misma
que va en el análisis de la línea de contrapartida. Así, número y análisis son siempre
del mismo. Ejemplos verificados contra la base el 22/09/2026, en la cuenta `4690210`:

| DNI | Último usado | Siguiente que pone el worker |
|---|---|---|
| 76775158 (Kory Cancino) | ninguno | `2026-0001` |
| 73026091 (Hiroshi Momy) | `2026-0003` (comprobante 2606263) | `2026-0004` |

El conteo incluye lo registrado a mano, así que el worker **continúa la numeración de
Contabilidad** en vez de empezar la suya. La consulta **L11** muestra, por persona, en
qué correlativo va y cuál sería el siguiente. La cuenta en dólares (`4690215`) lleva su
propia numeración.

Dos detalles de funcionamiento: si dos informes de caja chica de la misma persona entran
en el mismo ciclo no chocan, porque cada uno recalcula el correlativo justo antes de
grabar, dentro de su propia transacción; y el correlativo no queda reservado, así que si
alguien registra a mano mientras el worker corre, el worker toma el siguiente libre en
ese momento.

### Lo que hace falta antes de que funcione

Cada persona que rinde tiene que tener un **análisis contable con el mismo documento
que tiene en Rindegastos**. Las tres cuentas de contrapartida exigen análisis
(`mpc_requiere_analisis = 1`), así que sin él no se puede grabar.

**Cómo se busca el análisis:** igual que en Ingreso de Comprobante. Cuando Contabilidad
escribe el DNI en el campo Análisis, la pantalla (`clsADAnalisis.mtdSelectAnalisis`)
busca en `tran_analisis` **solo por código interno, sin filtrar por tabla**, y toma la
primera fila. El worker hace lo mismo (`ORDER BY tan_cod_analisis`, que es el orden del
índice clustered). No se limita a análisis de empleado (`EM`) porque la mayoría de las
personas que rinden solo tienen análisis de **cliente** (`CL`), y es el que Contabilidad
usa en sus rendiciones manuales:

| Cuenta | Líneas con análisis CL | Líneas con análisis EM |
|---|---|---|
| 1413110 | 17,717 | 826 |
| 4699210 | 12,037 | 315 |
| 4690210 | 14,420 | 340 |

Por ejemplo, Yulianna Malma (DNI 43738540) solo tiene el análisis 124372 `CL`, y con ese
Contabilidad registró 2598106 y 2600293. Ningún empleado vigente tiene más de un análisis
con el mismo DNI, así que "tomar el primero" no es ambiguo.

Hoy quedan **21 empleados vigentes sin ningún análisis** con su DNI (consulta L4).

Cuando falta, el worker distingue los dos casos en el mensaje: *"no hay ningún empleado
con documento X"* frente a *"el empleado X existe pero no hay ningún análisis contable
con ese documento"*. El documento se guarda en `rg_informe` aunque falle, para que la
consulta L3 muestre a quién le falta.

### Diferencias con el módulo de compras, verificadas en el código del ERP

- El comprobante queda en **estado 17** ("Comprobante Abierto"), no en 19. En esa
  pantalla `blnTermina` vuelve a `True` en cada `Page_Load`, así que la rama del 19 no
  se alcanza (`btnGrabar_Click`, líneas 848-854).
- La auditoría del detalle dice **"Ingresado en Contabilidad"** en vez de "Ingresado en
  Detalle Por Compras.", porque la pantalla pasa `"Contabilidad"` como descripción
  (línea 998).
- Ninguna línea lleva documento de origen: no hay factura detrás.

### Configuración

```jsonc
"Integracion": {
  "ProcesarRendiciones": true,   // apaga este flujo sin tocar el otro
  "StatusInforme": 1             // 0 = abierto, 1 = cerrado, null = ambos
}
```

`StatusInforme` es propio de este flujo. **Cuidado:** en informes ese campo no significa
lo mismo que en gastos — aquí `0` es *abierto* y `1` es *cerrado*, mientras que en gastos
`1` es *aprobado*. Va en `1` porque el informe se integra cuando termina de aprobarse.
Aunque se ponga en `null`, la homologación igual deja esperando cualquier informe que no
esté cerrado.

Al pedir los informes el worker manda siempre `TypeDateFilter=2`, para que el filtro de
fecha se aplique sobre la fecha de envío. Por defecto la API filtra por fecha de cierre,
y un informe todavía abierto no tiene esa fecha: sin ese parámetro no aparece ninguno.

### Tabla

`sql\06_tabla_rg_informe.sql` crea `rg_informe`, con los mismos cinco estados que
`rg_gasto`. No toca ninguna tabla del ERP.

> Si ejecutas ese script o consultas la tabla con `sqlcmd`, usa el modificador **`-I`**.
> La tabla tiene un índice filtrado y SQL Server los rechaza cuando `QUOTED_IDENTIFIER`
> está apagado, que es como viene `sqlcmd` por defecto. SSMS ya lo trae encendido.

---

# Tercer flujo: entrega de fondos

Cuando Contabilidad crea un fondo en Rindegastos (una caja chica, por ejemplo) y le
deposita dinero, en el ERP eso es una **transferencia**: sale del banco de caja chica y
entra a la cuenta del fondo, a nombre de quien lo recibe. Entra por la misma pantalla,
`Ingreso de Comprobante`, pero con **tipo 19 (Banco Egreso-Transferencia)**.

La unidad de trabajo es el **depósito**, no el fondo: la entrega inicial y cada recarga
son transferencias distintas.

### Los dos datos que escribe Contabilidad en el fondo

| Campo en Rindegastos | Qué debe llevar | Ejemplo |
|---|---|---|
| `Descripción` | El código de la cuenta contable del ERP | `1020152` (Fondo Fijo Cusco MN) |
| `Código` | El documento de identidad de quien recibe el fondo | `76775158` |

Un fondo cuya `Descripción` no sea un código de cuenta se **omite** (queda solo en el
log, no en la tabla): así son los fondos viejos y de prueba, que traen texto libre o
nada. Si trae una cuenta pero está mal, o la persona no tiene análisis contable, sí se
guarda y queda en error, a la vista en la consulta **M1**.

### El comprobante

Verificado contra el 2606159, que Contabilidad ingresó a mano el 11/09/2026 por el fondo
926276 "CAJA CHICA CUSCO":

```
tipo 19, folio 3, glosa "TR-3 KORY CANCINO - CAJA CHICA CUSCO"
  1020152  analisis=149671 (DNI 76775158)  td=26  num=11092026  cargo 1000.00
  1041060  analisis=765 (BCP)              td=55  num=TR-3      abono 1000.00
```

- **Glosa:** `TR-` + folio + nombre corto de `mae_empleado` + ` - ` + título del fondo.
- **Línea del fondo:** la cuenta de `Descripción`, a nombre de quien recibe el fondo, con
  tipo de documento **26 OTROS** (código SUNAT 99) y número `ddMMyyyy` del depósito.
- **Línea del banco:** `1041060` Banco de Crédito MN Caja Chica, con tipo de documento
  **55 TRANSFERENCIA DE FONDOS** (código SUNAT 003) y número `TR-` + folio. Su análisis
  no se elige: lo trae la cuenta enlazada, igual que en la pantalla
  (`usp_enlazaCuentaconAnalisis` devuelve el análisis 765, Banco de Crédito del Perú).
- **Fecha del comprobante y vencimiento:** la fecha del depósito.
- El comprobante queda en **estado 17**, con la misma auditoría del flujo de informes.

> ⚠️ **Pendiente de confirmar:** para un fondo en dólares se usaría `1041055`
> (Banco de Crédito ME - Caja Chica). No hay ningún fondo en dólares con qué compararlo.

**El folio se calcula antes de armar las líneas**, dentro de la misma transacción, porque
la glosa y el número del banco lo llevan (`TR-3`). Contabilidad, a mano, graba primero la
cabecera y después la corrige para poner el folio; el worker no necesita ese segundo paso.

**No duplicar lo que se registró a mano:** antes de grabar se busca un cargo por el mismo
monto, en la misma cuenta, a nombre del mismo documento y con el mismo número. Así el
fondo 926276 no se vuelve a registrar, porque ya está en el 2606159.

**La marca en Rindegastos es del fondo completo**, no de cada depósito: solo se marca
cuando todos sus depósitos tienen comprobante.

### Configuración y tabla

```jsonc
"Integracion": {
  "ProcesarFondos": true   // apaga este flujo sin tocar los otros
}
```

`sql\07_tabla_rg_fondo.sql` crea `rg_fondo`, con los mismos cinco estados y la llave
(fondo, número de depósito). También necesita **`sqlcmd -I`** por el índice filtrado.

---

# Cuarto flujo: solicitudes de fondo

Alguien pide dinero **por adelantado** (viáticos o entrega a rendir) y, cuando la
solicitud termina de aprobarse, se le transfiere. Es la misma transferencia del flujo
anterior (**tipo 19**), pero cargada a **entregas a rendir**, porque la solicitud no
trae una cuenta contable propia.

Verificado contra el 2606190, que Contabilidad ingresó a mano por la solicitud
"VIATICOS PIURA" de 500.00:

```
tipo 19, folio 7, glosa "TR-7 LILIAN JOSE - VIATICOS PIURA"
  1413110  analisis=31909 (DNI 42791075)  td=26  num=16092026  cargo 500.00
  1041060  analisis=765 (BCP)             td=55  num=TR-7      abono 500.00
```

- **Las dos políticas se registran igual** (`Solicitudes de Fondos - Viáticos` y
  `Solicitudes de Fondos - Entrega por rendir`): ambas van a `1413110`, o a `1413010`
  si fueran en dólares.
- **Quién recibe el dinero:** el campo extra **`DNI`** de la solicitud. Las solicitudes
  viejas no lo traen (el campo se agregó después) y quedan en error, a la vista en la
  consulta **S1**.
- **Solo las aprobadas** (`Status = APPROVED`). Mientras no lo estén, no se guardan.
- **Fecha del comprobante:** la de aprobación (`ClosedDate`). La API manda las fechas en
  UTC, así que se pasan a hora de Perú antes de tomar el día; si no, una solicitud
  aprobada de noche quedaría con la fecha del día siguiente.

### Lo que evita registrar la plata dos veces

Al aprobar una solicitud, Rindegastos **crea además un fondo** con ese dinero. Ese fondo
trae `FundRequestId`, y por eso el flujo de fondos lo omite: la entrega la registra este
flujo, que es el que tiene el DNI y el tipo de rendición. El fondo sigue existiendo en
Rindegastos para recibir después la liquidación, que entra por el flujo de informes.

Además, antes de grabar se busca un cargo por el mismo monto, en la misma cuenta y a
nombre del mismo documento, que tenga **el mismo número o una glosa que termine igual**.
Lo segundo hace falta porque a mano el comprobante se graba con la fecha del día en que
se registra: el 2606190 quedó con número `16092026` y la solicitud se aprobó el 17.

### Configuración y tabla

```jsonc
"Integracion": {
  "ProcesarSolicitudesFondo": true   // apaga este flujo sin tocar los otros
}
```

`sql\08_tabla_rg_solicitud_fondo.sql` crea `rg_solicitud_fondo`. El Id de una solicitud
no es numérico: es un identificador de 24 caracteres. También necesita **`sqlcmd -I`**.

---

# Equivalencia con el ingreso manual

Como la integración reemplaza el ingreso manual, se compararon en la base de datos
los documentos que generó el worker contra los que Contabilidad ingresó a mano el
mismo día, uno de cada tipo:

| Tipo | Worker | Manual |
|---|---|---|
| Boleta | `0002-42118` → comprobante 2606116 | `0001-14051` → comprobante 2606115 |
| Recibo por Honorarios | `E001-36` → comprobante 2606117 | `E001-66` → comprobante 2606113 |
| Factura | `FE01-4520` → comprobante 2606118 | `F315-90062` → comprobante 2606114 |

**Resultado: coinciden en todo.** Se revisaron columna por columna:

- `mae_factura_boleta` — las 31 columnas iguales.
- `mae_comprobante_contable` — año, mes, tipo, folio correlativo, glosa con el mismo
  formato (`RAZÓN SOCIAL Nºfolio`) y estado 19.
- `mae_detalle_comprobante_contable` — mismo número de líneas y mismos valores en
  cuenta, análisis, centro de costo, tipo de documento, documento de origen, número
  de documento, fecha de vencimiento, cargo, abono, moneda y tipo de cambio.
- `tran_auditoria_comprobante_contable` y `tran_auditoria_detalle_comprobante_trigger`.
- Se verificó además que ninguna otra tabla del ERP quede escrita en el ingreso manual
  y vacía en el automático.

La **única** diferencia es el usuario: `1000000` (usuario de la integración) en vez del
código del contador. Eso es intencional: permite distinguir en cualquier reporte qué
documentos entraron solos y cuáles a mano.

Para repetir esta comprobación en cualquier momento, están las consultas **E4**
(auditoría completa) y **E5** (comparación lado a lado del mes en curso) en
`sql\03_consultas_monitoreo.sql`.

### Informes: comparación línea por línea (21/09/2026)

Se homologaron los dos informes de prueba y se comparó el asiento generado con el que
Contabilidad ingresó a mano el mismo día:

| Informe | Qué trae | Manual |
|---|---|---|
| 14020925 | 3 documentos + 1 planilla de movilidad | 2606225 |
| 14024447 | solo 3 documentos | 2606232 |

**Coinciden en cuenta, análisis, centro de costo, tipo de documento, número, montos y
glosa de cada línea.** Con dos observaciones:

- El **orden** de las líneas puede diferir: el worker sigue el orden en que la API
  devuelve los gastos, y a mano se teclean en cualquier orden. No afecta al asiento.
- En 2606232 la boleta `0003-3656263` quedó a mano con **tipo de documento 1 (factura)**,
  cuando en compras está registrada como **14 (boleta)**. El worker copia el tipo del
  documento ya registrado, así que pone 14. Es un error de tipeo del ingreso manual.

En ese mismo informe se ve además por qué el worker espera: el comprobante del informe
(2606232) se grabó **antes** que el de compras del documento (2606234). El worker no
puede hacerlo en ese orden, porque necesita el documento registrado para saber qué
cancelar; por eso el informe queda "en espera" hasta que compras lo registre.

### Diferencias que se encontraron y se corrigieron

Las tres surgieron de esta comparación:

1. **Faltaban las filas de auditoría.** El ERP graba una fila en
   `tran_auditoria_comprobante_contable` por comprobante y una en
   `tran_auditoria_detalle_comprobante_trigger` por cada línea. El worker no las
   escribía, así que sus comprobantes no aparecían en los reportes de auditoría
   contable. Ya las graba.
2. **El estado de la auditoría de cabecera era 19 y debe ser 17.** El comprobante queda
   en 19 ("Comprobante en Modificación"), pero la auditoría registra 17 ("Comprobante
   Abierto"), que es el estado al crear el encabezado. Confirmado en el código del ERP
   (`clsComprobanteContable.vb`, línea 995) y en 64 250 filas de 2026.
3. **`mae_factura_boleta_documentos_asociados` quedaba en NULL.** El ERP graba ahí la
   fecha del día, tipo 0 y textos vacíos. Se verificó que esto no afecta al Libro
   Electrónico de Compras —`usp_ctb_neo_libro_compra_le_81v2` no lee esos campos para
   facturas de servicio, solo para notas de crédito— pero se igualó de todos modos para
   que ningún registro se vea distinto.

Los comprobantes generados antes de estas correcciones se arreglaron con
`sql\05_backfill_auditoria.sql`.

---

# Pendiente

- **Informes de gastos.** Hoy se integra a nivel de gasto individual. La documentación
  de Rindegastos indica que además hay que llamar a `setExpenseReportIntegration` para
  el informe completo. Está previsto en `Integracion:MarcarInformeCompleto` pero no
  implementado.
- **Adjuntos.** `Files[]` trae las URLs de las boletas escaneadas. No se descargan.
- **Solicitudes de fondo y caja chica.** Fuera de este alcance.
- **Pantalla de monitoreo dentro del ERP.** Por ahora se usa
  `sql\03_consultas_monitoreo.sql`. Conviene construirla cuando Contabilidad deba
  resolver los errores sin depender de sistemas.
- **Alertas por correo** cuando haya gastos en estado 9, o en estado 2 por más de
  2 horas (consulta F1).
