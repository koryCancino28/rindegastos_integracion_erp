/* ============================================================================
   05_backfill_auditoria.sql
   ----------------------------------------------------------------------------
   Rellena la auditoria contable de los comprobantes que el worker creo ANTES
   de que se implementara la escritura de auditoria.

   POR QUE HACE FALTA
   ------------------
   Al comparar los documentos del worker contra los que Hiroshi ingreso a mano
   se encontro que eran identicos en todo (cabecera de la factura, cabecera del
   comprobante y cada linea del detalle), con una sola excepcion: al worker le
   faltaban las filas de auditoria que el ERP graba desde la pantalla.

     tran_auditoria_comprobante_contable           1 fila por comprobante
     tran_auditoria_detalle_comprobante_trigger    1 fila por linea del detalle

   El worker ya las graba. Este script solo arregla los comprobantes viejos.

   QUE NO HACE
   -----------
   No toca ninguna cifra contable. No crea ni modifica comprobantes, facturas
   ni lineas de detalle: solo agrega las filas de auditoria que faltan, con los
   mismos valores que habria grabado la pantalla.

   COMO SE EJECUTA
   ---------------
   Se puede correr varias veces sin problema: solo procesa los comprobantes que
   todavia no tienen auditoria. Al final muestra un cuadro de verificacion.
============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;

/* Textos y estado exactos que usa el ERP. Verificados con DATALENGTH contra las
   filas reales: 35 bytes la descripcion de cabecera, 33 bytes la del detalle.
   El estado 17 es "Comprobante Abierto": es lo que la auditoria registra al
   crear el encabezado, aunque el comprobante quede en 19. */
DECLARE @DescCabecera varchar(500) = 'Ingreso Encabezado Por Contabilidad';
DECLARE @DescDetalle  varchar(250) = 'Ingresado en Detalle Por Compras.';
DECLARE @EstadoAuditoria int = 17;

/* ---------------------------------------------------------------------------
   1. Comprobantes creados por el worker que no tienen auditoria de cabecera
--------------------------------------------------------------------------- */
DECLARE @pendientes TABLE (comp int PRIMARY KEY, usuario int);

INSERT INTO @pendientes (comp, usuario)
SELECT DISTINCT g.rgg_cod_comprobante, f.mfb_cod_usuario
FROM rg_gasto g
JOIN mae_factura_boleta f ON f.mfb_cod_factura_boleta = g.rgg_cod_factura_boleta
WHERE g.rgg_cod_comprobante IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM tran_auditoria_comprobante_contable a
                  WHERE a.tac_nro_cod_comprobante = g.rgg_cod_comprobante);

DECLARE @filasCab int = (SELECT COUNT(*) FROM @pendientes);
PRINT 'Comprobantes sin auditoria de cabecera: ' + CAST(@filasCab AS varchar(10));

/* ---------------------------------------------------------------------------
   2. Auditoria de cabecera
--------------------------------------------------------------------------- */
DECLARE @comp int, @usuario int;
DECLARE cur_cab CURSOR LOCAL FAST_FORWARD FOR SELECT comp, usuario FROM @pendientes ORDER BY comp;

OPEN cur_cab;
FETCH NEXT FROM cur_cab INTO @comp, @usuario;

WHILE @@FETCH_STATUS = 0
BEGIN
    DECLARE @fechaProceso datetime, @anno smallint, @mes tinyint,
            @tipo smallint, @folio int, @glosa varchar(100);

    SELECT @fechaProceso = mcm_fecha_proceso,
           @anno         = mcm_anno,
           @mes          = mcm_mes,
           @tipo         = mcm_cod_tipo_comprobante,
           @folio        = mcm_folio,
           @glosa        = LEFT(mcm_glosa, 100)
    FROM mae_comprobante_contable
    WHERE mcm_cod_comprobante_contable = @comp;

    EXEC sp_insert_tran_Auditoria_Comprobante_Contable_1
         @tac_nro_cod_comprobante_1      = @comp,
         @tac_fecha_proceso_2            = @fechaProceso,
         @tac_anno_3                     = @anno,
         @tac_mes_4                      = @mes,
         @tac_cod_tipo_Comprobante_5     = @tipo,
         @tac_folio_comprobante_6        = @folio,
         @tac_glosa_7                    = @glosa,
         @tac_cod_estado_global_actual_8 = @EstadoAuditoria,
         @tac_descripcion_9              = @DescCabecera,
         @tac_procedimiento_10           = NULL,
         @tac_cod_usuario_11             = @usuario,
         @tac_hora_12                    = @fechaProceso,
         @tac_string_sql_13              = NULL,
         @tac_source_14                  = NULL;

    FETCH NEXT FROM cur_cab INTO @comp, @usuario;
END

CLOSE cur_cab;
DEALLOCATE cur_cab;

/* ---------------------------------------------------------------------------
   3. Auditoria de detalle: una fila por cada linea que no la tenga
--------------------------------------------------------------------------- */
DECLARE @det bigint, @pc int, @an bigint, @cc smallint, @item smallint,
        @td int, @orig int, @numdoc varchar(20), @fvenc datetime,
        @cargo numeric(18,2), @abono numeric(18,2), @mon int, @tc numeric(18,4),
        @gld varchar(100), @cargoS numeric(18,2), @abonoS numeric(18,2),
        @fechaDet datetime, @usuarioTxt varchar(50);

DECLARE cur_det CURSOR LOCAL FAST_FORWARD FOR
SELECT d.mdco_cod_detalle_comprobante_contable,
       d.mdco_cod_comprobante_contable,
       d.mdco_cod_plan_cuenta,
       d.mdco_cod_analisis,
       d.mdco_cod_centro_costo,
       d.mdco_cod_item_gasto,
       d.mdco_cod_tipo_documento_contable,
       d.mdco_cod_documento_origen,
       LEFT(ISNULL(d.mdco_numero_documento, ''), 20),
       d.mdco_fecha_vencimiento,
       d.mdco_cargo,
       d.mdco_abono,
       d.mdco_cod_moneda,
       d.mdco_monto_tipo_cambio,
       LEFT(ISNULL(d.mdco_glosa, ''), 100),
       d.mdco_cargo_soles,
       d.mdco_abono_soles,
       c.mcm_fecha_proceso,
       CAST(f.mfb_cod_usuario AS varchar(50))
FROM rg_gasto g
JOIN mae_factura_boleta f ON f.mfb_cod_factura_boleta = g.rgg_cod_factura_boleta
JOIN mae_comprobante_contable c ON c.mcm_cod_comprobante_contable = g.rgg_cod_comprobante
JOIN mae_detalle_comprobante_contable d ON d.mdco_cod_comprobante_contable = c.mcm_cod_comprobante_contable
WHERE g.rgg_cod_comprobante IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM tran_auditoria_detalle_comprobante_trigger t
                  WHERE t.tacc_cod_detalle_comprobante_contable = d.mdco_cod_detalle_comprobante_contable)
ORDER BY d.mdco_cod_comprobante_contable, d.mdco_cod_detalle_comprobante_contable;

OPEN cur_det;
FETCH NEXT FROM cur_det INTO @det, @comp, @pc, @an, @cc, @item, @td, @orig, @numdoc,
                             @fvenc, @cargo, @abono, @mon, @tc, @gld, @cargoS, @abonoS,
                             @fechaDet, @usuarioTxt;

DECLARE @filasDet int = 0;

WHILE @@FETCH_STATUS = 0
BEGIN
    EXEC sp_insert_tran_auditoria_detalle_comprobante_trigger_1
         @tacc_cod_detalle_comprobante_contable_1 = @det,
         @tacc_cod_comprobante_contable_2         = @comp,
         @tacc_cod_plan_cuenta_3                  = @pc,
         @tacc_cod_analisis_4                     = @an,
         @tacc_centro_costo_5                     = @cc,
         @tacc_cod_item_gasto_6                   = @item,
         @tacc_cod_tipo_documento_contable_7      = @td,
         @tacc_cod_documento_origen_8             = @orig,
         @tacc_numero_documento_9                 = @numdoc,
         @tacc_fecha_vencimiento_10               = @fvenc,
         @tacc_cargo_11                           = @cargo,
         @tacc_abono_12                           = @abono,
         @tacc_cod_moneda_13                      = @mon,
         @tacc_monto_tipo_cambio_14               = @tc,
         @tacc_glosa_15                           = @gld,
         @tacc_conciliacion_16                    = 0,
         @tacc_cargo_soles_17                     = @cargoS,
         @tacc_abono_soles_18                     = @abonoS,
         @tacc_fecha_19                           = @fechaDet,
         @tacc_usuario_20                         = @usuarioTxt,
         @tacc_llamada_21                         = NULL,
         @tacc_obs_22                             = @DescDetalle;

    SET @filasDet = @filasDet + 1;

    FETCH NEXT FROM cur_det INTO @det, @comp, @pc, @an, @cc, @item, @td, @orig, @numdoc,
                                 @fvenc, @cargo, @abono, @mon, @tc, @gld, @cargoS, @abonoS,
                                 @fechaDet, @usuarioTxt;
END

CLOSE cur_det;
DEALLOCATE cur_det;

PRINT 'Filas de auditoria de detalle agregadas: ' + CAST(@filasDet AS varchar(10));

/* ---------------------------------------------------------------------------
   4. Documentos asociados: el ERP graba fecha del dia, tipo 0 y textos vacios;
      los comprobantes viejos del worker quedaron con NULL. Se igualan.
--------------------------------------------------------------------------- */
UPDATE d
SET d.FechaDocAsoc    = CAST(f.mfb_fecha_creacion AS date),
    d.NserieDocAsoc   = '',
    d.TipoDocAsoc     = 0,
    d.FolioDocAsoc    = '',
    d.FechaDocAsoc82  = CAST(f.mfb_fecha_creacion AS date),
    d.NserieDocAsoc82 = '',
    d.TipoDocAsoc82   = 0,
    d.FolioDocAsoc82  = ''
FROM mae_factura_boleta_documentos_asociados d
JOIN mae_factura_boleta f ON f.mfb_cod_factura_boleta = d.mfb_cod_factura_boleta
JOIN rg_gasto g ON g.rgg_cod_factura_boleta = f.mfb_cod_factura_boleta
WHERE d.FechaDocAsoc IS NULL;

PRINT 'Filas de documentos asociados normalizadas: ' + CAST(@@ROWCOUNT AS varchar(10));

/* ---------------------------------------------------------------------------
   5. Verificacion: lineas contra filas de auditoria. Deben coincidir.
--------------------------------------------------------------------------- */
PRINT '';
PRINT '=== Verificacion (aud_cab debe ser 1 y aud_det igual a lineas) ===';

SELECT g.rgg_id                       AS id_rindegastos,
       g.rgg_nro_documento            AS folio,
       g.rgg_cod_factura_boleta       AS cod_factura,
       g.rgg_cod_comprobante          AS comprobante,
       (SELECT COUNT(*) FROM mae_detalle_comprobante_contable d
        WHERE d.mdco_cod_comprobante_contable = g.rgg_cod_comprobante)      AS lineas,
       (SELECT COUNT(*) FROM tran_auditoria_comprobante_contable a
        WHERE a.tac_nro_cod_comprobante = g.rgg_cod_comprobante)            AS aud_cab,
       (SELECT COUNT(*) FROM tran_auditoria_detalle_comprobante_trigger t
        WHERE t.tacc_cod_comprobante_contable = g.rgg_cod_comprobante)      AS aud_det
FROM rg_gasto g
WHERE g.rgg_cod_comprobante IS NOT NULL
ORDER BY g.rgg_cod_comprobante;
