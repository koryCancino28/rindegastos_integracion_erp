# Integración Rindegastos → ERP ICS

Programa que trae los **gastos** aprobados desde Rindegastos y los registra en el módulo
`Contabilidad > Comprobantes > Comprobantes Compra de Servicio sin Prorrateo`
(`wctrFacturaCompra2.ascx`), exactamente como si alguien los hubiera digitado a mano.

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

**No modifican ninguna tabla existente del ERP.** Solo crean tablas nuevas con prefijo `rg_`.
Y son idempotentes: si los corres dos veces, la segunda no hace nada.

Verificación:

```sql
SELECT name FROM sys.tables WHERE name LIKE 'rg_%';   -- debe devolver 3 filas
```

Para deshacer todo, si hiciera falta:

```sql
DROP TABLE dbo.rg_gasto; DROP TABLE dbo.rg_homologacion; DROP TABLE dbo.rg_log_api;
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
| Factura (01) | `total / 1.18` | `neto × 0.18` | 0 |
| Boleta (03) | 0 | 0 | `total` |
| Recibo por Honorarios (R1) | 0 | 0 | `total` |

La tasa se lee de `ref_parametro_general_calculo` (parámetro 1 = IGV = 18.00), no está
fija en el código.

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
