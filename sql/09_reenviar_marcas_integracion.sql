/* =============================================================================
   Reenviar las marcas de integracion a Rindegastos.

   Hasta el 17/09/2026 el worker mandaba el cuerpo de los metodos
   set...IntegrationBulk como una lista suelta:
       [ {"Id":...}, {"Id":...} ]
   y la API espera un objeto con la lista adentro:
       {"Expenses":[...]}  {"ExpenseReports":[...]}  {"Funds":[...]}  {"FundsRequest":[...]}

   La API contestaba HTTP 200 con el error adentro ("property 0 should not
   exist", "statusCode":400), el worker lo tomaba como exito y dejaba la fila en
   estado 3 CONFIRMADO. En Rindegastos nada quedo marcado como integrado.

   Este script devuelve a estado 2 CONTABILIZADO todo lo que tiene comprobante
   y figura como confirmado, para que el proximo ciclo vuelva a mandar la marca
   (ya con el cuerpo correcto). NO toca ninguna tabla del ERP ni crea
   comprobantes: el paso de confirmacion solo llama a la API.

   Ejecutar con -I:  sqlcmd ... -I -i 09_reenviar_marcas_integracion.sql
   ============================================================================= */

SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

DECLARE @n INT;

UPDATE dbo.rg_gasto
SET rgg_estado = 2
WHERE rgg_estado = 3 AND rgg_cod_comprobante IS NOT NULL;
SET @n = @@ROWCOUNT;
PRINT CONCAT('rg_gasto            -> estado 2: ', @n);

UPDATE dbo.rg_informe
SET rgi_estado = 2
WHERE rgi_estado = 3 AND rgi_cod_comprobante IS NOT NULL;
SET @n = @@ROWCOUNT;
PRINT CONCAT('rg_informe          -> estado 2: ', @n);

UPDATE dbo.rg_fondo
SET rgf_estado = 2
WHERE rgf_estado = 3 AND rgf_cod_comprobante IS NOT NULL;
SET @n = @@ROWCOUNT;
PRINT CONCAT('rg_fondo            -> estado 2: ', @n);

UPDATE dbo.rg_solicitud_fondo
SET rgs_estado = 2
WHERE rgs_estado = 3 AND rgs_cod_comprobante IS NOT NULL;
SET @n = @@ROWCOUNT;
PRINT CONCAT('rg_solicitud_fondo  -> estado 2: ', @n);
