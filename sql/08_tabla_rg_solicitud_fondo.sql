/* =============================================================================
   rg_solicitud_fondo: staging del CUARTO flujo, las solicitudes de fondo.

   Alguien pide dinero por adelantado (viaticos o entrega a rendir) y, cuando la
   solicitud se aprueba, se le transfiere. En el ERP es un comprobante de
   transferencia (tipo 19) cargado a entregas a rendir.

   El Id de la solicitud NO es numerico: es un identificador de 24 caracteres
   ("6aab1181b618ece4f364f50b").

   Estados, los mismos que las otras tablas:
     0 DESCARGADO   1 HOMOLOGADO   2 CONTABILIZADO   3 CONFIRMADO   9 ERROR

   Ejecutar con:  sqlcmd -S 172.16.21.27 -d bd_epysa_peru -U admecsperu -P *** -I -i 08_tabla_rg_solicitud_fondo.sql
   ============================================================================= */

SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('dbo.rg_solicitud_fondo', 'U') IS NOT NULL
BEGIN
    PRINT 'La tabla dbo.rg_solicitud_fondo ya existe: no se toca.';
    RETURN;
END
GO

CREATE TABLE dbo.rg_solicitud_fondo
(
    rgs_id                  VARCHAR(40)    NOT NULL,   -- Id de la solicitud en Rindegastos
    rgs_titulo              VARCHAR(300)   NULL,
    rgs_descripcion         VARCHAR(300)   NULL,
    rgs_politica            VARCHAR(200)   NULL,       -- PolicyName
    rgs_tipo_rendicion      VARCHAR(100)   NULL,       -- campo extra "Tipo de rendicion"
    rgs_dni                 VARCHAR(30)    NULL,       -- campo extra "DNI"
    rgs_empleado_nombre     VARCHAR(200)   NULL,
    rgs_moneda              VARCHAR(10)    NULL,
    rgs_monto               DECIMAL(18,2)  NOT NULL,
    rgs_fecha_aprobacion    DATE           NULL,
    rgs_id_fondo            BIGINT         NULL,       -- fondo que creo Rindegastos al aprobarla

    rgs_json                NVARCHAR(MAX)  NULL,

    rgs_estado              TINYINT        NOT NULL CONSTRAINT DF_rg_solicitud_estado   DEFAULT (0),
    rgs_intentos            INT            NOT NULL CONSTRAINT DF_rg_solicitud_intentos DEFAULT (0),
    rgs_ultimo_error        NVARCHAR(2000) NULL,

    rgs_cuenta              VARCHAR(20)    NULL,
    rgs_cod_analisis        BIGINT         NULL,
    rgs_nombre_corto        VARCHAR(100)   NULL,

    rgs_cod_comprobante     INT            NULL,
    rgs_folio_comprobante   INT            NULL,

    rgs_fecha_descarga      DATETIME       NOT NULL CONSTRAINT DF_rg_solicitud_descarga DEFAULT (GETDATE()),
    rgs_fecha_contabilizado DATETIME       NULL,
    rgs_fecha_confirmado    DATETIME       NULL,

    CONSTRAINT PK_rg_solicitud_fondo PRIMARY KEY CLUSTERED (rgs_id)
);
GO

CREATE UNIQUE INDEX UX_rg_solicitud_comprobante
    ON dbo.rg_solicitud_fondo (rgs_cod_comprobante)
    WHERE rgs_cod_comprobante IS NOT NULL;
GO

CREATE INDEX IX_rg_solicitud_estado ON dbo.rg_solicitud_fondo (rgs_estado);
GO

PRINT 'Tabla dbo.rg_solicitud_fondo creada.';
GO
