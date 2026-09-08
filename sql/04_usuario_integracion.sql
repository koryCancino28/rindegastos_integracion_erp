/* =============================================================================
   INTEGRACION RINDEGASTOS -> ERP ICS
   Script 04: Crea el usuario con el que el worker graba los documentos.

   POR QUE UN USUARIO PROPIO
   Cada factura guarda en mfb_cod_usuario quien la registro. Si usaramos el
   codigo de una persona, los asientos automaticos quedarian a su nombre y en
   una auditoria no se distinguiria lo que hizo ella de lo que hizo el worker.
   Con un usuario propio basta un WHERE para separar todo lo automatico.

   ESTE USUARIO NO PUEDE INICIAR SESION
   Se crea SIN CONTRASENA (mus_password y mus_pass_cripto en NULL), asi que el
   perfil no le abre ningun acceso real: solo existe para que su codigo quede
   grabado en los documentos.

   Ejecutar UNA sola vez. Si se corre de nuevo, detecta que ya existe.
   ============================================================================= */

SET NOCOUNT ON;
GO

DECLARE @usuario      VARCHAR(50)  = 'rindegastos';
DECLARE @nombre       VARCHAR(200) = 'INTEGRACION RINDEGASTOS';
DECLARE @nombreCorto  VARCHAR(50)  = 'RINDEGASTOS';
DECLARE @perfil       INT          = 173;  -- ref_perfil 173 = ASISTENTE CONTABLE
DECLARE @sucursal     TINYINT      = 1;    -- ref_sucursal 1 = Tienda Callao
DECLARE @codGenerado  INT;

/* ---- 1. Validaciones previas ------------------------------------------------ */

IF EXISTS (SELECT 1 FROM dbo.mae_usuario WHERE mus_usuario = @usuario)
BEGIN
    SELECT @codGenerado = mus_cod_usuario FROM dbo.mae_usuario WHERE mus_usuario = @usuario;
    PRINT 'El usuario ya existe. No se crea de nuevo.';
    PRINT 'mus_cod_usuario = ' + CAST(@codGenerado AS VARCHAR(10));
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM dbo.ref_perfil WHERE ref_cod_perfil = @perfil)
BEGIN
    RAISERROR('El perfil %d no existe en ref_perfil.', 16, 1, @perfil);
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM dbo.ref_sucursal WHERE rsu_cod_sucursal = @sucursal)
BEGIN
    RAISERROR('La sucursal %d no existe en ref_sucursal.', 16, 1, @sucursal);
    RETURN;
END

/* ---- 2. Creacion ------------------------------------------------------------ */

BEGIN TRY
    BEGIN TRANSACTION;

    INSERT INTO dbo.mae_usuario
    (
        mus_nombre,               -- nombre que se ve en el ERP
        mus_nom_corto,
        mus_cod_perfil,           -- ASISTENTE CONTABLE
        mus_cod_empleado,         -- NULL: no es una persona
        mus_cod_sucursal_origen,
        mus_usuario,              -- login (que nunca se va a usar)
        mus_password,             -- NULL: no puede iniciar sesion
        mus_pass_cripto,          -- NULL: idem
        mus_vigente,
        mus_nivel_acceso,         -- 1 = el mas bajo
        mus_fuera_horario,        -- 1 = puede grabar a cualquier hora
        mus_fec_creacion,
        mus_email,
        mus_acceso_hua,
        mus_tipo_ics
    )
    VALUES
    (
        @nombre,
        @nombreCorto,
        @perfil,
        NULL,
        @sucursal,
        @usuario,
        NULL,
        NULL,
        1,
        1,
        1,
        GETDATE(),
        NULL,
        0,
        1
    );

    SET @codGenerado = SCOPE_IDENTITY();

    COMMIT TRANSACTION;

    PRINT '';
    PRINT '=========================================================';
    PRINT ' Usuario creado correctamente.';
    PRINT '';
    PRINT ' mus_cod_usuario = ' + CAST(@codGenerado AS VARCHAR(10));
    PRINT '';
    PRINT ' Copia ese numero en appsettings.json:';
    PRINT '     "Integracion": { "CodUsuarioErp": ' + CAST(@codGenerado AS VARCHAR(10)) + ' }';
    PRINT '=========================================================';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'Error al crear el usuario:';
    PRINT ERROR_MESSAGE();
END CATCH
GO


/* ---- 3. Verificacion -------------------------------------------------------- */

SELECT
    mus_cod_usuario   AS codigo_para_appsettings,
    mus_usuario       AS login,
    mus_nombre        AS nombre,
    mus_cod_perfil    AS perfil,
    mus_vigente       AS vigente,
    CASE WHEN mus_password IS NULL AND mus_pass_cripto IS NULL
         THEN 'OK: no puede iniciar sesion'
         ELSE 'ATENCION: tiene contrasena' END AS seguridad
FROM dbo.mae_usuario
WHERE mus_usuario = 'rindegastos';
GO


/* =============================================================================
   CONSULTAS UTILES DESPUES
   ============================================================================= */

/* Todo lo que genero la integracion (reemplaza NNN por el codigo obtenido)

SELECT mfb_cod_factura_boleta, mfb_folio, mfb_fecha_creacion, mfb_monto_total, mfb_cod_referencia
FROM dbo.mae_factura_boleta
WHERE mfb_cod_usuario = NNN
ORDER BY mfb_cod_factura_boleta DESC;
*/

/* Comparar: cuanto se sigue haciendo a mano vs cuanto ya es automatico

SELECT CASE WHEN mfb_cod_usuario = NNN THEN 'Automatico (Rindegastos)' ELSE 'Manual' END AS origen,
       COUNT(*) AS facturas, SUM(mfb_monto_total) AS monto
FROM dbo.mae_factura_boleta
WHERE mfb_fecha_creacion >= DATEADD(MONTH, -1, GETDATE())
GROUP BY CASE WHEN mfb_cod_usuario = NNN THEN 'Automatico (Rindegastos)' ELSE 'Manual' END;
*/

/* DESACTIVAR el usuario (por ejemplo, para detener la integracion sin desinstalar
   el worker). Al quedar no vigente, el ERP lo rechaza.

UPDATE dbo.mae_usuario SET mus_vigente = 0, mus_fec_baja = GETDATE()
WHERE mus_usuario = 'rindegastos';
*/
