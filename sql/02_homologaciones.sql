/* =============================================================================
   INTEGRACION RINDEGASTOS -> ERP ICS
   Script 02: Carga inicial de equivalencias.

   Todos los valores fueron verificados contra la BD bd_epysa_peru.
   ============================================================================= */

SET NOCOUNT ON;
GO

/* -----------------------------------------------------------------------------
   TIPO DE DOCUMENTO
   Rindegastos manda ExtraFields[Name='Tipo de Documento'].Code
   El ERP usa ref_tipo_documento_contable.rtdc_cod_tipo_documento_contable,
   que se relaciona con el codigo SUNAT en rtdc_cod_sunat.

   OJO: Rindegastos usa "R1" para Recibo por Honorarios, no el codigo SUNAT "02".
   ----------------------------------------------------------------------------- */
MERGE dbo.rg_homologacion AS d
USING (VALUES
    ('TIPO_DOCUMENTO', '01', '1',  'Factura -> FACTURA (SUNAT 01)'),
    ('TIPO_DOCUMENTO', '03', '14', 'Boleta de Venta -> BOLETA DE VENTA (SUNAT 03)'),
    ('TIPO_DOCUMENTO', 'R1', '15', 'Recibo por Honorarios -> RECIBO DE HONORARIOS BH (SUNAT 02)'),
    ('TIPO_DOCUMENTO', '02', '15', 'Recibo por Honorarios (codigo SUNAT directo)'),
    ('TIPO_DOCUMENTO', '07', '6',  'Nota de Credito -> NOTA DE CREDITO CONTABLE'),
    ('TIPO_DOCUMENTO', '08', '13', 'Nota de Debito -> NOTA DEBITO'),
    ('TIPO_DOCUMENTO', '12', '52', 'Ticket -> TICKET'),
    ('TIPO_DOCUMENTO', '14', '51', 'Recibo Servicios Publicos')
) AS o (tipo, val_rg, val_erp, glosa)
ON  d.rgh_tipo = o.tipo AND d.rgh_valor_rg = o.val_rg
WHEN NOT MATCHED THEN
    INSERT (rgh_tipo, rgh_valor_rg, rgh_valor_erp, rgh_glosa)
    VALUES (o.tipo, o.val_rg, o.val_erp, o.glosa);
GO


/* -----------------------------------------------------------------------------
   MONEDA
   ref_moneda ya tiene la columna rmo_iso, asi que la equivalencia es directa.
   Se carga igual en la tabla para poder ajustarla sin tocar el catalogo del ERP.
   ----------------------------------------------------------------------------- */
MERGE dbo.rg_homologacion AS d
USING (VALUES
    ('MONEDA', 'PEN', '1', 'Soles'),
    ('MONEDA', 'USD', '2', 'Dolares'),
    ('MONEDA', 'EUR', '7', 'Euros'),
    ('MONEDA', 'CLP', '8', 'Peso Chileno')
) AS o (tipo, val_rg, val_erp, glosa)
ON  d.rgh_tipo = o.tipo AND d.rgh_valor_rg = o.val_rg
WHEN NOT MATCHED THEN
    INSERT (rgh_tipo, rgh_valor_rg, rgh_valor_erp, rgh_glosa)
    VALUES (o.tipo, o.val_rg, o.val_erp, o.glosa);
GO


/* -----------------------------------------------------------------------------
   CENTRO DE COSTO
   Verificado: el Code que manda Rindegastos YA es el rcc_cod_centro_costo.
       Code 3   -> TIENDA CALLAO
       Code 87  -> TIENDA CHICLAYO
       Code 229 -> TIENDA PIURA
   Por eso NO se cargan filas: el worker usa el Code directamente.
   Si en el futuro algun centro no coincide, se agrega aqui una fila
   con rgh_tipo = 'CENTRO_COSTO' y esa excepcion tiene prioridad.
   ----------------------------------------------------------------------------- */


/* -----------------------------------------------------------------------------
   CUENTA CONTABLE
   Verificado: CategoryCode de Rindegastos YA es el mpc_codigo_cuenta del ERP
   (6561070, 6251010, 6399010 existen en mae_plan_cuenta).
   Por eso NO se cargan filas: el worker busca la cuenta por CategoryCode.
   Las excepciones se agregan aqui con rgh_tipo = 'CUENTA'.
   ----------------------------------------------------------------------------- */


/* -----------------------------------------------------------------------------
   VERIFICACION: categorias de Rindegastos sin cuenta contable en el ERP.
   Ejecutar despues de la primera descarga para detectar faltantes.
   ----------------------------------------------------------------------------- */
/*
SELECT DISTINCT g.rgg_category_code, g.rgg_category, COUNT(*) AS gastos
FROM dbo.rg_gasto g
LEFT JOIN dbo.mae_plan_cuenta pc ON pc.mpc_codigo_cuenta = g.rgg_category_code
LEFT JOIN dbo.rg_homologacion h  ON h.rgh_tipo = 'CUENTA' AND h.rgh_valor_rg = g.rgg_category_code
WHERE pc.mpc_cod_plan_cuenta IS NULL AND h.rgh_id IS NULL
GROUP BY g.rgg_category_code, g.rgg_category;
*/

SELECT rgh_tipo, rgh_valor_rg, rgh_valor_erp, rgh_glosa
FROM dbo.rg_homologacion
ORDER BY rgh_tipo, rgh_valor_rg;
GO


--=========PARA CREAR EL USUARIO DE RINDEGASTOS 

DECLARE @usuario     VARCHAR(50)  = 'rindegastos';
DECLARE @nombre      VARCHAR(200) = 'INTEGRACION RINDEGASTOS';
DECLARE @perfil      INT          = 173;  -- ref_perfil 173 = ASISTENTE CONTABLE
DECLARE @sucursal    TINYINT      = 1;    -- ref_sucursal 1 = Tienda Callao

INSERT INTO dbo.mae_usuario
(
    mus_nombre, mus_nom_corto,
    mus_cod_perfil,           -- ASISTENTE CONTABLE
    mus_cod_empleado,         -- NULL: no es una persona
    mus_cod_sucursal_origen,
    mus_usuario,              -- login (que nunca se va a usar)
    mus_password,             -- NULL: no puede iniciar sesion
    mus_pass_cripto,          -- NULL: idem
    mus_vigente, mus_nivel_acceso, mus_fuera_horario,
    mus_fec_creacion, mus_email, mus_acceso_hua, mus_tipo_ics
)
VALUES
( @nombre, 'RINDEGASTOS', @perfil, NULL, @sucursal, @usuario,
  NULL, NULL, 1, 1, 1, GETDATE(), NULL, 0, 1 );