/* ============================================================================
   06_tabla_rg_informe.sql
   ----------------------------------------------------------------------------
   Tabla de trabajo para el segundo flujo: las RENDICIONES (planilla de movilidad)
   que entran por el modulo Contabilidad > Comprobantes > Ingreso de Comprobante.

   POR QUE UNA TABLA APARTE
   ------------------------
   El primer flujo trabaja gasto por gasto: cada gasto es una factura y un
   comprobante. En las rendiciones la unidad es el INFORME: un informe agrupa
   varios gastos y genera UN solo comprobante, con una linea por gasto mas una
   linea de contrapartida.

   Por eso rg_gasto no sirve aqui: la llave es el informe, no el gasto.

   QUE NO HACE
   -----------
   No toca ninguna tabla del ERP. Solo crea rg_informe en el mismo esquema donde
   ya viven rg_gasto, rg_homologacion y rg_log_api.

   Se puede ejecutar varias veces sin problema.
============================================================================ */

SET NOCOUNT ON;

IF OBJECT_ID('dbo.rg_informe', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.rg_informe
    (
        -- Id del informe en Rindegastos. Es la barrera anti duplicados:
        -- un informe ya descargado nunca se vuelve a procesar.
        rgi_id                  BIGINT       NOT NULL PRIMARY KEY,

        -- ---- Datos tal como llegan de la API --------------------------------
        rgi_titulo              VARCHAR(300) NULL,
        rgi_numero_informe      VARCHAR(30)  NULL,
        rgi_fecha_envio         DATE         NULL,
        rgi_empleado_id         BIGINT       NULL,
        rgi_empleado_nombre     VARCHAR(200) NULL,
        rgi_tipo_rendicion      VARCHAR(100) NULL,   -- texto crudo: "Viáticos ", "Caja Chica"...
        rgi_centro_costo_code   VARCHAR(20)  NULL,
        rgi_fondo_id            BIGINT       NULL,   -- solo en caja chica
        rgi_fondo_nombre        VARCHAR(200) NULL,
        rgi_total               DECIMAL(18,2) NULL,
        rgi_moneda              VARCHAR(10)  NULL,
        rgi_cantidad_gastos     INT          NULL,

        -- JSON completo del informe y de sus gastos, por si hay que revisar
        -- que fue exactamente lo que mando la plataforma.
        rgi_json                NVARCHAR(MAX) NULL,
        rgi_json_gastos         NVARCHAR(MAX) NULL,

        -- ---- Resultado de la homologacion -----------------------------------
        rgi_cod_analisis        BIGINT       NULL,   -- la persona que rinde
        rgi_documento_empleado  VARCHAR(20)  NULL,
        rgi_tipo_comprobante    SMALLINT     NULL,   -- 17 Diario / 2 Caja Egreso
        rgi_cuenta_contrapartida VARCHAR(20) NULL,
        rgi_numero_documento    VARCHAR(20)  NULL,   -- calculado, no viene de la API
        rgi_fecha_vencimiento   DATE         NULL,

        -- ---- Maquina de estados ---------------------------------------------
        --   0 DESCARGADO  1 HOMOLOGADO  2 CONTABILIZADO  3 CONFIRMADO  9 ERROR
        rgi_estado              TINYINT      NOT NULL CONSTRAINT DF_rg_informe_estado DEFAULT (0),
        rgi_cod_comprobante     INT          NULL,
        rgi_folio_comprobante   INT          NULL,

        rgi_fecha_descarga      DATETIME     NOT NULL CONSTRAINT DF_rg_informe_descarga DEFAULT (GETDATE()),
        rgi_fecha_contabilizado DATETIME     NULL,
        rgi_fecha_confirmado    DATETIME     NULL,
        rgi_intentos            INT          NOT NULL CONSTRAINT DF_rg_informe_intentos DEFAULT (0),
        rgi_ultimo_error        NVARCHAR(2000) NULL
    );

    PRINT 'Tabla rg_informe creada.';
END
ELSE
    PRINT 'Tabla rg_informe ya existia. No se modifico.';
GO

/* Un comprobante del ERP solo puede venir de un informe. Es la ultima barrera
   contra duplicados, la misma idea que el indice de rg_gasto.

   El SET de abajo hace falta porque este es un indice filtrado (tiene WHERE) y
   SQL Server los rechaza si QUOTED_IDENTIFIER esta apagado, como viene por
   defecto al ejecutar con sqlcmd. */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_rg_informe_comprobante')
BEGIN
    CREATE UNIQUE INDEX UX_rg_informe_comprobante
        ON dbo.rg_informe (rgi_cod_comprobante)
        WHERE rgi_cod_comprobante IS NOT NULL;

    PRINT 'Indice unico UX_rg_informe_comprobante creado.';
END
GO

/* Para las consultas del dia a dia, que siempre filtran por estado. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_rg_informe_estado')
BEGIN
    CREATE INDEX IX_rg_informe_estado ON dbo.rg_informe (rgi_estado) INCLUDE (rgi_id);
    PRINT 'Indice IX_rg_informe_estado creado.';
END
GO

PRINT '';
PRINT 'Listo. Los estados son:';
PRINT '  0 DESCARGADO     el informe esta guardado, contabilidad no fue tocada';
PRINT '  1 HOMOLOGADO     todos los codigos del ERP resolvieron bien';
PRINT '  2 CONTABILIZADO  el comprobante ya existe en el ERP';
PRINT '  3 CONFIRMADO     Rindegastos acepto la marca de integracion';
PRINT '  9 ERROR          requiere revision manual';
