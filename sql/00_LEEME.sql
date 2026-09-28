/* =============================================================================
   INTEGRACION RINDEGASTOS -> ERP ICS
   Script 00: INDICE. Que hace cada archivo de esta carpeta y cuando se ejecuta.

   Este archivo NO se ejecuta: es la guia. Abajo esta la lista completa y, al
   final, una consulta para verificar que la instalacion quedo bien.

   Base de datos: bd_epysa_peru   (QA: servidor 172.16.21.27)

   NINGUN script modifica tablas del ERP. Los que crean cosas solo agregan
   tablas nuevas con prefijo "rg_" (rg = Rindegastos) y un usuario propio para
   que los documentos automaticos se distingan de los que se ingresan a mano.
   Todos son idempotentes: si se corren dos veces, la segunda no hace nada.

   -----------------------------------------------------------------------------
   INSTALACION: se ejecutan UNA VEZ, en este orden
   -----------------------------------------------------------------------------
     01_tablas_rg.sql                 Crea rg_gasto (staging del flujo de gastos),
                                      rg_homologacion (equivalencias) y
                                      rg_log_api (registro de las llamadas a la API).

     02_homologaciones.sql            Carga las equivalencias iniciales: tipo de
                                      documento de Rindegastos -> tipo del ERP, y
                                      moneda. Verificadas contra la BD.

     04_usuario_integracion.sql       Crea el usuario con el que el worker graba.
                                      Sin contrasena: no puede iniciar sesion, solo
                                      existe para quedar en mfb_cod_usuario y en la
                                      auditoria. Su codigo va en appsettings.json
                                      (Integracion:CodUsuarioErp).

     06_tabla_rg_informe.sql          Crea rg_informe: staging del flujo de INFORMES
                                      (rendiciones). La unidad es el informe, no el
                                      gasto.                        (*) necesita -I

     07_tabla_rg_fondo.sql            Crea rg_fondo: staging del flujo de FONDOS
                                      (cajas chicas). La unidad es el deposito.
                                                                    (*) necesita -I

     08_tabla_rg_solicitud_fondo.sql  Crea rg_solicitud_fondo: staging del flujo de
                                      SOLICITUDES de fondo aprobadas.
                                                                    (*) necesita -I

   (*) Esas tres tablas tienen un indice filtrado. SQL Server los rechaza cuando
       QUOTED_IDENTIFIER esta apagado, que es como viene sqlcmd por defecto:

           sqlcmd -S 172.16.21.27 -d bd_epysa_peru -U admecsperu -P *** -I -i 07_tabla_rg_fondo.sql

       Desde SSMS no hace falta: ya lo trae encendido.

   -----------------------------------------------------------------------------
   USO DIARIO: no se ejecutan completos
   -----------------------------------------------------------------------------
     03_consultas_monitoreo.sql       CATALOGO de consultas para revisar como va
                                      todo. Se abre, se elige el bloque y F5.
                                      Todas son de solo lectura menos el bloque J,
                                      que esta marcado. Bloques:

                                        A  tablero de gastos (A3 = los 4 flujos)
                                        B  errores de gastos
                                        C  datos maestros que faltan en el ERP
                                        D  duplicados
                                        E  trazabilidad del gasto al asiento
                                        F  salud del proceso y alertas
                                        G  homologaciones configuradas
                                        H  limpieza del log
                                        J  acciones que MODIFICAN datos
                                        K  reversar un comprobante mal generado
                                        L  informes (rendiciones)
                                        M  fondos
                                        S  solicitudes de fondo

   -----------------------------------------------------------------------------
   ARREGLOS PUNTUALES: se ejecutaron una vez, se dejan como historia
   -----------------------------------------------------------------------------
     05_backfill_auditoria.sql        Relleno la auditoria contable de los
                                      comprobantes que el worker creo ANTES de que
                                      grabara auditoria. Ya ejecutado (03/09/2026).
                                      No hace falta volver a correrlo.

     09_reenviar_marcas_integracion.sql
                                      Devolvio a estado 2 lo que habia quedado como
                                      confirmado sin estarlo, por un error al armar
                                      el cuerpo de los metodos set...IntegrationBulk.
                                      Ya ejecutado (18/09/2026). Sirve de nuevo si
                                      alguna vez hay que forzar el reenvio de las
                                      marcas a Rindegastos.

   ============================================================================= */


/* -----------------------------------------------------------------------------
   VERIFICACION: que la instalacion quedo completa.
   Debe devolver las 6 tablas y el usuario de la integracion.
   ----------------------------------------------------------------------------- */

SELECT 'tablas creadas' AS que_reviso, name AS detalle
FROM sys.tables
WHERE name LIKE 'rg[_]%'

UNION ALL
SELECT 'faltan',
       'FALTA la tabla ' + t.tabla
FROM (VALUES ('rg_gasto'), ('rg_homologacion'), ('rg_log_api'),
             ('rg_informe'), ('rg_fondo'), ('rg_solicitud_fondo')) AS t(tabla)
WHERE NOT EXISTS (SELECT 1 FROM sys.tables s WHERE s.name = t.tabla)

UNION ALL
SELECT 'equivalencias cargadas',
       CAST(COUNT(*) AS varchar(10)) + ' filas en rg_homologacion'
FROM dbo.rg_homologacion

UNION ALL
SELECT 'usuario de la integracion',
       CAST(mus_cod_usuario AS varchar(10)) + ' - ' + LTRIM(RTRIM(mus_nombre))
FROM dbo.mae_usuario
WHERE mus_cod_usuario = 1000000;
