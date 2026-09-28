/* =============================================================================
   INTEGRACION RINDEGASTOS -> ERP ICS
   Script 01: Tablas de staging, homologacion y log.

   Base de datos: bd_epysa_peru  (servidor 172.16.21.27)

   Estas tablas son NUEVAS. No modifican ninguna tabla existente del ERP.
   Prefijo "rg_" = Rindegastos.
   ============================================================================= */

SET NOCOUNT ON;
GO

/* -----------------------------------------------------------------------------
   rg_gasto : copia fiel de cada gasto que llega de /getExpenses.
   Un gasto de Rindegastos = una factura de compra + un comprobante en el ERP.
   ----------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.rg_gasto') IS NULL
BEGIN
    CREATE TABLE dbo.rg_gasto
    (
        /* --- Identidad (PK = Id de Rindegastos: garantiza no duplicar) --- */
        rgg_id                  BIGINT        NOT NULL,

        /* --- Datos tal como llegan de la API --- */
        rgg_report_id           BIGINT        NULL,      -- ReportId
        rgg_user_id             BIGINT        NULL,      -- UserId
        rgg_policy_id           BIGINT        NULL,      -- ExpensePolicyId
        rgg_status              TINYINT       NULL,      -- 0=En proceso 1=Aprobado 2=Rechazado
        rgg_supplier            VARCHAR(255)  NULL,      -- Supplier (texto libre)
        rgg_issue_date          DATE          NULL,      -- IssueDate
        rgg_total               DECIMAL(18,4) NULL,      -- Total
        rgg_net_api             DECIMAL(18,4) NULL,      -- Net (informativo: NO se usa para contabilizar)
        rgg_tax_api             DECIMAL(18,4) NULL,      -- Taxes.tax (informativo)
        rgg_tax_percentage      DECIMAL(9,4)  NULL,      -- Taxes.taxPercentage
        rgg_currency            CHAR(3)       NULL,      -- Currency (ISO 4217)
        rgg_exchange_rate       DECIMAL(18,6) NULL,      -- ExchangeRate (llega 0: se recalcula)
        rgg_category            VARCHAR(255)  NULL,      -- Category
        rgg_category_code       VARCHAR(50)   NULL,      -- CategoryCode = cuenta contable de gasto
        rgg_note                VARCHAR(500)  NULL,      -- Note -> glosa del detalle
        rgg_reimbursable        BIT           NULL,

        /* --- Campos extra de la politica (ExtraFields) --- */
        rgg_ruc_proveedor       VARCHAR(20)   NULL,      -- ExtraField "Ruc Proveedor"
        rgg_tipo_doc_code       VARCHAR(10)   NULL,      -- ExtraField "Tipo de Documento".Code  (01/03/R1)
        rgg_tipo_doc_nombre     VARCHAR(100)  NULL,      -- ExtraField "Tipo de Documento".Value
        rgg_nro_documento       VARCHAR(50)   NULL,      -- ExtraField "Nro Documento" -> folio
        rgg_centro_costo_code   VARCHAR(20)   NULL,      -- ExtraField "Centro de Costos 1".Code
        rgg_centro_costo_nombre VARCHAR(150)  NULL,

        /* --- SUNAT (respaldo del RUC y razon social) --- */
        rgg_sunat_ruc           VARCHAR(20)   NULL,      -- SunatInfo.Ruc
        rgg_sunat_razon_social  VARCHAR(255)  NULL,      -- SunatInfo.BusinessName
        rgg_sunat_doc_estado    VARCHAR(150)  NULL,      -- SunatInfo.DocStatusName

        /* --- Payload completo: fuente de verdad para auditoria --- */
        rgg_json                NVARCHAR(MAX) NULL,

        /* --- Valores resueltos por la homologacion --- */
        rgg_cod_proveedor       INT           NULL,      -- mae_proveedor.mpr_cod_proveedor
        rgg_cod_analisis        BIGINT        NULL,      -- tran_analisis.tan_cod_analisis
        rgg_cod_tipo_doc        SMALLINT      NULL,      -- ref_tipo_documento_contable.rtdc_cod_tipo_documento_contable
        rgg_cod_moneda          TINYINT       NULL,      -- ref_moneda.rmo_cod_moneda
        rgg_tipo_cambio         DECIMAL(18,4) NULL,      -- tran_tipo_cambio o 1 si es PEN
        rgg_cod_centro_costo    SMALLINT      NULL,      -- ref_centro_costo.rcc_cod_centro_costo
        rgg_cod_plan_cuenta     INT           NULL,      -- mae_plan_cuenta.mpc_cod_plan_cuenta (cuenta de gasto)

        /* --- Montos calculados segun el tipo de documento --- */
        rgg_monto_neto          DECIMAL(18,4) NULL,      -- solo Factura
        rgg_monto_igv           DECIMAL(18,4) NULL,      -- solo Factura
        rgg_monto_exento        DECIMAL(18,4) NULL,      -- Boleta y Recibo por Honorarios

        /* --- Control del proceso --- */
        rgg_estado              TINYINT       NOT NULL CONSTRAINT DF_rg_gasto_estado DEFAULT (0),
        rgg_cod_factura_boleta  INT           NULL,      -- mae_factura_boleta.mfb_cod_factura_boleta
        rgg_cod_comprobante     INT           NULL,      -- mae_comprobante_contable.mcm_cod_comprobante_contable
        rgg_fecha_descarga      DATETIME      NOT NULL CONSTRAINT DF_rg_gasto_fdesc DEFAULT (GETDATE()),
        rgg_fecha_contabilizado DATETIME      NULL,
        rgg_fecha_confirmado    DATETIME      NULL,      -- cuando Rindegastos acepto la marca
        rgg_intentos            INT           NOT NULL CONSTRAINT DF_rg_gasto_int DEFAULT (0),
        rgg_ultimo_error        NVARCHAR(2000) NULL,

        CONSTRAINT PK_rg_gasto PRIMARY KEY CLUSTERED (rgg_id)
    );

    CREATE INDEX IX_rg_gasto_estado   ON dbo.rg_gasto (rgg_estado) INCLUDE (rgg_cod_comprobante);
    CREATE INDEX IX_rg_gasto_reporte  ON dbo.rg_gasto (rgg_report_id);
    /* Un comprobante del ERP solo puede provenir de un gasto de Rindegastos */
    CREATE UNIQUE INDEX UX_rg_gasto_comprobante ON dbo.rg_gasto (rgg_cod_comprobante)
        WHERE rgg_cod_comprobante IS NOT NULL;

    PRINT 'Tabla rg_gasto creada.';
END
ELSE PRINT 'Tabla rg_gasto ya existe.';
GO


/* -----------------------------------------------------------------------------
   rg_homologacion : equivalencias entre valores de Rindegastos y codigos del ERP.
   Contabilidad la mantiene sin tocar codigo.
   ----------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.rg_homologacion') IS NULL
BEGIN
    CREATE TABLE dbo.rg_homologacion
    (
        rgh_id        INT IDENTITY(1,1) NOT NULL,
        rgh_tipo      VARCHAR(30)  NOT NULL,   -- TIPO_DOCUMENTO | MONEDA | CENTRO_COSTO | CUENTA
        rgh_valor_rg  VARCHAR(255) NOT NULL,   -- valor tal como llega de Rindegastos
        rgh_valor_erp VARCHAR(255) NOT NULL,   -- codigo equivalente en el ERP
        rgh_glosa     VARCHAR(255) NULL,       -- descripcion para el usuario
        rgh_vigente   BIT NOT NULL CONSTRAINT DF_rg_homol_vig DEFAULT (1),
        CONSTRAINT PK_rg_homologacion PRIMARY KEY CLUSTERED (rgh_id),
        CONSTRAINT UQ_rg_homologacion UNIQUE (rgh_tipo, rgh_valor_rg)
    );
    PRINT 'Tabla rg_homologacion creada.';
END
ELSE PRINT 'Tabla rg_homologacion ya existe.';
GO


/* -----------------------------------------------------------------------------
   rg_log_api : bitacora de todas las llamadas a la API.
   ----------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.rg_log_api') IS NULL
BEGIN
    CREATE TABLE dbo.rg_log_api
    (
        rgl_id          BIGINT IDENTITY(1,1) NOT NULL,
        rgl_fecha       DATETIME     NOT NULL CONSTRAINT DF_rg_log_fecha DEFAULT (GETDATE()),
        rgl_metodo      VARCHAR(60)  NOT NULL,   -- getExpenses, setExpenseIntegrationBulk...
        rgl_http_status INT          NULL,
        rgl_ms          INT          NULL,
        rgl_ok          BIT          NOT NULL,
        rgl_request     NVARCHAR(MAX) NULL,
        rgl_response    NVARCHAR(MAX) NULL,
        CONSTRAINT PK_rg_log_api PRIMARY KEY CLUSTERED (rgl_id)
    );
    CREATE INDEX IX_rg_log_fecha ON dbo.rg_log_api (rgl_fecha DESC);
    PRINT 'Tabla rg_log_api creada.';
END
ELSE PRINT 'Tabla rg_log_api ya existe.';
GO

/* -----------------------------------------------------------------------------
   ESTADOS DE rgg_estado
     0 = DESCARGADO    el JSON esta en staging, no se toco contabilidad
     1 = HOMOLOGADO    todos los codigos resolvieron correctamente
     2 = CONTABILIZADO existe el comprobante en el ERP (commit local hecho)
     3 = CONFIRMADO    Rindegastos acepto la marca de integracion  <- fin
     9 = ERROR         requiere intervencion manual
   ----------------------------------------------------------------------------- */
