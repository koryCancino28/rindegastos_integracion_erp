/* =============================================================================
   INTEGRACION RINDEGASTOS -> ERP ICS
   Script 03: Consultas de monitoreo y diagnostico.

   Este archivo NO se ejecuta completo. Es un catalogo de consultas:
   selecciona el bloque que necesites y presiona F5.

   Todas son de solo lectura, EXCEPTO el bloque J, que esta marcado.

   ESTADOS de rg_gasto.rgg_estado
     0 = DESCARGADO     llego de Rindegastos, aun no se traduce
     1 = HOMOLOGADO     listo para contabilizar
     2 = CONTABILIZADO  ya existe el comprobante, falta avisar a Rindegastos
     3 = CONFIRMADO     terminado
     9 = ERROR          necesita que alguien lo revise
   ============================================================================= */


/* =============================================================================
   A. TABLERO: lo primero que se mira cada dia
   ============================================================================= */

-- A0. LA CONSULTA DEL DIA A DIA: todos los gastos, uno por fila, con su estado
--     en texto. Si solo vas a usar una consulta de este archivo, usa esta.
SELECT
    rgg_id AS id_rindegastos,
    CASE rgg_estado
        WHEN 0 THEN '0 - Descargado'
        WHEN 1 THEN '1 - Listo para contabilizar'
        WHEN 2 THEN '2 - Contabilizado, sin confirmar'
        WHEN 3 THEN '3 - Terminado'
        WHEN 9 THEN '9 - ERROR'
    END                      AS estado,
    rgg_supplier             AS proveedor,
    rgg_nro_documento        AS documento,
    rgg_issue_date           AS fecha_emision,
    rgg_total                AS total,
    rgg_cod_factura_boleta   AS factura_erp,
    rgg_cod_comprobante      AS comprobante_erp,
    rgg_ultimo_error         AS motivo_si_fallo
FROM dbo.rg_gasto
ORDER BY rgg_estado, rgg_fecha_descarga DESC;


-- A1. Cuantos gastos hay en cada estado.
--
--     OJO AL LEER EL RESULTADO: la primera columna es el CODIGO del estado,
--     no la cantidad. Si ves una fila "0 | 0 - Descargado | 8", significa
--     8 gastos en estado Descargado, no cero gastos.
SELECT
    rgg_estado AS estado,
    CASE rgg_estado
        WHEN 0 THEN '0 - Descargado'
        WHEN 1 THEN '1 - Listo para contabilizar'
        WHEN 2 THEN '2 - Contabilizado, sin confirmar en Rindegastos'
        WHEN 3 THEN '3 - Terminado'
        WHEN 9 THEN '9 - ERROR: requiere revision'
        ELSE 'desconocido'
    END AS descripcion,
    COUNT(*)          AS cantidad,
    SUM(rgg_total)    AS monto_total,
    MIN(rgg_fecha_descarga) AS mas_antiguo
FROM dbo.rg_gasto
GROUP BY rgg_estado
ORDER BY rgg_estado;


-- A2. Resumen del dia: que se contabilizo hoy
SELECT
    COUNT(*)                AS gastos_contabilizados,
    SUM(rgg_total)          AS monto_total,
    MIN(rgg_cod_comprobante) AS primer_comprobante,
    MAX(rgg_cod_comprobante) AS ultimo_comprobante
FROM dbo.rg_gasto
WHERE CAST(rgg_fecha_contabilizado AS DATE) = CAST(GETDATE() AS DATE);


/* =============================================================================
   B. ERRORES: que hay que arreglar
   ============================================================================= */

-- B1. Todos los gastos con problema, con el motivo exacto
SELECT
    rgg_id                AS id_rindegastos,
    CASE rgg_estado
        WHEN 0 THEN '0 - Descargado'
        WHEN 1 THEN '1 - Listo para contabilizar'
        WHEN 2 THEN '2 - Contabilizado, sin confirmar'
        WHEN 3 THEN '3 - Terminado'
        WHEN 9 THEN '9 - ERROR'
    END                   AS estado,
    rgg_supplier          AS proveedor_rindegastos,
    rgg_ruc_proveedor     AS ruc,
    rgg_nro_documento     AS documento,
    rgg_issue_date        AS fecha_emision,
    rgg_total             AS total,
    rgg_category_code     AS cuenta_categoria,
    rgg_centro_costo_code AS centro_costo,
    rgg_intentos          AS intentos,
    rgg_ultimo_error      AS motivo
FROM dbo.rg_gasto
WHERE rgg_estado = 9 OR rgg_ultimo_error IS NOT NULL
ORDER BY rgg_fecha_descarga DESC;


-- B2. Agrupa los errores por tipo, para atacar el mas frecuente primero
SELECT
    CASE
        WHEN rgg_ultimo_error LIKE '%no existe en mae_proveedor%'    THEN '1. Falta crear el proveedor'
        WHEN rgg_ultimo_error LIKE '%no tiene analisis contable%'    THEN '2. Falta el analisis del proveedor'
        WHEN rgg_ultimo_error LIKE '%no trae CategoryCode%'          THEN '3. El gasto no tiene categoria en Rindegastos'
        WHEN rgg_ultimo_error LIKE '%no existe en mae_plan_cuenta%'  THEN '4. La cuenta contable no existe'
        WHEN rgg_ultimo_error LIKE '%requiere centro de costo%'      THEN '5. Falta centro de costo en Rindegastos'
        WHEN rgg_ultimo_error LIKE '%no existe en ref_centro_costo%' THEN '6. El centro de costo no existe en el ERP'
        WHEN rgg_ultimo_error LIKE '%no tiene equivalencia%'         THEN '7. Falta homologar el tipo de documento'
        WHEN rgg_ultimo_error LIKE '%ya esta registrado en el ERP%'  THEN '8. Documento duplicado'
        WHEN rgg_ultimo_error LIKE '%periodo esta cerrado%'          THEN '9. Periodo contable cerrado'
        WHEN rgg_ultimo_error LIKE '%tipo de cambio%'                THEN '10. Falta tipo de cambio'
        WHEN rgg_ultimo_error LIKE '%descuadrado%'                   THEN '11. El comprobante no cuadra'
        ELSE '99. Otro'
    END                AS tipo_de_problema,
    COUNT(*)           AS cantidad,
    SUM(rgg_total)     AS monto_afectado
FROM dbo.rg_gasto
WHERE rgg_ultimo_error IS NOT NULL
GROUP BY
    CASE
        WHEN rgg_ultimo_error LIKE '%no existe en mae_proveedor%'    THEN '1. Falta crear el proveedor'
        WHEN rgg_ultimo_error LIKE '%no tiene analisis contable%'    THEN '2. Falta el analisis del proveedor'
        WHEN rgg_ultimo_error LIKE '%no trae CategoryCode%'          THEN '3. El gasto no tiene categoria en Rindegastos'
        WHEN rgg_ultimo_error LIKE '%no existe en mae_plan_cuenta%'  THEN '4. La cuenta contable no existe'
        WHEN rgg_ultimo_error LIKE '%requiere centro de costo%'      THEN '5. Falta centro de costo en Rindegastos'
        WHEN rgg_ultimo_error LIKE '%no existe en ref_centro_costo%' THEN '6. El centro de costo no existe en el ERP'
        WHEN rgg_ultimo_error LIKE '%no tiene equivalencia%'         THEN '7. Falta homologar el tipo de documento'
        WHEN rgg_ultimo_error LIKE '%ya esta registrado en el ERP%'  THEN '8. Documento duplicado'
        WHEN rgg_ultimo_error LIKE '%periodo esta cerrado%'          THEN '9. Periodo contable cerrado'
        WHEN rgg_ultimo_error LIKE '%tipo de cambio%'                THEN '10. Falta tipo de cambio'
        WHEN rgg_ultimo_error LIKE '%descuadrado%'                   THEN '11. El comprobante no cuadra'
        ELSE '99. Otro'
    END
ORDER BY cantidad DESC;


/* =============================================================================
   C. DATOS MAESTROS QUE FALTAN EN EL ERP
      Estas 3 consultas son la lista de tareas para Contabilidad.
   ============================================================================= */

-- C1. Proveedores que hay que dar de alta.
--     Se agrupa SOLO por RUC: el rendidor escribe el nombre libremente en
--     Rindegastos, asi que el mismo RUC llega con textos distintos.
--     La razon social de SUNAT es la buena para dar de alta el proveedor.
SELECT
    g.rgg_ruc_proveedor           AS ruc_a_crear,
    MAX(g.rgg_sunat_razon_social) AS razon_social_sunat,
    MAX(g.rgg_supplier)           AS ejemplo_nombre_rindegastos,
    COUNT(*)                      AS gastos_detenidos,
    SUM(g.rgg_total)              AS monto_detenido
FROM dbo.rg_gasto g
LEFT JOIN dbo.mae_proveedor p
       ON LTRIM(RTRIM(p.mpr_id)) = LTRIM(RTRIM(g.rgg_ruc_proveedor))
WHERE p.mpr_cod_proveedor IS NULL
  AND g.rgg_ruc_proveedor IS NOT NULL
GROUP BY g.rgg_ruc_proveedor
ORDER BY monto_detenido DESC;


-- C2. Proveedores que existen pero NO tienen analisis contable.
--     El analisis se une por el RUC limpiando separadores, igual que el ERP.
SELECT DISTINCT
    p.mpr_cod_proveedor,
    p.mpr_id  AS ruc,
    p.mpr_nombre,
    COUNT(*)  AS gastos_detenidos
FROM dbo.rg_gasto g
INNER JOIN dbo.mae_proveedor p
        ON LTRIM(RTRIM(p.mpr_id)) = LTRIM(RTRIM(g.rgg_ruc_proveedor))
LEFT JOIN dbo.tran_analisis ta
       ON ta.tan_cod_interno_analisis = CAST(
            REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                p.mpr_id,'/',''),'.',''),'-',''),'k','0'),'x','0'),'t',''),'f','') AS BIGINT)
WHERE ta.tan_cod_analisis IS NULL
GROUP BY p.mpr_cod_proveedor, p.mpr_id, p.mpr_nombre;


-- C3. Categorias de Rindegastos cuya cuenta contable no existe en el ERP
SELECT
    g.rgg_category_code AS cuenta_que_falta,
    g.rgg_category      AS nombre_categoria_rindegastos,
    COUNT(*)            AS gastos_detenidos,
    SUM(g.rgg_total)    AS monto_detenido
FROM dbo.rg_gasto g
LEFT JOIN dbo.mae_plan_cuenta pc
       ON LTRIM(RTRIM(pc.mpc_codigo_cuenta)) = LTRIM(RTRIM(g.rgg_category_code))
WHERE pc.mpc_cod_plan_cuenta IS NULL
  AND g.rgg_category_code IS NOT NULL
GROUP BY g.rgg_category_code, g.rgg_category
ORDER BY gastos_detenidos DESC;


-- C4. Gastos que llegaron SIN categoria (hay que corregirlos en Rindegastos)
SELECT rgg_id, rgg_supplier, rgg_nro_documento, rgg_issue_date, rgg_total, rgg_note
FROM dbo.rg_gasto
WHERE rgg_category_code IS NULL OR LTRIM(RTRIM(rgg_category_code)) = ''
ORDER BY rgg_issue_date DESC;


-- C5. Gastos cuya cuenta EXIGE centro de costo pero llegaron sin el
SELECT
    g.rgg_id, g.rgg_supplier, g.rgg_nro_documento, g.rgg_total,
    g.rgg_category_code AS cuenta,
    pc.mpc_nombre       AS nombre_cuenta
FROM dbo.rg_gasto g
INNER JOIN dbo.mae_plan_cuenta pc
        ON LTRIM(RTRIM(pc.mpc_codigo_cuenta)) = LTRIM(RTRIM(g.rgg_category_code))
WHERE pc.mpc_requiere_centro_costo = 1
  AND (g.rgg_centro_costo_code IS NULL OR LTRIM(RTRIM(g.rgg_centro_costo_code)) = '');


-- C6. Tipos de documento de Rindegastos sin equivalencia configurada
SELECT DISTINCT
    g.rgg_tipo_doc_code   AS codigo_rindegastos,
    g.rgg_tipo_doc_nombre AS nombre_rindegastos,
    COUNT(*)              AS gastos_detenidos
FROM dbo.rg_gasto g
LEFT JOIN dbo.rg_homologacion h
       ON h.rgh_tipo = 'TIPO_DOCUMENTO' AND h.rgh_valor_rg = g.rgg_tipo_doc_code AND h.rgh_vigente = 1
LEFT JOIN dbo.ref_tipo_documento_contable td
       ON LTRIM(RTRIM(td.rtdc_cod_sunat)) = LTRIM(RTRIM(g.rgg_tipo_doc_code)) AND td.rtdc_vigente = 'S'
WHERE h.rgh_id IS NULL AND td.rtdc_cod_tipo_documento_contable IS NULL
  AND g.rgg_tipo_doc_code IS NOT NULL
GROUP BY g.rgg_tipo_doc_code, g.rgg_tipo_doc_nombre;


/* =============================================================================
   D. DUPLICADOS
   ============================================================================= */

-- D1. El mismo documento cargado varias veces EN RINDEGASTOS.
--     Cada copia tiene un Id distinto, asi que el candado por Id no las detecta.
--     Solo la primera se contabiliza; las demas se rechazan por folio repetido.
--     Se agrupa por RUC + documento, sin incluir el nombre del proveedor:
--     el mismo comprobante puede llegar con el nombre escrito distinto.
SELECT
    rgg_ruc_proveedor   AS ruc,
    rgg_nro_documento   AS documento,
    MAX(rgg_supplier)   AS proveedor,
    MAX(rgg_total)      AS monto,
    COUNT(*)            AS veces_cargado,
    STRING_AGG(CAST(rgg_id AS VARCHAR(20)), ', ') AS ids_en_rindegastos
FROM dbo.rg_gasto
WHERE rgg_nro_documento IS NOT NULL
GROUP BY rgg_ruc_proveedor, rgg_nro_documento
HAVING COUNT(*) > 1
ORDER BY veces_cargado DESC;


-- D2. Gastos cuyo documento YA existia en el ERP antes de la integracion
--     (por ejemplo, alguien lo digito a mano)
SELECT
    g.rgg_id, g.rgg_supplier, g.rgg_nro_documento, g.rgg_total,
    fb.mfb_cod_factura_boleta AS factura_ya_existente,
    fb.mfb_fecha_creacion     AS fecha_en_que_se_digito,
    fb.mfb_cod_usuario        AS usuario_que_la_digito
FROM dbo.rg_gasto g
INNER JOIN dbo.mae_proveedor p
        ON LTRIM(RTRIM(p.mpr_id)) = LTRIM(RTRIM(g.rgg_ruc_proveedor))
INNER JOIN dbo.mae_factura_boleta fb
        ON LTRIM(RTRIM(fb.mfb_folio)) = LTRIM(RTRIM(g.rgg_nro_documento))
       AND fb.mfb_cod_proveedor = p.mpr_cod_proveedor
WHERE g.rgg_cod_factura_boleta IS NULL   -- todavia no lo genero la integracion
ORDER BY g.rgg_id;


/* =============================================================================
   E. TRAZABILIDAD: del gasto de Rindegastos al asiento contable
   ============================================================================= */

-- E1. Que se genero por cada gasto integrado
SELECT
    g.rgg_id              AS id_rindegastos,
    CASE g.rgg_estado
        WHEN 2 THEN '2 - Contabilizado, sin confirmar en Rindegastos'
        WHEN 3 THEN '3 - Terminado'
    END                   AS estado,
    g.rgg_supplier        AS proveedor,
    g.rgg_nro_documento   AS documento,
    g.rgg_total           AS total,
    g.rgg_cod_factura_boleta AS factura_erp,
    g.rgg_cod_comprobante    AS comprobante_erp,
    c.mcm_anno, c.mcm_mes, c.mcm_folio,
    c.mcm_glosa,
    g.rgg_fecha_contabilizado,
    g.rgg_fecha_confirmado
FROM dbo.rg_gasto g
LEFT JOIN dbo.mae_comprobante_contable c
       ON c.mcm_cod_comprobante_contable = g.rgg_cod_comprobante
WHERE g.rgg_estado >= 2
ORDER BY g.rgg_fecha_contabilizado DESC;


-- E2. Detalle contable completo de UN gasto.
--     Cambia el Id por el que quieras revisar.
DECLARE @id_gasto BIGINT = 72622902;

SELECT
    d.mdco_cod_detalle_comprobante_contable AS linea,
    pc.mpc_codigo_cuenta AS cuenta,
    pc.mpc_nombre        AS nombre_cuenta,
    ta.tan_cod_interno_analisis AS analisis,
    cc.rcc_nombre_centro_costo  AS centro_costo,
    d.mdco_cod_tipo_documento_contable AS tipo_doc,
    d.mdco_numero_documento AS numero,
    d.mdco_cargo, d.mdco_abono,
    d.mdco_cargo_soles, d.mdco_abono_soles,
    d.mdco_glosa
FROM dbo.rg_gasto g
INNER JOIN dbo.mae_detalle_comprobante_contable d
        ON d.mdco_cod_comprobante_contable = g.rgg_cod_comprobante
LEFT JOIN dbo.mae_plan_cuenta   pc ON pc.mpc_cod_plan_cuenta      = d.mdco_cod_plan_cuenta
LEFT JOIN dbo.tran_analisis     ta ON ta.tan_cod_analisis         = d.mdco_cod_analisis
LEFT JOIN dbo.ref_centro_costo  cc ON cc.rcc_cod_centro_costo     = d.mdco_cod_centro_costo
WHERE g.rgg_id = @id_gasto
ORDER BY d.mdco_cod_detalle_comprobante_contable;


-- E3. Comprobantes generados por la integracion que NO cuadran.
--     Deberia devolver 0 filas siempre. Si devuelve algo, es grave.
SELECT
    g.rgg_id, g.rgg_cod_comprobante,
    SUM(d.mdco_cargo_soles) AS total_cargo,
    SUM(d.mdco_abono_soles) AS total_abono,
    SUM(d.mdco_cargo_soles) - SUM(d.mdco_abono_soles) AS diferencia
FROM dbo.rg_gasto g
INNER JOIN dbo.mae_detalle_comprobante_contable d
        ON d.mdco_cod_comprobante_contable = g.rgg_cod_comprobante
WHERE g.rgg_cod_comprobante IS NOT NULL
GROUP BY g.rgg_id, g.rgg_cod_comprobante
HAVING ABS(SUM(d.mdco_cargo_soles) - SUM(d.mdco_abono_soles)) > 0.05;


-- E4. AUDITORIA COMPLETA: cada comprobante de la integracion debe tener
--     1 fila de auditoria de cabecera y 1 por cada linea del detalle,
--     igual que uno ingresado a mano desde la pantalla.
--     La columna "revisar" debe decir OK en todas las filas.
SELECT
    g.rgg_id                          AS id_rindegastos,
    g.rgg_nro_documento               AS folio,
    g.rgg_cod_comprobante             AS comprobante,
    lineas.total                      AS lineas_detalle,
    aud.cabecera                      AS auditoria_cabecera,
    aud.detalle                       AS auditoria_detalle,
    CASE WHEN aud.cabecera = 1 AND aud.detalle = lineas.total
         THEN 'OK' ELSE 'FALTA AUDITORIA' END AS revisar
FROM dbo.rg_gasto g
CROSS APPLY (
    SELECT COUNT(*) AS total
    FROM dbo.mae_detalle_comprobante_contable d
    WHERE d.mdco_cod_comprobante_contable = g.rgg_cod_comprobante
) lineas
CROSS APPLY (
    SELECT
        (SELECT COUNT(*) FROM dbo.tran_auditoria_comprobante_contable a
         WHERE a.tac_nro_cod_comprobante = g.rgg_cod_comprobante)          AS cabecera,
        (SELECT COUNT(*) FROM dbo.tran_auditoria_detalle_comprobante_trigger t
         WHERE t.tacc_cod_comprobante_contable = g.rgg_cod_comprobante)    AS detalle
) aud
WHERE g.rgg_cod_comprobante IS NOT NULL
ORDER BY g.rgg_cod_comprobante;


-- E5. COMPARACION CON LOS REGISTROS MANUALES.
--     Pone lado a lado los documentos de la integracion y los que Contabilidad
--     ingreso a mano el mismo mes, para confirmar que se ven iguales.
--     Se espera que las dos filas tengan la misma estructura; la unica
--     diferencia legitima es el usuario (el de la integracion vs el contador).
DECLARE @anno_comp smallint = YEAR(GETDATE());
DECLARE @mes_comp  tinyint  = MONTH(GETDATE());

SELECT
    CASE WHEN g.rgg_id IS NULL THEN 'MANUAL' ELSE 'INTEGRACION' END AS origen,
    c.mcm_cod_comprobante_contable    AS comprobante,
    c.mcm_cod_tipo_comprobante        AS tipo_comprobante,
    f.mfb_folio                       AS folio,
    f.mfb_cod_tipo_factura_boleta     AS tipo_documento,
    f.mfb_cod_moneda                  AS moneda,
    f.mfb_monto_total                 AS total,
    f.mfb_cod_estado_global           AS estado_factura,
    c.mcm_cod_estado_global_actual    AS estado_comprobante,
    f.mfb_cod_usuario                 AS usuario,
    (SELECT COUNT(*) FROM dbo.mae_detalle_comprobante_contable d
     WHERE d.mdco_cod_comprobante_contable = c.mcm_cod_comprobante_contable)  AS lineas,
    (SELECT COUNT(*) FROM dbo.tran_auditoria_comprobante_contable a
     WHERE a.tac_nro_cod_comprobante = c.mcm_cod_comprobante_contable)        AS aud_cabecera,
    (SELECT COUNT(*) FROM dbo.tran_auditoria_detalle_comprobante_trigger t
     WHERE t.tacc_cod_comprobante_contable = c.mcm_cod_comprobante_contable)  AS aud_detalle
FROM dbo.mae_factura_boleta f
INNER JOIN dbo.mae_comprobante_contable c
        ON c.mcm_cod_comprobante_contable = f.mfb_cod_referencia
LEFT JOIN dbo.rg_gasto g
        ON g.rgg_cod_factura_boleta = f.mfb_cod_factura_boleta
WHERE c.mcm_anno = @anno_comp
  AND c.mcm_mes  = @mes_comp
  AND f.mfb_cod_tipo_factura_boleta IN (1, 14, 15)   -- Factura, Boleta, Recibo Honorarios
ORDER BY origen, c.mcm_cod_comprobante_contable;


/* =============================================================================
   F. SALUD DEL PROCESO: alertas
   ============================================================================= */

-- F1. ALERTA CRITICA: contabilizado hace mas de 2 horas y aun sin confirmar
--     en Rindegastos. Significa que la API no responde o el token vencio.
SELECT
    rgg_id, rgg_supplier, rgg_cod_comprobante,
    rgg_fecha_contabilizado,
    DATEDIFF(MINUTE, rgg_fecha_contabilizado, GETDATE()) AS minutos_esperando
FROM dbo.rg_gasto
WHERE rgg_estado = 2
  AND rgg_fecha_contabilizado < DATEADD(HOUR, -2, GETDATE())
ORDER BY rgg_fecha_contabilizado;


-- F2. Cuando corrio el worker por ultima vez
SELECT TOP 20
    rgl_fecha, rgl_metodo, rgl_http_status, rgl_ms, rgl_ok
FROM dbo.rg_log_api
ORDER BY rgl_fecha DESC;


-- F3. Llamadas fallidas a la API en los ultimos 7 dias
SELECT
    CAST(rgl_fecha AS DATE) AS dia,
    rgl_metodo,
    rgl_http_status,
    COUNT(*) AS fallos
FROM dbo.rg_log_api
WHERE rgl_ok = 0 AND rgl_fecha >= DATEADD(DAY, -7, GETDATE())
GROUP BY CAST(rgl_fecha AS DATE), rgl_metodo, rgl_http_status
ORDER BY dia DESC, fallos DESC;


-- F4. Ver la respuesta completa de una llamada que fallo
SELECT TOP 5 rgl_fecha, rgl_metodo, rgl_http_status, rgl_request, rgl_response
FROM dbo.rg_log_api
WHERE rgl_ok = 0
ORDER BY rgl_fecha DESC;


-- F5. Tiempo de respuesta promedio de la API (para detectar lentitud)
SELECT
    rgl_metodo,
    COUNT(*)      AS llamadas,
    AVG(rgl_ms)   AS ms_promedio,
    MAX(rgl_ms)   AS ms_peor_caso
FROM dbo.rg_log_api
WHERE rgl_fecha >= DATEADD(DAY, -7, GETDATE())
GROUP BY rgl_metodo
ORDER BY ms_promedio DESC;


-- F6. El periodo contable esta cerrado?
--     Si la fecha de cierre es HOY o posterior, el worker no podra contabilizar.
SELECT TOP 1
    tcm_fecha AS cierre_contable_vigente,
    CASE WHEN CAST(GETDATE() AS DATE) <= CAST(tcm_fecha AS DATE)
         THEN 'BLOQUEADO: no se puede contabilizar'
         ELSE 'OK: se puede contabilizar'
    END AS situacion
FROM dbo.tran_cierre_mensual
WHERE tcm_cod_tipo_cierre = 1
ORDER BY tcm_fecha DESC;


/* =============================================================================
   G. HOMOLOGACIONES: ver y mantener
   ============================================================================= */

-- G1. Equivalencias configuradas hoy
SELECT rgh_id, rgh_tipo, rgh_valor_rg, rgh_valor_erp, rgh_glosa, rgh_vigente
FROM dbo.rg_homologacion
ORDER BY rgh_tipo, rgh_valor_rg;


/* =============================================================================
   H. LIMPIEZA DEL LOG
   ============================================================================= */

-- H1. Cuanto ocupa el log
SELECT COUNT(*) AS registros, MIN(rgl_fecha) AS mas_antiguo, MAX(rgl_fecha) AS mas_reciente
FROM dbo.rg_log_api;


/* #############################################################################
   J. ACCIONES QUE MODIFICAN DATOS
      Todas estan comentadas a proposito. Descomenta solo la que necesites.
   ############################################################################# */

/* J1. REINTENTAR un gasto despues de corregir el dato maestro.
       Lo devuelve al estado inicial para que el proximo ciclo lo reprocese.

UPDATE dbo.rg_gasto
SET rgg_estado = 0, rgg_intentos = 0, rgg_ultimo_error = NULL
WHERE rgg_id = 72622902;
*/


/* J2. REINTENTAR TODOS los que fallaron por proveedor inexistente,
       despues de haber creado los proveedores.

UPDATE dbo.rg_gasto
SET rgg_estado = 0, rgg_intentos = 0, rgg_ultimo_error = NULL
WHERE rgg_estado = 9 AND rgg_ultimo_error LIKE '%no existe en mae_proveedor%';
*/


/* J3. DESCARTAR un gasto que no se debe contabilizar (por ejemplo, un duplicado).
       Lo deja en estado 3 sin haber generado comprobante, para que no vuelva
       a aparecer como pendiente.

UPDATE dbo.rg_gasto
SET rgg_estado = 3, rgg_ultimo_error = 'Descartado manualmente: duplicado en Rindegastos'
WHERE rgg_id = 74954628;
*/


/* J4. LIMPIAR el log de mas de 90 dias.

DELETE FROM dbo.rg_log_api WHERE rgl_fecha < DATEADD(DAY, -90, GETDATE());
*/


/* J5. BORRAR TODO EL STAGING Y VOLVER A EMPEZAR.
       Solo durante las pruebas, y solo si NADA se contabilizo todavia.
       Verifica primero que no haya nada en estado 2 o 3:
           SELECT COUNT(*) FROM dbo.rg_gasto WHERE rgg_estado >= 2;

DELETE FROM dbo.rg_gasto WHERE rgg_estado < 2;
*/


/* #############################################################################
   K. REVERSAR UN COMPROBANTE MAL GENERADO
      Ultimo recurso. Hacer respaldo antes.
      El ERP tiene su propio boton de eliminar en la pantalla: usarlo es
      preferible a borrar a mano.
   ############################################################################# */

/* K1. Ver que se va a borrar antes de borrarlo.
       Cambia el Id por el gasto que quieras revertir.

DECLARE @id BIGINT = 0;
SELECT rgg_cod_factura_boleta, rgg_cod_comprobante FROM dbo.rg_gasto WHERE rgg_id = @id;
SELECT * FROM dbo.mae_detalle_comprobante_contable
WHERE mdco_cod_comprobante_contable = (SELECT rgg_cod_comprobante FROM dbo.rg_gasto WHERE rgg_id = @id);
*/
