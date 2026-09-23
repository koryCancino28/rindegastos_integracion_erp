/* =============================================================================
   rg_fondo: staging del TERCER flujo, la entrega de fondos.

   La unidad de trabajo es el DEPOSITO, no el fondo: un fondo puede recibir una
   entrega inicial y despues recargas, y cada una es una transferencia distinta
   en el ERP. Por eso la llave es (fondo, numero de deposito).

   Estados, los mismos que rg_gasto y rg_informe:
     0 DESCARGADO   1 HOMOLOGADO   2 CONTABILIZADO   3 CONFIRMADO   9 ERROR

   Ejecutar con:  sqlcmd -S 172.16.21.27 -d bd_epysa_peru -U admecsperu -P *** -I -i 07_tabla_rg_fondo.sql
   (el -I es necesario por el indice filtrado de mas abajo)
   ============================================================================= */

SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('dbo.rg_fondo', 'U') IS NOT NULL
BEGIN
    PRINT 'La tabla dbo.rg_fondo ya existe: no se toca.';
    RETURN;
END
GO

CREATE TABLE dbo.rg_fondo
(
    rgf_id_fondo            BIGINT         NOT NULL,   -- Id del fondo en Rindegastos
    rgf_deposito            SMALLINT       NOT NULL,   -- 1 la entrega inicial, 2 la primera recarga, etc.

    rgf_titulo              VARCHAR(300)   NULL,       -- Title del fondo
    rgf_code                VARCHAR(30)    NULL,       -- Code: documento de quien recibe
    rgf_descripcion         VARCHAR(200)   NULL,       -- Description: cuenta contable del ERP
    rgf_moneda              VARCHAR(10)    NULL,
    rgf_monto               DECIMAL(18,2)  NOT NULL,
    rgf_fecha_deposito      DATE           NOT NULL,

    rgf_json                NVARCHAR(MAX)  NULL,       -- el fondo tal como llego de la API

    rgf_estado              TINYINT        NOT NULL CONSTRAINT DF_rg_fondo_estado   DEFAULT (0),
    rgf_intentos            INT            NOT NULL CONSTRAINT DF_rg_fondo_intentos DEFAULT (0),
    rgf_ultimo_error        NVARCHAR(2000) NULL,

    -- Lo que resolvio la homologacion
    rgf_cuenta              VARCHAR(20)    NULL,
    rgf_cod_analisis        BIGINT         NULL,
    rgf_nombre_corto        VARCHAR(100)   NULL,

    -- Lo que se creo en el ERP
    rgf_cod_comprobante     INT            NULL,
    rgf_folio_comprobante   INT            NULL,

    rgf_fecha_descarga      DATETIME       NOT NULL CONSTRAINT DF_rg_fondo_descarga DEFAULT (GETDATE()),
    rgf_fecha_contabilizado  DATETIME      NULL,
    rgf_fecha_confirmado     DATETIME      NULL,

    CONSTRAINT PK_rg_fondo PRIMARY KEY CLUSTERED (rgf_id_fondo, rgf_deposito)
);
GO

-- Un comprobante no puede quedar enlazado a dos depositos distintos.
CREATE UNIQUE INDEX UX_rg_fondo_comprobante
    ON dbo.rg_fondo (rgf_cod_comprobante)
    WHERE rgf_cod_comprobante IS NOT NULL;
GO

CREATE INDEX IX_rg_fondo_estado ON dbo.rg_fondo (rgf_estado) INCLUDE (rgf_fecha_deposito);
GO

PRINT 'Tabla dbo.rg_fondo creada.';
GO
